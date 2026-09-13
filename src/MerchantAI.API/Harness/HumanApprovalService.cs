using Microsoft.SemanticKernel;
using MerchantAdmin.AI.API.Ai.Tools;

namespace MerchantAdmin.AI.API.Harness;

/// <summary>登记待确认操作的结果。</summary>
public sealed record ApprovalRegistration(bool Registered, string? ActionId, string Summary, string Message);

/// <summary>确认执行的结果。刻意不含任何「工具名判断」——加新写工具不需要改这里。</summary>
public sealed record ConfirmOutcome(
    string ActionId,
    string ToolName,
    string Summary,
    bool Success,
    string? Data,
    string? Error);

/// <summary>
/// 人机协同（HITL）审批服务，对应 README 里规划的 HumanApprovalService。
/// 职责：写操作的参数校验 → 生成可重放的请求 → 登记待确认 → 确认后统一重放。
/// 这里是唯一知道「待确认操作怎么执行」的地方，StoreGuardFilter 和 ChatController 都只调它。
/// </summary>
public sealed class HumanApprovalService
{
    private static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(30);

    private readonly IPendingActionStore _store;
    private readonly IToolCatalog _catalog;
    private readonly IToolInvoker _invoker;
    private readonly PendingSummaryRegistry _summaries;
    private readonly AgentTraceService _trace;
    private readonly ILogger<HumanApprovalService> _logger;

    public HumanApprovalService(
        IPendingActionStore store,
        IToolCatalog catalog,
        IToolInvoker invoker,
        PendingSummaryRegistry summaries,
        AgentTraceService trace,
        ILogger<HumanApprovalService> logger)
    {
        _store = store;
        _catalog = catalog;
        _invoker = invoker;
        _summaries = summaries;
        _trace = trace;
        _logger = logger;
    }

    /// <summary>写操作被拦截后调用：校验参数并登记一条待人工确认的操作。</summary>
    public ApprovalRegistration Request(ToolDescriptor descriptor, KernelArguments arguments, string sessionId, long userId)
    {
        var plan = _invoker.BuildPlan(descriptor, arguments);

        // 参数不合法时绝不登记空操作：否则会生成一张内容为空的确认卡片，用户点了才报错
        if (!plan.Ok)
        {
            _trace.Record(sessionId, "approval_invalid", descriptor.Name, new { error = plan.Error }, userId);
            return new ApprovalRegistration(false, null, string.Empty, plan.Error!);
        }

        _store.PurgeExpired(PendingTtl);

        var summary = _summaries.Format(descriptor, plan.Plan!);
        var action = new PendingAction
        {
            ActionId = Guid.NewGuid().ToString("N"),
            ToolName = descriptor.Name,
            SessionId = sessionId,
            UserId = userId,
            Plan = plan.Plan!,
            Summary = summary
        };
        _store.Add(action);

        _trace.Record(sessionId, "approval_pending", descriptor.Name, new
        {
            action.ActionId,
            summary,
            request = plan.Plan!.Describe()
        }, userId);

        var message =
            $"[待人工确认] 已登记操作（actionId={action.ActionId}）：{descriptor.Name} —— {summary}。" +
            "请用自然语言清楚地告诉用户即将执行的内容，并询问是否确认；" +
            "不要自行继续，也不要在用户确认前声称操作已经完成。";

        return new ApprovalRegistration(true, action.ActionId, summary, message);
    }

    /// <summary>确认后真正执行。可一次确认多条（一轮对话里模型可能连续登记了多个写操作）。</summary>
    public async Task<IReadOnlyList<ConfirmOutcome>> ConfirmManyAsync(
        IEnumerable<string> actionIds,
        string sessionId,
        long userId,
        CancellationToken ct = default)
    {
        var outcomes = new List<ConfirmOutcome>();
        foreach (var actionId in actionIds)
            outcomes.Add(await ConfirmAsync(actionId, sessionId, userId, ct));
        return outcomes;
    }

    public async Task<ConfirmOutcome> ConfirmAsync(string actionId, string sessionId, long userId, CancellationToken ct = default)
    {
        var action = _store.Get(actionId);
        if (action is null)
            return new ConfirmOutcome(actionId, string.Empty, string.Empty, false, null, "待确认操作不存在或已失效");

        // 别人登记的写操作，你不能替他点确认
        if (action.UserId != 0 && action.UserId != userId)
        {
            _trace.Record(sessionId, "approval_denied", action.ToolName, new { actionId, reason = "not_owner" }, userId);
            return new ConfirmOutcome(actionId, action.ToolName, action.Summary, false, null, "无权确认其他用户登记的操作");
        }

        var descriptor = _catalog.Find(action.ToolName);
        if (descriptor is null)
            return new ConfirmOutcome(actionId, action.ToolName, action.Summary, false, null,
                $"工具 {action.ToolName} 已从 tools.json 中移除或因 access 变更而不可执行");

        var result = await _invoker.SendAsync(descriptor, action.Plan, ct);

        // 请求已送达（不管业务成功失败）就消费掉，避免用户重复点击造成重复下单；
        // 没送达（服务未启动/超时）则保留，让用户恢复服务后能重试。
        if (result.Delivered) _store.Remove(actionId);

        _trace.Record(sessionId, result.Delivered ? "tool_post" : "tool_error", descriptor.Name, new
        {
            actionId,
            request = action.Plan.Describe(),
            delivered = result.Delivered,
            result = result.Text
        }, userId);

        if (result.Delivered)
            return new ConfirmOutcome(actionId, descriptor.Name, action.Summary, true, result.Text, null);

        _logger.LogWarning("写操作 {Tool} 未送达业务服务：{Text}", descriptor.Name, result.Text);
        return new ConfirmOutcome(actionId, descriptor.Name, action.Summary, false, null, result.Text);
    }
}
