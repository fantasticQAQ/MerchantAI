namespace MerchantAdmin.AI.API.Harness;

/// <summary>
/// 「确认后自动续跑」用的合成指令。
///
/// 背景：写操作必须人工点确认才落地，但**点完确认之后模型并不会自己继续下一步** ——
/// 用户得反复打「继续」，多步任务（比如「删掉全部订单再重建」）体验很差。
/// 这里在确认执行完之后，把执行结果作为一条助手消息写进历史，再用下面这条合成指令让模型接着往下做。
/// 人工确认这道闸门仍然在：每一步写操作照样要用户点一次。
/// </summary>
public static class AgentContinuation
{
    /// <summary>合成消息的标记。会话记录展示时会把它过滤掉，免得看起来像用户自己说的话。</summary>
    public const string Marker = "[自动继续]";

    public const string Message =
        Marker + " 用户已确认并执行完上一步操作。请继续完成剩余步骤；" +
        "如果整个任务已经结束，就直接给出最终汇总。不要询问是否继续，也不要重复已经完成的操作。";

    public static bool IsContinuation(string? text)
        => text is not null && text.StartsWith(Marker, StringComparison.Ordinal);
}
