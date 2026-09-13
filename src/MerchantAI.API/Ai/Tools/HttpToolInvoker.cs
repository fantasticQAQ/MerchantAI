using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel;

namespace MerchantAdmin.AI.API.Ai.Tools;

/// <summary>一次工具调用的参数解析+校验结果。</summary>
public sealed record PlanResult(HttpRequestPlan? Plan, string? Error)
{
    public bool Ok => Plan is not null;
    public static PlanResult Success(HttpRequestPlan plan) => new(plan, null);
    public static PlanResult Fail(string error) => new(null, error);
}

/// <summary>
/// 一次工具执行的结果。
/// <paramref name="Delivered"/> 表示「请求是否真的送达了业务服务」——业务层返回失败也算送达。
/// 审批流程靠它判断该不该把待确认操作消费掉：没送达就留着让用户重试，送达了就无论如何都消费掉，避免重复下单。
/// </summary>
public sealed record ToolCallResult(bool Delivered, string Text);

public interface IToolInvoker
{
    /// <summary>把模型给的原始参数解析成一份可重放的 HTTP 请求；校验不过则返回可读的错误。</summary>
    PlanResult BuildPlan(ToolDescriptor descriptor, KernelArguments arguments);

    /// <summary>真正发请求。只读工具直接调；写工具由审批流程在确认后调。</summary>
    Task<ToolCallResult> SendAsync(ToolDescriptor descriptor, HttpRequestPlan plan, CancellationToken ct = default);
}

/// <summary>
/// 通用工具执行器：所有工具共用这一份 HTTP 管道（组 URL / 序列化 / 鉴权 / 超时 / 解包 / 错误映射）。
/// 新增业务能力只需要在 tools.json 加一条，这里的代码一行都不用改。
/// </summary>
public sealed class HttpToolInvoker : IToolInvoker
{
    public const string HttpClientName = "merchant";

    private static readonly Regex PathParamRegex = new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);
    private const int MaxResultChars = 8000;

    /// <summary>不要把中文转义成 \uXXXX：既省 token，模型读起来也更准。统一走 JsonText，别再各写各的。</summary>
    private static readonly JsonSerializerOptions JsonOut = JsonText.Relaxed;

    private readonly IHttpClientFactory _httpFactory;
    private readonly IToolCatalog _catalog;
    private readonly ILogger<HttpToolInvoker> _logger;

    public HttpToolInvoker(IHttpClientFactory httpFactory, IToolCatalog catalog, ILogger<HttpToolInvoker> logger)
    {
        _httpFactory = httpFactory;
        _catalog = catalog;
        _logger = logger;
    }

    // ---------------------------------------------------------------- 参数

    /// <summary>把模型传来的原始 JSON 统一成 JsonNode，并补上 tools.json 里声明的默认值。</summary>
    private static Dictionary<string, JsonNode?> Normalize(ToolDescriptor descriptor, KernelArguments arguments)
    {
        var values = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in descriptor.Parameters)
        {
            JsonNode? node = null;
            if (arguments.TryGetValue(p.Name, out var raw) && raw is not null)
                node = CoerceToSchemaType(ToNode(raw), p.Schema as JsonObject);

            if (node is null && p.Default is not null)
                node = p.Default.DeepClone();

            if (node is not null)
                values[p.Name] = node;
        }

        return values;
    }

    /// <summary>
    /// 按 schema 声明做一次窄范围类型还原。实测模型（DeepSeek）会这样传参：
    /// - 数组/对象整个被序列化成字符串：orderItems = "[{\"productId\":1}]"
    /// - 数字/布尔被写成字符串：page = "1"、isActive = "true"
    /// 只在 schema 明确要求该类型、且字符串确实能解析成该类型时才转换，其他情况原样放过交给校验报错。
    /// </summary>
    private static JsonNode? CoerceToSchemaType(JsonNode? node, JsonObject? schema)
    {
        if (node is not JsonValue value) return node;

        var expected = schema?["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var t) ? t : null;
        if (expected is null) return node;

        if (value.TryGetValue<string>(out var text))
        {
            var trimmed = text.Trim();
            if (trimmed.Length == 0) return node;

            switch (expected)
            {
                case "array" when trimmed.StartsWith('['):
                case "object" when trimmed.StartsWith('{'):
                    try { return JsonNode.Parse(trimmed); }
                    catch (JsonException) { return node; }

                case "integer" when long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLong):
                    return JsonValue.Create(parsedLong);

                case "number" when decimal.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDecimal):
                    return JsonValue.Create(parsedDecimal);

                case "boolean" when bool.TryParse(trimmed, out var parsedBool):
                    return JsonValue.Create(parsedBool);
            }
        }

        return node;
    }

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode n => n.DeepClone(),
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        JsonElement je => JsonNode.Parse(je.GetRawText()),
        string s => JsonValue.Create(s),
        _ => JsonNode.Parse(JsonSerializer.Serialize(value))
    };

    // ---------------------------------------------------------------- 构建请求

    public PlanResult BuildPlan(ToolDescriptor descriptor, KernelArguments arguments)
    {
        var values = Normalize(descriptor, arguments);
        var errors = new List<string>();

        var pathParams = PathParamRegex.Matches(descriptor.Http.Path)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var p in descriptor.Parameters)
        {
            var present = values.TryGetValue(p.Name, out var node) && node is not null;

            // 路径参数等于必填
            var required = p.Required || pathParams.Contains(p.Name);
            if (required && !present)
            {
                errors.Add($"缺少必填参数 {p.Name}" + (string.IsNullOrWhiteSpace(p.Description) ? "" : $"（{p.Description}）"));
                continue;
            }

            if (present)
                errors.AddRange(JsonSchemaLite.Validate(p.Schema, values[p.Name], p.Name));
        }

        if (errors.Count > 0)
            return PlanResult.Fail(
                $"参数校验未通过，未执行任何操作：{string.Join("；", errors)}。" +
                $"请修正后重新调用 {descriptor.Name}。");

        return PlanResult.Success(new HttpRequestPlan
        {
            Method = descriptor.Http.Method,
            Path = BuildPath(descriptor, values),
            Query = BuildQuery(descriptor, values, pathParams),
            Body = descriptor.Request.Body is null ? null : Render(descriptor.Request.Body, values),
            Arguments = values
        });
    }

    private static string BuildPath(ToolDescriptor descriptor, IReadOnlyDictionary<string, JsonNode?> values)
        => PathParamRegex.Replace(descriptor.Http.Path, m =>
            Uri.EscapeDataString(ScalarText(values.GetValueOrDefault(m.Groups[1].Value)) ?? string.Empty));

    private static Dictionary<string, string> BuildQuery(
        ToolDescriptor descriptor,
        IReadOnlyDictionary<string, JsonNode?> values,
        HashSet<string> pathParams)
    {
        // 没显式声明 query 且没有 body 时，默认所有非路径参数都进 querystring
        var names = descriptor.Request.Query.Count > 0
            ? descriptor.Request.Query
            : descriptor.Request.Body is null
                ? descriptor.Parameters.Select(p => p.Name).Where(n => !pathParams.Contains(n)).ToList()
                : new List<string>();

        var query = new Dictionary<string, string>();
        foreach (var name in names)
        {
            if (!values.TryGetValue(name, out var node) || node is null) continue;
            var text = ScalarText(node);
            if (text is null) continue;               // null / 空对象不进 query
            query[name] = text;
        }
        return query;
    }

    /// <summary>
    /// body 模板渲染：
    /// - 字符串 "$param" 整块替换为该参数的原始值
    /// - 字符串 "{param}" 做字符串插值
    /// - 对象 { "$map": "数组参数名", "item": {每个元素的模板} } 把数组逐元素投影成后端契约要求的形状
    /// - 其余原样拷贝
    /// </summary>
    private static JsonNode? Render(JsonNode template, IReadOnlyDictionary<string, JsonNode?> values)
    {
        switch (template)
        {
            // 数组投影：模型只管传它知道的字段，后端契约要求的其它字段由模板补齐
            case JsonObject mapObject
                when mapObject["$map"] is JsonValue mapSource
                     && mapSource.TryGetValue<string>(out var sourceName):

                if (values.GetValueOrDefault(sourceName) is not JsonArray source)
                    return new JsonArray();

                var itemTemplate = mapObject["item"];
                var projected = new JsonArray();

                foreach (var element in source)
                {
                    // 元素字段并入作用域，item 模板里的 {field} / $field 因此指向当前元素
                    var scope = new Dictionary<string, JsonNode?>(values, StringComparer.OrdinalIgnoreCase);
                    if (element is JsonObject elementObject)
                        foreach (var (key, value) in elementObject)
                            scope[key] = value;

                    projected.Add(itemTemplate is null ? element?.DeepClone() : Render(itemTemplate, scope));
                }

                return projected;

            case JsonObject obj:
                var outObj = new JsonObject();
                foreach (var (key, child) in obj)
                    outObj[key] = child is null ? null : Render(child, values);
                return outObj;

            case JsonArray arr:
                var outArr = new JsonArray();
                foreach (var child in arr)
                    outArr.Add(child is null ? null : Render(child, values));
                return outArr;

            case JsonValue val when val.TryGetValue<string>(out var s):
                if (s.StartsWith('$') && s.Length > 1)
                    return values.TryGetValue(s[1..], out var whole) ? whole?.DeepClone() : null;
                if (s.Contains('{'))
                    return JsonValue.Create(Interpolate(s, values));
                return JsonValue.Create(s);

            default:
                return template.DeepClone();
        }
    }

    private static string Interpolate(string template, IReadOnlyDictionary<string, JsonNode?> values)
        => Regex.Replace(template, @"\{([A-Za-z_][A-Za-z0-9_]*)\}", m =>
            ScalarText(values.GetValueOrDefault(m.Groups[1].Value)) ?? string.Empty);

    /// <summary>把 JsonNode 变成适合放进 query/path 的文本；对象/数组退化为 JSON。</summary>
    private static string? ScalarText(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v when v.TryGetValue<decimal>(out var d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue<double>(out var dd) => dd.ToString(System.Globalization.CultureInfo.InvariantCulture),
        JsonObject o when o.Count == 0 => null,
        _ => node.ToJsonString()
    };

    // ---------------------------------------------------------------- 发送

    public async Task<ToolCallResult> SendAsync(ToolDescriptor descriptor, HttpRequestPlan plan, CancellationToken ct = default)
    {
        var client = _httpFactory.CreateClient(HttpClientName);
        var url = plan.Path + (plan.Query.Count == 0
            ? string.Empty
            : "?" + string.Join("&", plan.Query.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}")));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_catalog.TimeoutSeconds));

        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(plan.Method), url);
            if (plan.Body is not null)
                request.Content = new StringContent(plan.Body.ToJsonString(JsonOut), Encoding.UTF8, "application/json");

            _logger.LogInformation("工具 {Tool} -> {Method} {Url}", descriptor.Name, plan.Method, url);

            using var response = await client.SendAsync(request, cts.Token);
            var text = await response.Content.ReadAsStringAsync(cts.Token);

            // 服务器有响应就说明请求已送达（哪怕是 4xx/5xx），审批流程据此决定不再重复执行
            if (!response.IsSuccessStatusCode)
                return new ToolCallResult(true,
                    $"业务接口返回 HTTP {(int)response.StatusCode}。" +
                    (string.IsNullOrWhiteSpace(text) ? string.Empty : Truncate(text)));

            return new ToolCallResult(true, Unwrap(text));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ToolCallResult(false,
                $"调用业务接口超时（超过 {_catalog.TimeoutSeconds} 秒）。请稍后重试，或直接告知用户服务暂时不可用。");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "工具 {Tool} 调用业务接口失败", descriptor.Name);
            return new ToolCallResult(false,
                $"无法连接业务服务（{_catalog.BaseUrl}）：{ex.Message}。请告知用户后台服务可能未启动，不要编造数据。");
        }
    }

    /// <summary>解掉 { code, message, data, success } 这层包装，只把 data 交给模型。</summary>
    private string Unwrap(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "(业务接口返回空响应)";

        var env = _catalog.Response;
        if (env.DataPath is null) return Truncate(json);

        try
        {
            var node = JsonNode.Parse(json);
            if (node is null) return Truncate(json);

            if (env.SuccessPath is not null
                && node[env.SuccessPath] is JsonValue sv
                && sv.TryGetValue<bool>(out var ok)
                && !ok)
            {
                var message = env.MessagePath is not null ? node[env.MessagePath]?.ToString() : null;
                var code = env.CodePath is not null ? node[env.CodePath]?.ToString() : null;
                return $"业务接口返回失败：{message ?? "未知错误"}（code={code}）。请如实告知用户，不要重试同样的参数。";
            }

            var data = node[env.DataPath];
            return data is null ? "(业务接口没有返回数据)" : Truncate(data.ToJsonString(JsonOut));
        }
        catch (JsonException)
        {
            return Truncate(json);
        }
    }

    private static string Truncate(string s)
        => s.Length <= MaxResultChars ? s : s[..MaxResultChars] + $"…（已截断，原长 {s.Length} 字符）";
}
