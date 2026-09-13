using System.Collections.Concurrent;
using System.Text.Json;
using MerchantAdmin.AI.API.Ai.Tools;
using StackExchange.Redis;

namespace MerchantAdmin.AI.API.Harness;

/// <summary>
/// 会话的「用户偏好」：改过的名字、置顶、所属分组。
///
/// 为什么不塞进 <see cref="SessionMetadata"/>：那份元信息是**从对话内容推出来的**
/// （标题取第一条用户消息、条数取 history.Count），每次保存都会整体重写。
/// 而这几项是用户手动设置的、和内容无关的状态，混进去会被下一次重写冲掉。
/// </summary>
public sealed record SessionPrefs
{
    /// <summary>标题。用户手动改过，或由模型总结生成。</summary>
    public string? Title { get; init; }

    /// <summary>true = 模型自动生成的，可以被后续总结覆盖；false = 用户手动命名，永不被覆盖。</summary>
    public bool TitleIsAuto { get; init; }

    public bool Pinned { get; init; }

    /// <summary>所属分组 id。null = 未分组。</summary>
    public string? GroupId { get; init; }

    /// <summary>
    /// 用户拖出来的顺序。null = 从没拖过，按最后活跃时间排。
    /// 用 int 而不是链表/浮点：一个分组里几十个会话，拖一次整体重排一遍完全够用，
    /// 而且存下来就是人一眼能看懂的数字，出问题好查。
    /// </summary>
    public int? SortOrder { get; init; }
}

/// <summary>一个会话分组。</summary>
public sealed record SessionGroup(string Id, string Name, bool Pinned, DateTime CreatedAt);

/// <summary>会话偏好与分组的存储。和会话/轨迹一样**永久保留**。</summary>
public interface ISessionPrefsStore
{
    /// <summary>一次取出该用户全部会话的偏好，列表接口靠它避免逐条往返。</summary>
    IReadOnlyDictionary<string, SessionPrefs> All(long userId);

    SessionPrefs Get(long userId, string sessionId);

    void Save(long userId, string sessionId, SessionPrefs prefs);

    IReadOnlyList<SessionGroup> Groups(long userId);

    SessionGroup CreateGroup(long userId, string name);

    /// <summary>改名 / 置顶。没传的项保持原样。</summary>
    bool UpdateGroup(long userId, string groupId, string? name, bool? pinned);

    /// <summary>删除分组本身。组里的会话**不会被删**，只是回到未分组。</summary>
    void DeleteGroup(long userId, string groupId);
}

internal static class SessionPrefsJson
{
    public static readonly JsonSerializerOptions Options = new(JsonText.Relaxed)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

public sealed class InMemorySessionPrefsStore : ISessionPrefsStore
{
    private readonly ConcurrentDictionary<string, SessionPrefs> _prefs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, List<SessionGroup>> _groups = new();
    private readonly object _groupLock = new();

    private static string Key(long userId, string sessionId) => $"{userId}:{sessionId}";

    public IReadOnlyDictionary<string, SessionPrefs> All(long userId)
    {
        var prefix = userId + ":";
        return _prefs
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key[prefix.Length..], kv => kv.Value, StringComparer.Ordinal);
    }

    public SessionPrefs Get(long userId, string sessionId)
        => _prefs.TryGetValue(Key(userId, sessionId), out var prefs) ? prefs : new SessionPrefs();

    public void Save(long userId, string sessionId, SessionPrefs prefs)
        => _prefs[Key(userId, sessionId)] = prefs;

    public IReadOnlyList<SessionGroup> Groups(long userId)
    {
        lock (_groupLock)
            return _groups.TryGetValue(userId, out var list) ? list.ToList() : new List<SessionGroup>();
    }

    public SessionGroup CreateGroup(long userId, string name)
    {
        var group = new SessionGroup(Guid.NewGuid().ToString("N"), name, false, DateTime.Now);
        lock (_groupLock)
        {
            var list = _groups.GetOrAdd(userId, _ => new List<SessionGroup>());
            list.Add(group);
        }
        return group;
    }

    public bool UpdateGroup(long userId, string groupId, string? name, bool? pinned)
    {
        lock (_groupLock)
        {
            if (!_groups.TryGetValue(userId, out var list)) return false;
            var index = list.FindIndex(g => g.Id == groupId);
            if (index < 0) return false;

            var current = list[index];
            list[index] = current with
            {
                Name = string.IsNullOrWhiteSpace(name) ? current.Name : name.Trim(),
                Pinned = pinned ?? current.Pinned
            };
            return true;
        }
    }

    public void DeleteGroup(long userId, string groupId)
    {
        lock (_groupLock)
        {
            if (!_groups.TryGetValue(userId, out var list)) return;
            list.RemoveAll(g => g.Id == groupId);
        }
    }
}

/// <summary>
/// Redis 实现。
/// - 偏好：`merchant-ai:prefs:{userId}` 哈希，field = sessionId
/// - 分组：`merchant-ai:groups:{userId}` 一个 JSON 数组
/// 两者都**不设过期时间** —— 置顶和分组要是自己过期了，用户的整理就白做了。
/// </summary>
public sealed class RedisSessionPrefsStore : ISessionPrefsStore
{
    private const string PrefsPrefix = "merchant-ai:prefs:";
    private const string GroupsPrefix = "merchant-ai:groups:";

    private readonly RedisConnection _redis;
    private readonly ILogger<RedisSessionPrefsStore> _logger;
    // 分组的读-改-写是「先 HashGetAll 再整体写回」，同进程内必须串行，否则并发改名会互相覆盖
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _locks = new();

    public RedisSessionPrefsStore(RedisConnection redis, ILogger<RedisSessionPrefsStore> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public IReadOnlyDictionary<string, SessionPrefs> All(long userId)
    {
        var result = new Dictionary<string, SessionPrefs>(StringComparer.Ordinal);
        try
        {
            var database = _redis.Database;
            if (database is null) return result;

            foreach (var entry in database.HashGetAll(PrefsPrefix + userId))
            {
                if (entry.Value.IsNullOrEmpty) continue;
                if (Deserialize<SessionPrefs>(entry.Value!) is { } prefs)
                    result[entry.Name.ToString()] = prefs;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取会话偏好失败：用户 {UserId}", userId);
        }
        return result;
    }

    public SessionPrefs Get(long userId, string sessionId)
    {
        try
        {
            var value = _redis.Database?.HashGet(PrefsPrefix + userId, sessionId);
            if (value is not null && !value.Value.IsNullOrEmpty)
                return Deserialize<SessionPrefs>(value.Value!) ?? new SessionPrefs();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取会话偏好失败：{Session}", sessionId);
        }
        return new SessionPrefs();
    }

    public void Save(long userId, string sessionId, SessionPrefs prefs)
    {
        try
        {
            _redis.Database?.HashSet(PrefsPrefix + userId, sessionId, JsonSerializer.Serialize(prefs, SessionPrefsJson.Options));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存会话偏好失败：{Session}", sessionId);
        }
    }

    public IReadOnlyList<SessionGroup> Groups(long userId) => ReadGroups(userId);

    public SessionGroup CreateGroup(long userId, string name)
    {
        var gate = _locks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            var groups = ReadGroups(userId).ToList();
            var group = new SessionGroup(Guid.NewGuid().ToString("N"), name, false, DateTime.Now);
            groups.Add(group);
            WriteGroups(userId, groups);
            return group;
        }
        finally
        {
            gate.Release();
        }
    }

    public bool UpdateGroup(long userId, string groupId, string? name, bool? pinned)
    {
        var gate = _locks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            var groups = ReadGroups(userId).ToList();
            var index = groups.FindIndex(g => g.Id == groupId);
            if (index < 0) return false;

            var current = groups[index];
            groups[index] = current with
            {
                Name = string.IsNullOrWhiteSpace(name) ? current.Name : name.Trim(),
                Pinned = pinned ?? current.Pinned
            };
            WriteGroups(userId, groups);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public void DeleteGroup(long userId, string groupId)
    {
        var gate = _locks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            var groups = ReadGroups(userId).ToList();
            if (groups.RemoveAll(g => g.Id == groupId) > 0) WriteGroups(userId, groups);
        }
        finally
        {
            gate.Release();
        }
    }

    private List<SessionGroup> ReadGroups(long userId)
    {
        try
        {
            var value = _redis.Database?.StringGet(GroupsPrefix + userId);
            if (value is not null && !value.Value.IsNullOrEmpty)
                return Deserialize<List<SessionGroup>>(value.Value!) ?? new List<SessionGroup>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取会话分组失败：用户 {UserId}", userId);
        }
        return new List<SessionGroup>();
    }

    private void WriteGroups(long userId, List<SessionGroup> groups)
    {
        try
        {
            _redis.Database?.StringSet(GroupsPrefix + userId, JsonSerializer.Serialize(groups, SessionPrefsJson.Options));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存会话分组失败：用户 {UserId}", userId);
        }
    }

    private static T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, SessionPrefsJson.Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
