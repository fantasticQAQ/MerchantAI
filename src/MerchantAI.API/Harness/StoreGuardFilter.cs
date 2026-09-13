using Microsoft.SemanticKernel;
using MerchantAdmin.AI.API.Ai.Tools;
using MerchantAdmin.AI.API.Auth;

namespace MerchantAdmin.AI.API.Harness;

/// <summary>
/// 工具护栏 Filter：所有工具调用的唯一收口。
///
/// 四件事：
/// 1. 分级——依据 tools.json 的 access，而不是写死在这里的工具名列表。
///    这样「新加写工具却忘了加拦截」这种安全漏洞在结构上就不可能出现（access 是必填项，缺了启动直接失败）。
/// 2. 角色——tools.json 的 roles 决定哪些角色能用这个工具。
///    必须做，因为 AI 后端调业务接口用的是服务账号 token：不按角色拦，任何登录用户都能借 AI 拿到管理员权限。
/// 3. 审批——写操作一律登记为「待确认」并短路，绝不在这里执行。
/// 4. 循环护栏——SK 的 Auto 循环没有步数上限，这里兜住工具调用次数和审批次数，防止无限循环烧 token。
/// </summary>
public class StoreGuardFilter : IFunctionInvocationFilter
{
    private readonly IToolCatalog _catalog;
    private readonly HumanApprovalService _approvals;
    private readonly AgentTraceService _trace;
    private readonly IHttpContextAccessor _http;
    private readonly ICurrentUser _user;
    private readonly AgentLoopOptions _options;
    private readonly ILogger<StoreGuardFilter> _logger;

    public StoreGuardFilter(
        IToolCatalog catalog,
        HumanApprovalService approvals,
        AgentTraceService trace,
        IHttpContextAccessor http,
        ICurrentUser user,
        AgentLoopOptions options,
        ILogger<StoreGuardFilter> logger)
    {
        _catalog = catalog;
        _approvals = approvals;
        _trace = trace;
        _http = http;
        _user = user;
        _options = options;
        _logger = logger;
    }

    public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        var fnName = context.Function.Name;
        var httpContext = _http.HttpContext;
        var sessionId = httpContext?.Items["SessionId"] as string ?? "unknown";
        var userId = _user.UserId;
        var descriptor = _catalog.Find(fnName);

        // 不是业务工具（例如最外层的 InvokePromptAsync），只记轨迹
        if (descriptor is null)
        {
            _trace.Record(sessionId, "tool_pre", fnName, null, userId);
            await next(context);
            _trace.Record(sessionId, "tool_post", fnName, context.Result?.ToString(), userId);
            return;
        }

        // ---- 角色护栏：工具可以限定只有某些角色能用 ----
        if (!_user.IsInAnyRole(descriptor.Roles))
        {
            _trace.Record(sessionId, "tool_denied", fnName,
                new { reason = "role", required = descriptor.Roles, actual = _user.Roles }, userId);
            _logger.LogWarning("用户 {User} 的角色 {Roles} 不允许使用工具 {Tool}（需要 {Required}）",
                _user.UserName, string.Join("/", _user.Roles), fnName, string.Join("/", descriptor.Roles));

            context.Result = new FunctionResult(context.Function,
                $"工具 {fnName} 需要 {string.Join("/", descriptor.Roles)} 权限，当前账号没有。请如实告知用户无权执行该操作。");
            return;
        }

        // ---- 循环护栏：整轮对话的工具调用次数 ----
        var used = AgentLoopState.Read(httpContext, AgentLoopState.ToolCallCountKey);
        if (used >= _options.MaxToolCallsPerTurn)
        {
            if (httpContext is not null) httpContext.Items[AgentLoopState.TruncatedKey] = true;
            _trace.Record(sessionId, "loop_guard", fnName,
                new { reason = "tool_calls_exceeded", used, limit = _options.MaxToolCallsPerTurn }, userId);
            _logger.LogWarning("会话 {Session} 本轮工具调用已达上限 {Limit}，强制停止调用工具", sessionId, _options.MaxToolCallsPerTurn);

            context.Result = new FunctionResult(context.Function,
                $"[已达本轮工具调用上限 {_options.MaxToolCallsPerTurn} 次] 请不要再调用任何工具，" +
                "立刻基于已经拿到的信息用自然语言作答；如果信息不足以完成任务，就如实说明还缺什么。");
            return;
        }
        AgentLoopState.Bump(httpContext, AgentLoopState.ToolCallCountKey);

        switch (descriptor.Access)
        {
            case ToolAccess.Forbidden:
                _trace.Record(sessionId, "tool_blocked", fnName, null, userId);
                _logger.LogWarning("工具 {Tool} 的 access 为 forbidden，已拦截", fnName);
                context.Result = new FunctionResult(context.Function,
                    $"工具 {fnName} 已被策略禁用，无法调用。请如实告知用户该操作不可用。");
                return;

            case ToolAccess.Write:
                var mode = AgentPermission.Read(httpContext);

                // 仅可查看：写操作一律拒绝（比登记一张卡片更干脆，也避免用户误点）
                if (mode == AiPermissionMode.ReadOnly)
                {
                    _trace.Record(sessionId, "tool_denied", fnName, new { reason = "readonly_mode" }, userId);
                    context.Result = new FunctionResult(context.Function,
                        $"当前是「仅可查看」权限，不能执行写操作（{fnName}）。请如实告知用户，并建议他切换权限级别。");
                    return;
                }

                // 完全权限：不登记待确认，直接执行。审计照记，出问题能回溯。
                if (mode == AiPermissionMode.Full)
                {
                    _trace.Record(sessionId, "tool_auto_executed", fnName, new
                    {
                        mode = mode.ToWire(),
                        arguments = SafeArguments(context.Arguments)
                    }, userId);

                    _trace.Record(sessionId, "tool_pre", fnName, null, userId);
                    await next(context);
                    _trace.Record(sessionId, "tool_post", fnName, context.Result?.ToString(), userId);
                    return;
                }

                // 默认（需确认）：登记为待人工确认，绝不在这里执行
                // ---- 循环护栏：审批卡片数量 ----
                var approvals = AgentLoopState.Read(httpContext, AgentLoopState.ApprovalCountKey);
                if (approvals >= _options.MaxApprovalsPerTurn)
                {
                    _trace.Record(sessionId, "loop_guard", fnName,
                        new { reason = "approvals_exceeded", approvals, limit = _options.MaxApprovalsPerTurn }, userId);
                    context.Result = new FunctionResult(context.Function,
                        $"[本轮待确认操作已达到上限 {_options.MaxApprovalsPerTurn} 条] 请停止登记新的写操作，" +
                        "先让用户处理已经生成的确认卡片。");
                    return;
                }
                AgentLoopState.Bump(httpContext, AgentLoopState.ApprovalCountKey);

                var registration = _approvals.Request(descriptor, context.Arguments, sessionId, userId);

                if (registration.Registered && httpContext is not null)
                {
                    // 一轮对话可能连续登记多个写操作（如「每个商品各下一单」），必须全部收集，
                    // 否则前端只会拿到最后一张确认卡片。
                    if (httpContext.Items["PendingActionIds"] is not List<string> ids)
                    {
                        ids = new List<string>();
                        httpContext.Items["PendingActionIds"] = ids;
                    }
                    ids.Add(registration.ActionId!);
                }

                context.Result = new FunctionResult(context.Function, registration.Message);
                return;

            case ToolAccess.Read:
            default:
                _trace.Record(sessionId, "tool_pre", fnName, null, userId);
                await next(context);
                _trace.Record(sessionId, "tool_post", fnName, context.Result?.ToString(), userId);
                return;
        }
    }

    private static string? SafeArguments(KernelArguments arguments)
    {
        try
        {
            return JsonText.Render(System.Text.Json.Nodes.JsonNode.Parse(
                System.Text.Json.JsonSerializer.Serialize(arguments)));
        }
        catch
        {
            return null;
        }
    }
}
