namespace MerchantAdmin.AI.API.Harness;

/// <summary>
/// Agent 循环护栏配置。
/// 之所以必须在 Filter 层自己实现：Semantic Kernel 1.80 的 FunctionChoiceBehavior.Auto
/// **没有任何「最多自动调用几轮工具」的上限**（FunctionChoiceBehaviorOptions 里没有对应项），
/// 模型一旦陷进工具循环就会一直烧 token。这里给每一轮对话加硬上限。
/// </summary>
public sealed class AgentLoopOptions
{
    /// <summary>
    /// 一轮对话里最多允许多少次工具调用，超过后强制模型基于现有信息作答。
    ///
    /// 定成 60 是因为**它才是批量任务的真正瓶颈**：审批上限提到 50 之后，
    /// 如果这里还是 12，「一次建 50 个商品」照样会在第 12 个被砍掉。
    /// 50 次写 + 若干次只读查询，60 够用。
    /// </summary>
    public int MaxToolCallsPerTurn { get; init; } = 60;

    /// <summary>单轮对话的总超时（秒）。超过后中止本轮并如实告知用户。</summary>
    public int TurnTimeoutSeconds { get; init; } = 120;

    /// <summary>
    /// 一轮对话最多允许多少次「写操作登记」。
    /// 定得宽（50）是因为批量任务很常见（「删掉全部订单」一次就 15 条），
    /// 限得太死会逼出「分好几批 + 用户反复点继续」的体验。真正的闸门是人工确认，不是这个数字。
    /// </summary>
    public int MaxApprovalsPerTurn { get; init; } = 50;

    public static AgentLoopOptions FromConfiguration(IConfiguration configuration) => new()
    {
        MaxToolCallsPerTurn = Math.Max(1, configuration.GetValue("Ai:Agent:MaxToolCallsPerTurn", 60)),
        TurnTimeoutSeconds = Math.Max(5, configuration.GetValue("Ai:Agent:TurnTimeoutSeconds", 120)),
        MaxApprovalsPerTurn = Math.Max(1, configuration.GetValue("Ai:Agent:MaxApprovalsPerTurn", 50))
    };
}

/// <summary>一轮对话内跨 Filter 调用共享的计数，挂在 HttpContext.Items 上。</summary>
public static class AgentLoopState
{
    public const string ToolCallCountKey = "__ai_tool_call_count__";
    public const string ApprovalCountKey = "__ai_approval_count__";
    public const string TruncatedKey = "__ai_agent_truncated__";

    public static int Bump(HttpContext? context, string key)
    {
        if (context is null) return 1;
        var next = (context.Items[key] as int? ?? 0) + 1;
        context.Items[key] = next;
        return next;
    }

    public static int Read(HttpContext? context, string key) => context?.Items[key] as int? ?? 0;
}
