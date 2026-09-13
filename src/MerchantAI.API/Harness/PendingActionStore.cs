using System.Collections.Concurrent;
using MerchantAdmin.AI.API.Ai.Tools;

namespace MerchantAdmin.AI.API.Harness;

/// <summary>
/// 待人工确认的操作（HITL）。
/// 关键设计：这里存的是**一份可直接重放的 HTTP 请求**（HttpRequestPlan），而不是「工具名 + 弱类型字典」。
/// 于是确认执行时不需要任何 if (FunctionName == "xxx") 分支，加新写工具也不用改审批代码。
/// </summary>
public sealed class PendingAction
{
    public required string ActionId { get; init; }
    public required string ToolName { get; init; }
    public required string SessionId { get; init; }
    /// <summary>登记这条操作的用户。确认/取消时校验，别人不能替你点确认。</summary>
    public long UserId { get; init; }
    public required HttpRequestPlan Plan { get; init; }
    public required string Summary { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.Now;
}

public interface IPendingActionStore
{
    void Add(PendingAction action);
    PendingAction? Get(string actionId);
    bool Remove(string actionId);
    /// <summary>某个会话还没确认的操作。用于刷新页面后把确认卡片恢复出来。</summary>
    IReadOnlyList<PendingAction> ListBySession(string sessionId);
    /// <summary>清理超过 ttl 的陈旧操作，避免内存无限增长。</summary>
    int PurgeExpired(TimeSpan ttl);
}

public sealed class InMemoryPendingActionStore : IPendingActionStore
{
    private readonly ConcurrentDictionary<string, PendingAction> _actions = new();

    public void Add(PendingAction action) => _actions[action.ActionId] = action;

    public PendingAction? Get(string actionId)
        => _actions.TryGetValue(actionId, out var a) ? a : null;

    public bool Remove(string actionId) => _actions.TryRemove(actionId, out _);

    public IReadOnlyList<PendingAction> ListBySession(string sessionId)
        => _actions.Values
            .Where(a => string.Equals(a.SessionId, sessionId, StringComparison.Ordinal))
            .OrderBy(a => a.CreatedAt)
            .ToList();

    public int PurgeExpired(TimeSpan ttl)
    {
        var cutoff = DateTime.Now - ttl;
        var stale = _actions.Where(kv => kv.Value.CreatedAt < cutoff).Select(kv => kv.Key).ToList();
        foreach (var key in stale) _actions.TryRemove(key, out _);
        return stale.Count;
    }
}
