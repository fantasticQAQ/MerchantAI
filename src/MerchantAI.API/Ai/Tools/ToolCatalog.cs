using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MerchantAdmin.AI.API.Ai.Tools;

public interface IToolCatalog
{
    IReadOnlyList<ToolDescriptor> Tools { get; }
    /// <summary>注册给模型的工具（Read + Write）。Forbidden 的不会出现在这里。</summary>
    IReadOnlyList<ToolDescriptor> Exposed { get; }
    ToolDescriptor? Find(string name);
    ToolDescriptor GetRequired(string name);
    string BaseUrl { get; }
    int TimeoutSeconds { get; }
    ResponseEnvelope Response { get; }
    /// <summary>工具定义文件的绝对路径（便于日志/排错）。</summary>
    string SourcePath { get; }
    /// <summary>swagger 里有、但 tools.json 还没暴露给模型的接口（开发期 DX 用）。</summary>
    IReadOnlyList<OpenApiOperation> UnexposedOperations { get; }
}

/// <summary>
/// 工具目录：tools.json 是「存在哪些工具、叫什么、干什么、是读还是写、怎么落到 HTTP」的唯一事实来源。
/// 消费方（Kernel 注册 / 护栏分级 / 审批摘要 / 审计 / 执行器 / MCP）都从这里取，不再各自硬编码工具名。
/// 工具可以用 "from" 引用 swagger 里的一条操作来省掉路径/参数骨架，但 access 和名字必须人写。
/// </summary>
public sealed class ToolCatalog : IToolCatalog
{
    private static readonly Regex PathParamRegex = new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);
    private static readonly Regex ValidToolNameRegex = new(@"^[A-Za-z_][A-Za-z0-9_-]*$", RegexOptions.Compiled);
    private static readonly HashSet<string> AllowedMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "POST", "PUT", "PATCH", "DELETE" };

    /// <summary>不可变快照。热加载时整体替换，读方永远看到自洽的一份视图。</summary>
    private sealed record Snapshot(
        IReadOnlyList<ToolDescriptor> Tools,
        IReadOnlyList<ToolDescriptor> Exposed,
        Dictionary<string, ToolDescriptor> ByName,
        string BaseUrl,
        string PathPrefix,
        int TimeoutSeconds,
        ResponseEnvelope Response,
        IReadOnlyList<OpenApiOperation> Unexposed);

    private volatile Snapshot _snapshot;

    private ToolCatalog(string sourcePath, Snapshot snapshot)
    {
        SourcePath = sourcePath;
        _snapshot = snapshot;
    }

    public IReadOnlyList<ToolDescriptor> Tools => _snapshot.Tools;
    public IReadOnlyList<ToolDescriptor> Exposed => _snapshot.Exposed;
    public string BaseUrl => _snapshot.BaseUrl;
    public int TimeoutSeconds => _snapshot.TimeoutSeconds;
    public ResponseEnvelope Response => _snapshot.Response;
    public string SourcePath { get; }
    public IReadOnlyList<OpenApiOperation> UnexposedOperations => _snapshot.Unexposed;

    public ToolDescriptor? Find(string name) => _snapshot.ByName.GetValueOrDefault(name);

    public ToolDescriptor GetRequired(string name) =>
        _snapshot.ByName.TryGetValue(name, out var t)
            ? t
            : throw new KeyNotFoundException($"tools.json 中不存在名为 '{name}' 的工具");

    public static ToolCatalog Load(
        string path,
        string? baseUrlOverride,
        int? timeoutOverride,
        IReadOnlyDictionary<string, OpenApiOperation>? openApi = null,
        Action<string>? log = null,
        string? pathPrefixOverride = null)
    {
        var fullPath = Path.GetFullPath(path);
        return new ToolCatalog(fullPath, BuildSnapshot(fullPath, baseUrlOverride, pathPrefixOverride, timeoutOverride, openApi, log));
    }

    /// <summary>
    /// 热加载：重新读文件并原子替换快照。
    /// 校验失败会抛异常，调用方保留旧快照 —— 改错一个字段不应该把服务打挂。
    /// </summary>
    public void Reload(
        string? baseUrlOverride,
        string? pathPrefixOverride,
        int? timeoutOverride,
        IReadOnlyDictionary<string, OpenApiOperation>? openApi,
        Action<string>? log = null)
        => _snapshot = BuildSnapshot(SourcePath, baseUrlOverride, pathPrefixOverride, timeoutOverride, openApi, log);

    private static Snapshot BuildSnapshot(
        string fullPath,
        string? baseUrlOverride,
        string? pathPrefixOverride,
        int? timeoutOverride,
        IReadOnlyDictionary<string, OpenApiOperation>? openApi,
        Action<string>? log)
    {
        var raw = ReadRaw(fullPath);

        var errors = new List<string>();
        var tools = new List<ToolDescriptor>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pathPrefix = NormalizePrefix(pathPrefixOverride);

        foreach (var rt in raw.Tools)
        {
            var descriptor = Map(rt, openApi, errors, pathPrefix);
            if (descriptor is null) continue;
            if (!seen.Add(descriptor.Name))
            {
                errors.Add($"工具名重复：'{descriptor.Name}'");
                continue;
            }
            if (!descriptor.IsExposedToModel)
                log?.Invoke($"工具 '{descriptor.Name}' 的 access 为 forbidden，不会注册给模型。");
            tools.Add(descriptor);
        }

        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"tools.json 校验失败（{fullPath}）：" + Environment.NewLine +
                string.Join(Environment.NewLine, errors.Select(e => "  - " + e)));

        // 找出 swagger 里有、但还没暴露给模型的接口，供开发期排查漏配。
        // 工具路径已经带上网关前缀了，这里比对时也要给 swagger 的原始路径加上同样的前缀，否则全部会被当成"没暴露"。
        var unexposed = new List<OpenApiOperation>();
        if (openApi is not null)
        {
            var covered = tools.Select(t => $"{t.Http.Method} {t.Http.Path}").ToHashSet(StringComparer.OrdinalIgnoreCase);
            unexposed.AddRange(openApi.Values
                .Where(op => !covered.Contains($"{op.Method} {ApplyGatewayPrefix(op.Path, pathPrefix)}")));
        }

        var baseUrl = baseUrlOverride ?? raw.Defaults?.BaseUrl ?? "http://localhost:5002";

        return new Snapshot(
            tools,
            tools.Where(t => t.IsExposedToModel).ToList(),
            tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase),
            baseUrl.TrimEnd('/'),
            pathPrefix,
            timeoutOverride ?? raw.Defaults?.TimeoutSeconds ?? 30,
            raw.Defaults?.Response ?? ResponseEnvelope.None,
            unexposed);
    }

    /// <summary>把配置里的前缀规整成 <c>/api/merchant</c> 形式；留空表示本地直连，不加前缀。</summary>
    private static string NormalizePrefix(string? prefix)
        => string.IsNullOrWhiteSpace(prefix) ? string.Empty : "/" + prefix.Trim().Trim('/');

    private static RawCatalog ReadRaw(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"工具目录文件不存在：{fullPath}", fullPath);

        var json = File.ReadAllText(fullPath);
        return JsonSerializer.Deserialize<RawCatalog>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) ?? throw new InvalidOperationException($"工具目录解析失败：{fullPath}");
    }

    /// <summary>
    /// 把业务服务内部路径（形如 /api/Products）换算成网关下的完整路径。
    /// 业务服务的 /api 前缀在网关上被映射成 prefix：
    ///   path="/api/Products" + prefix="/api/merchant"  ->  "/api/merchant/Products"
    ///   path="/api/Products" + prefix=""                ->  "/api/Products"（本地直连）
    /// </summary>
    public static string ApplyGatewayPrefix(string path, string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return path;
        var p = "/" + prefix.Trim('/');
        return path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
            ? p + path["/api".Length..]
            : p + path;
    }

    /// <summary>业务服务基地址：appsettings 覆盖 tools.json 默认值。</summary>
    public static string ResolveBaseUrl(string path, string? baseUrlOverride)
    {
        var raw = ReadRaw(path);
        return (baseUrlOverride ?? raw.Defaults?.BaseUrl ?? "http://localhost:5002").TrimEnd('/');
    }

    /// <summary>swagger 地址：tools.json 的 discovery.swaggerUrl 优先，否则从基地址推导。</summary>
    public static string ResolveSwaggerUrl(string path, string baseUrl)
    {
        var raw = ReadRaw(path);
        return string.IsNullOrWhiteSpace(raw.Discovery?.SwaggerUrl)
            ? $"{baseUrl}/swagger/v1/swagger.json"
            : raw.Discovery!.SwaggerUrl!;
    }

    /// <summary>tools.json 里是否有工具用 "from" 引用 swagger —— 有的话 swagger 取不到就必须启动失败。</summary>
    public static bool UsesOpenApi(string path)
        => ReadRaw(path).Tools.Any(t => !string.IsNullOrWhiteSpace(t.From));

    private static ToolDescriptor? Map(
        RawTool rt,
        IReadOnlyDictionary<string, OpenApiOperation>? openApi,
        List<string> errors,
        string? pathPrefix = null)
    {
        var where = string.IsNullOrWhiteSpace(rt.Name) ? "<未命名工具>" : rt.Name;

        if (string.IsNullOrWhiteSpace(rt.Name))
        {
            errors.Add("存在没有 name 的工具。");
            return null;
        }
        if (!ValidToolNameRegex.IsMatch(rt.Name))
        {
            errors.Add($"工具 '{rt.Name}' 的名字非法，只允许字母/数字/下划线/连字符，且不能以数字开头。");
            return null;
        }

        // ---- 解析 from：从 swagger 取路径与参数骨架 ----
        OpenApiOperation? operation = null;
        if (!string.IsNullOrWhiteSpace(rt.From))
        {
            if (openApi is null || openApi.Count == 0)
            {
                errors.Add($"工具 '{where}' 使用了 from='{rt.From}'，但没有取到 swagger 文档（检查 Ai:Tools:SwaggerUrl / 业务服务是否在运行）。");
                return null;
            }
            if (!openApi.TryGetValue(NormalizeOperationKey(rt.From), out operation))
            {
                var similar = openApi.Keys
                    .Where(k => k.Contains(rt.From.Split(' ').Last(), StringComparison.OrdinalIgnoreCase))
                    .Take(5)
                    .ToList();
                errors.Add($"工具 '{where}' 引用的操作 '{rt.From}' 在 swagger 中不存在。" +
                           (similar.Count > 0 ? $"你是不是想要：{string.Join(" / ", similar)}" : ""));
                return null;
            }
        }

        var method = (rt.Http?.Method ?? operation?.Method)?.ToUpperInvariant();
        var declaredPath = rt.Http?.Path ?? operation?.Path;

        // 经网关访问时把业务路径换到网关前缀下。
        // 必须在这里换、而不是靠 HttpClient 的 BaseAddress：工具路径都以 / 开头，
        // 按 Uri 的规则相对路径会**整个替换**掉 BaseAddress 的路径，前缀会被吃掉。
        var httpPath = string.IsNullOrWhiteSpace(declaredPath)
            ? declaredPath
            : ApplyGatewayPrefix(declaredPath, pathPrefix ?? string.Empty);

        if (string.IsNullOrWhiteSpace(httpPath))
        {
            errors.Add($"工具 '{where}' 缺少 http.path，也没有可用的 from 引用。");
            return null;
        }
        if (!httpPath.StartsWith('/'))
        {
            errors.Add($"工具 '{where}' 的路径必须以 / 开头，当前为 '{httpPath}'。");
            return null;
        }
        if (method is null || !AllowedMethods.Contains(method))
        {
            errors.Add($"工具 '{where}' 的 http.method '{method}' 不支持，只允许 {string.Join('/', AllowedMethods)}。");
            return null;
        }

        // ---- 安全默认：access 缺失或写错 -> 报错 + Forbidden，而不是「默认放行」 ----
        var access = rt.Access?.Trim().ToLowerInvariant() switch
        {
            "read" => ToolAccess.Read,
            "write" => ToolAccess.Write,
            "forbidden" or "none" or "disabled" => ToolAccess.Forbidden,
            null or "" => Report(errors, $"工具 '{where}' 没有声明 access。为避免写操作被静默放行，必须显式声明 read / write / forbidden。"),
            var other => Report(errors, $"工具 '{where}' 的 access '{other}' 非法，只允许 read / write / forbidden。")
        };

        // ---- 参数：swagger 骨架 + tools.json overlay（按名字合并，overlay 优先） ----
        var parameters = MergeParameters(operation, rt.Parameters, where, errors);
        var paramNames = parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in PathParamRegex.Matches(httpPath))
        {
            if (!paramNames.Contains(m.Groups[1].Value))
                errors.Add($"工具 '{where}' 的路径 '{httpPath}' 引用了未声明的参数 '{m.Groups[1].Value}'。");
        }

        var request = new RequestMapping
        {
            Query = rt.Request?.Query?.ToList() ?? operation?.QueryNames.ToList() ?? new List<string>(),
            Body = rt.Request?.Body?.DeepClone() ?? operation?.BodyTemplate?.DeepClone()
        };

        foreach (var q in request.Query)
        {
            if (!paramNames.Contains(q))
                errors.Add($"工具 '{where}' 的 request.query 引用了未声明的参数 '{q}'。");
        }

        if (request.Body is not null)
        {
            foreach (var referenced in CollectBodyReferences(request.Body))
            {
                if (!paramNames.Contains(referenced))
                    errors.Add($"工具 '{where}' 的 request.body 引用了未声明的参数 '{referenced}'。");
            }
        }

        return new ToolDescriptor
        {
            Name = rt.Name,
            Description = rt.Description?.Trim() ?? string.Empty,
            Access = access,
            Http = new HttpBinding { Method = method, Path = httpPath },
            Request = request,
            Parameters = parameters,
            SummaryFormatter = rt.SummaryFormatter,
            SummaryTemplate = rt.SummaryTemplate,
            Roles = rt.Roles?.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToList() ?? new List<string>()
        };
    }

    private static List<ToolParameter> MergeParameters(
        OpenApiOperation? operation,
        List<RawParameter> overlays,
        string where,
        List<string> errors)
    {
        var merged = new Dictionary<string, ToolParameter>(StringComparer.OrdinalIgnoreCase);

        // 关键：swagger 骨架对象会在多次 Load/Reload 之间复用，必须深拷贝后再改。
        // 否则 (a) 第二次加载会因为 ContainsKey 已为 true 而跳过 description 注入，(b) 并发加载会就地改坏共享的 JsonObject。
        foreach (var p in operation?.Parameters ?? Enumerable.Empty<ToolParameter>())
            merged[p.Name] = Clone(p);

        foreach (var overlay in overlays)
        {
            if (string.IsNullOrWhiteSpace(overlay.Name))
            {
                errors.Add($"工具 '{where}' 存在没有 name 的参数。");
                continue;
            }

            if (merged.TryGetValue(overlay.Name, out var baseParameter))
            {
                merged[overlay.Name] = ApplyOverlay(baseParameter, overlay);
            }
            else if (operation is not null)
            {
                // 用 from 引用了 swagger，却不认识这个参数名 —— 多半是后端改了字段名，早点炸出来比默默给出一个坏工具好。
                errors.Add($"工具 '{where}' 的参数 '{overlay.Name}' 在 swagger 操作 '{operation.Key}' 中不存在" +
                           $"（该操作的参数：{string.Join(", ", merged.Keys)}）。请核对后端契约是否已改名。");
            }
            else
            {
                if (overlay.Schema is not JsonObject)
                {
                    errors.Add($"工具 '{where}' 的参数 '{overlay.Name}' 必须显式提供 schema（没有 from 引用时无从推导）。");
                    continue;
                }
                merged[overlay.Name] = new ToolParameter
                {
                    Name = overlay.Name,
                    Description = overlay.Description,
                    Required = overlay.Required ?? false,
                    Default = overlay.Default?.DeepClone(),
                    Schema = (JsonObject)overlay.Schema.DeepClone()
                };
            }
        }

        var result = new List<ToolParameter>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in merged.Values)
        {
            if (!seen.Add(parameter.Name))
            {
                errors.Add($"工具 '{where}' 的参数名重复：'{parameter.Name}'。");
                continue;
            }
            if (parameter.Schema is not JsonObject schema)
            {
                errors.Add($"工具 '{where}' 的参数 '{parameter.Name}' 缺少 schema，或 schema 不是 JSON 对象。");
                continue;
            }

            // 关键：description/default 必须内联进 schema 才会被连接器发给模型（已实测验证）
            EnsureSchemaMeta(schema, "description", parameter.Description);
            if (parameter.Default is not null) EnsureSchemaMeta(schema, "default", parameter.Default.DeepClone());

            result.Add(new ToolParameter
            {
                Name = parameter.Name,
                Description = parameter.Description,
                Required = parameter.Required,
                Default = parameter.Default,
                Schema = schema
            });
        }

        return result;
    }

    /// <summary>拷贝一份参数，schema 深拷贝，确保后续修改不会碰到共享的 swagger 骨架。</summary>
    private static ToolParameter Clone(ToolParameter source) => new()
    {
        Name = source.Name,
        Description = source.Description,
        Required = source.Required,
        Default = source.Default?.DeepClone(),
        Schema = source.Schema.DeepClone()
    };

    private static ToolParameter ApplyOverlay(ToolParameter baseParameter, RawParameter overlay)
    {
        var schema = baseParameter.Schema as JsonObject ?? new JsonObject();

        if (overlay.Schema is JsonObject overlaySchema)
        {
            schema = string.Equals(overlay.SchemaMode, "replace", StringComparison.OrdinalIgnoreCase)
                ? (JsonObject)overlaySchema.DeepClone()   // 直接丢掉 swagger 的骨架（例如不想要的 productName/price 字段）
                : DeepMerge(schema, overlaySchema);
        }

        return new ToolParameter
        {
            Name = baseParameter.Name,
            Description = overlay.Description ?? baseParameter.Description,
            Required = overlay.Required ?? baseParameter.Required,
            Default = overlay.Default?.DeepClone() ?? baseParameter.Default,
            Schema = schema
        };
    }

    /// <summary>overlay 覆盖 base；对象递归合并，其余以 overlay 为准。</summary>
    private static JsonObject DeepMerge(JsonObject baseObject, JsonObject overlay)
    {
        var result = (JsonObject)baseObject.DeepClone();
        foreach (var (key, value) in overlay)
        {
            result[key] = value is JsonObject overlayChild && result[key] is JsonObject baseChild
                ? DeepMerge(baseChild, overlayChild)
                : value?.DeepClone();
        }
        return result;
    }

    private static string NormalizeOperationKey(string from)
    {
        var parts = from.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length < 2 ? from.Trim() : $"{parts[0].ToUpperInvariant()} {parts[1]}";
    }

    private static void EnsureSchemaMeta(JsonObject schema, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (schema.ContainsKey(key)) return;
        schema[key] = value;
    }

    private static void EnsureSchemaMeta(JsonObject schema, string key, JsonNode? value)
    {
        if (value is null) return;
        if (schema.ContainsKey(key)) return;
        schema[key] = value;
    }

    /// <summary>记录一条校验错误并返回 Forbidden 兜底值，便于在 switch 表达式里收集错误。</summary>
    private static ToolAccess Report(List<string> errors, string message)
    {
        errors.Add(message);
        return ToolAccess.Forbidden;
    }

    private static IEnumerable<string> CollectBodyReferences(JsonNode? node)
    {
        switch (node)
        {
            // $map 的 item 模板里的 {field}/$field 指向数组元素，不是顶层参数；
            // 只有 $map 指定的那个源数组才是顶层引用。
            case JsonObject mapObject
                when mapObject["$map"] is JsonValue source
                     && source.TryGetValue<string>(out var sourceName):
                yield return sourceName;
                break;

            case JsonObject obj:
                foreach (var (_, v) in obj)
                    foreach (var r in CollectBodyReferences(v))
                        yield return r;
                break;

            case JsonArray arr:
                foreach (var v in arr)
                    foreach (var r in CollectBodyReferences(v))
                        yield return r;
                break;

            case JsonValue val when val.TryGetValue<string>(out var s):
                if (s.StartsWith('$') && s.Length > 1) yield return s[1..];
                else if (s.StartsWith('{') && s.EndsWith('}') && s.Length > 2) yield return s[1..^1];
                break;
        }
    }
}
