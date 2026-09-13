using System.Collections.Concurrent;
using StackExchange.Redis;

namespace MerchantAdmin.AI.API.Harness;

/// <summary>
/// 「界面上不显示」和「真的删掉」是两件事，这里管前者。
///
/// 为什么要有它：审计轨迹和会话都要求**永久保留在 Redis 里**（不能真删），
/// 但界面上总得能清理 —— 不然列表越堆越长。
/// 所以删除动作一律是**软删除**：只在这里记一笔「这条别再展示了」，Redis 里的原始数据一条不动。
/// 想恢复的话，把对应集合里的成员删掉即可（也可以做「回收站」，数据都还在）。
///
/// 注意粒度是**按用户**的：同一个 sessionId 在不同用户下是两个会话（会话键本来就按 userId 分开），
/// 所以隐藏标记也必须带上 userId，否则 A 隐藏了会连 B 的一起藏掉。
/// </summary>
public interface IVisibilityStore
{
    /// <summary>隐藏整个会话：会话本身和它的轨迹都不再展示。</summary>
    void HideSession(long userId, string sessionId);

    /// <summary>
    /// 只隐藏轨迹，会话（对话内容）照常展示。
    ///
    /// 注意：**当前没有对外接口调用它** —— 产品上轨迹只能随会话一起删。
    /// 保留是因为 IsTraceHidden/HiddenTraces 仍然会读取这份标记（历史上写过的数据要认），
    /// 而且能力留着，将来若要单独清理轨迹不用重写存储层。
    /// </summary>
    void HideTraces(long userId, string sessionId);

    bool IsSessionHidden(long userId, string sessionId);

    /// <summary>轨迹是否不该展示。会话被隐藏时，它的轨迹自然也不展示。</summary>
    bool IsTraceHidden(long userId, string sessionId);

    /// <summary>一次取出该用户所有被隐藏的会话，避免列表接口里逐条往返 Redis。</summary>
    IReadOnlySet<string> HiddenSessions(long userId);

    IReadOnlySet<string> HiddenTraces(long userId);
}

/// <summary>没配 Redis 时的退路：重启即丢，和会话/待确认操作的内存实现保持一致的语义。</summary>
public sealed class InMemoryVisibilityStore : IVisibilityStore
{
    private readonly ConcurrentDictionary<string, byte> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _traces = new(StringComparer.Ordinal);

    private static string Key(long userId, string sessionId) => $"{userId}:{sessionId}";

    public void HideSession(long userId, string sessionId) => _sessions[Key(userId, sessionId)] = 0;

    public void HideTraces(long userId, string sessionId) => _traces[Key(userId, sessionId)] = 0;

    public bool IsSessionHidden(long userId, string sessionId) => _sessions.ContainsKey(Key(userId, sessionId));

    public bool IsTraceHidden(long userId, string sessionId)
        => IsSessionHidden(userId, sessionId) || _traces.ContainsKey(Key(userId, sessionId));

    public IReadOnlySet<string> HiddenSessions(long userId) => OfUser(_sessions, userId);

    public IReadOnlySet<string> HiddenTraces(long userId) => OfUser(_traces, userId);

    private static IReadOnlySet<string> OfUser(ConcurrentDictionary<string, byte> source, long userId)
    {
        var prefix = userId + ":";
        return source.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => k[prefix.Length..])
            .ToHashSet(StringComparer.Ordinal);
    }
}

/// <summary>
/// Redis 实现。和会话/轨迹一样**不设过期时间** —— 这只是一张「别展示」的名单，
/// 它自己过期了就会把删掉的东西又冒出来，那才是真的见鬼。
/// </summary>
public sealed class RedisVisibilityStore : IVisibilityStore
{
    private const string SessionKey = "merchant-ai:hidden:sessions";
    private const string TraceKey = "merchant-ai:hidden:traces";

    private readonly RedisConnection _redis;
    private readonly ILogger<RedisVisibilityStore> _logger;

    public RedisVisibilityStore(RedisConnection redis, ILogger<RedisVisibilityStore> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    private static RedisValue Member(long userId, string sessionId) => $"{userId}:{sessionId}";
    private static string Prefix(long userId) => userId + ":";

    public void HideSession(long userId, string sessionId) => Add(SessionKey, Member(userId, sessionId));

    public void HideTraces(long userId, string sessionId) => Add(TraceKey, Member(userId, sessionId));

    public bool IsSessionHidden(long userId, string sessionId)
        => Contains(SessionKey, Member(userId, sessionId));

    public bool IsTraceHidden(long userId, string sessionId)
        => IsSessionHidden(userId, sessionId) || Contains(TraceKey, Member(userId, sessionId));

    public IReadOnlySet<string> HiddenSessions(long userId) => Members(SessionKey, userId);

    public IReadOnlySet<string> HiddenTraces(long userId) => Members(TraceKey, userId);

    private void Add(string key, RedisValue member)
    {
        try
        {
            _redis.Database?.SetAdd(key, member);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入隐藏标记失败：{Key}/{Member}", key, member);
        }
    }

    private bool Contains(string key, RedisValue member)
    {
        try
        {
            return _redis.Database?.SetContains(key, member) ?? false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取隐藏标记失败：{Key}/{Member}", key, member);
            return false;
        }
    }

    private IReadOnlySet<string> Members(string key, long userId)
    {
        try
        {
            var database = _redis.Database;
            if (database is null) return new HashSet<string>(StringComparer.Ordinal);

            var prefix = Prefix(userId);
            return database.SetMembers(key)
                .Select(v => v.ToString())
                .Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
                .Select(v => v[prefix.Length..])
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取隐藏标记列表失败：{Key}", key);
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }
}
