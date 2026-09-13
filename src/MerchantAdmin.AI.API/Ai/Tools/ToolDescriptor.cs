using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.SemanticKernel;

namespace MerchantAdmin.AI.API.Ai.Tools;

/// <summary>工具分级。决定 GuardFilter 怎么处理这次调用。</summary>
public enum ToolAccess
{
    /// <summary>未声明或显式禁用：不注册给模型，模型看不到也调不到。</summary>
    Forbidden = 0,
    /// <summary>只读：直接执行。</summary>
    Read = 1,
    /// <summary>写操作：拦截至「待人工确认」，确认后才真正执行。</summary>
    Write = 2
}

/// <summary>工具参数。Schema 是发给模型的 JSON Schema；Description/Default 会在加载时内联进 Schema。</summary>
public sealed class ToolParameter
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool Required { get; init; }
    public JsonNode? Default { get; init; }
    /// <summary>已内联 description / default 的最终 JSON Schema。</summary>
    public required JsonNode Schema { get; init; }
}

/// <summary>工具落到哪个 HTTP 端点。</summary>
public sealed class HttpBinding
{
    public required string Method { get; init; }
    /// <summary>路径模板，支持 /api/orders/{orderId} 形式的路由参数。</summary>
    public required string Path { get; init; }
}

/// <summary>参数怎么塞进 HTTP 请求。</summary>
public sealed class RequestMapping
{
    /// <summary>这些参数进 querystring（为空且无 Body 时，默认所有参数都进 querystring）。</summary>
    public List<string> Query { get; init; } = new();
    /// <summary>Body 模板。字符串 "$param" 表示整块替换为该参数的原始值；"{param}" 表示字符串插值。</summary>
    public JsonNode? Body { get; init; }
    /// <summary>Body 模板里用到的参数中，哪些是「数组内每个元素各自成一次请求」（暂未实现，预留）。</summary>
    public bool BodyIsSingle { get; init; } = true;
}

/// <summary>业务接口响应外层包装（如 { code, message, data, success }）的解包规则。</summary>
public sealed class ResponseEnvelope
{
    public string? SuccessPath { get; init; }
    public string? MessagePath { get; init; }
    public string? CodePath { get; init; }
    public string? DataPath { get; init; }

    public static ResponseEnvelope None => new();
}

/// <summary>一个工具的完整定义。tools.json 里的一条 = 一个 ToolDescriptor。</summary>
public sealed class ToolDescriptor
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public ToolAccess Access { get; init; } = ToolAccess.Forbidden;
    public required HttpBinding Http { get; init; }
    public RequestMapping Request { get; init; } = new();
    public List<ToolParameter> Parameters { get; init; } = new();
    /// <summary>待确认卡片摘要的格式化器名字；缺省时把参数原样渲染成 JSON。</summary>
    public string? SummaryFormatter { get; init; }
    /// <summary>待确认卡片摘要的模板，形如 "取消订单 #{orderId}"。优先于 SummaryFormatter。</summary>
    public string? SummaryTemplate { get; init; }
    /// <summary>
    /// 允许调用这个工具的角色。空 = 任何登录用户都可以。
    /// 必要性：AI 后端调业务接口用的是**服务账号** token，不限制角色的话，
    /// 任何登录用户都能通过 AI 做到只有管理员才有的权限（比如删商品）。
    /// 取值要与后端控制器的 [Authorize(Roles=...)] 保持一致。
    /// </summary>
    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();

    public bool IsExposedToModel => Access is ToolAccess.Read or ToolAccess.Write;
    public bool RequiresApproval => Access == ToolAccess.Write;
}

/// <summary>一次已经解析/校验完毕的 HTTP 请求。审批流程把它存进 PendingAction，确认时原样重放。</summary>
public sealed class HttpRequestPlan
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public Dictionary<string, string> Query { get; init; } = new();
    public JsonNode? Body { get; init; }
    /// <summary>归一化后的参数（已补默认值），供审批摘要与轨迹使用。</summary>
    public IReadOnlyDictionary<string, JsonNode?> Arguments { get; init; } = new Dictionary<string, JsonNode?>();

    public string Describe()
    {
        var q = Query.Count == 0 ? "" : "?" + string.Join("&", Query.Select(kv => $"{kv.Key}={kv.Value}"));
        var b = Body is null ? "" : $" body={Body.ToJsonString()}";
        return $"{Method} {Path}{q}{b}";
    }
}

/// <summary>写操作在「待确认」卡片上展示的业务摘要。可按 tools.json 的 summaryFormatter 指定，缺省则渲染全部参数。</summary>
public interface IPendingSummaryFormatter
{
    string Name { get; }
    string Format(ToolDescriptor descriptor, HttpRequestPlan plan);
}

/// <summary>tools.json 里 tools[].parameters[] 的原始形态（description/default 可能缺省，加载时补齐）。</summary>
internal sealed class RawParameter
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool? Required { get; set; }
    public JsonNode? Default { get; set; }
    public JsonNode? Schema { get; set; }
    /// <summary>merge（默认，与 swagger 骨架深合并）或 replace（整份替换 swagger 给的 schema）。</summary>
    public string? SchemaMode { get; set; }
}

internal sealed class RawTool
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Access { get; set; }
    /// <summary>引用 swagger 里的一个操作，形如 "GET /api/Products"。路径/参数骨架从 swagger 取，其余由本条目覆盖。</summary>
    public string? From { get; set; }
    public RawHttp? Http { get; set; }
    public RawRequest? Request { get; set; }
    public List<RawParameter> Parameters { get; set; } = new();
    public string? SummaryFormatter { get; set; }
    public string? SummaryTemplate { get; set; }
    /// <summary>允许调用这个工具的角色；不写 = 任何登录用户。</summary>
    public List<string>? Roles { get; set; }
}

internal sealed class RawHttp
{
    public string Method { get; set; } = "GET";
    public string Path { get; set; } = string.Empty;
}

internal sealed class RawRequest
{
    public List<string>? Query { get; set; }
    public JsonNode? Body { get; set; }
}

internal sealed class RawDiscovery
{
    public bool? Enabled { get; set; }
    public string? SwaggerUrl { get; set; }
}

internal sealed class RawDefaults
{
    public string? BaseUrl { get; set; }
    public int? TimeoutSeconds { get; set; }
    public ResponseEnvelope? Response { get; set; }
}

internal sealed class RawCatalog
{
    public int Version { get; set; } = 1;
    public RawDefaults? Defaults { get; set; }
    public RawDiscovery? Discovery { get; set; }
    public List<RawTool> Tools { get; set; } = new();
}
