using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using MerchantAdmin.AI.API.Ai.Tools;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using StackExchange.Redis;

namespace MerchantAdmin.AI.API.Harness;

/// <summary>Redis 连接的持有者。连接不可用时 <see cref="IsAvailable"/> 为 false，调用方降级到内存实现。</summary>
public sealed class RedisConnection : IDisposable
{
    private readonly ILogger<RedisConnection> _logger;
    private readonly Lazy<IConnectionMultiplexer?> _multiplexer;

    public RedisConnection(IConfiguration configuration, ILogger<RedisConnection> logger)
    {
        _logger = logger;
        ConnectionString = configuration["Ai:Redis:ConnectionString"] ?? string.Empty;

        _multiplexer = new Lazy<IConnectionMultiplexer?>(() =>
        {
            if (string.IsNullOrWhiteSpace(ConnectionString)) return null;

            try
            {
                var options = ConfigurationOptions.Parse(ConnectionString);
                options.AbortOnConnectFail = false;          // 起服务时 Redis 没起来也不该崩
                options.ConnectTimeout = 3000;
                return ConnectionMultiplexer.Connect(options);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "连接 Redis 失败（{ConnectionString}），会话/待确认操作将退回内存存储", ConnectionString);
                return null;
            }
        });
    }

    public string ConnectionString { get; }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(ConnectionString);

    public IDatabase? Database
    {
        get
        {
            var multiplexer = _multiplexer.Value;
            if (multiplexer is null) return null;

            try
            {
                return multiplexer.GetDatabase();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "获取 Redis database 失败");
                return null;
            }
        }
    }

    /// <summary>
    /// 真的 ping 一次。注意不能只看 <see cref="Database"/> 是否为 null ——
    /// 为了「Redis 没起来也不崩服务」我们设了 AbortOnConnectFail=false，
    /// 这种情况下即使地址写错、Redis 没起，Database 依然拿得到，一 ping 才暴露出不可用。
    /// </summary>
    public bool TryPing()
    {
        if (!IsEnabled) return false;

        try
        {
            return Database?.Ping() is not null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Redis ping 失败（{ConnectionString}）：{Message}", ConnectionString, ex.Message);
            return false;
        }
    }

    /// <summary>取的 Redis server，用于 SCAN（列会话时用）。</summary>
    public IServer? Server
    {
        get
        {
            var multiplexer = _multiplexer.Value;
            if (multiplexer is null) return null;

            try
            {
                return multiplexer.GetServers().FirstOrDefault();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "获取 Redis server 失败");
                return null;
            }
        }
    }

    public void Dispose()
    {
        if (_multiplexer.IsValueCreated) _multiplexer.Value?.Dispose();
    }
}

/// <summary>
/// 基于 Redis 的会话历史。为了跨重启、跨实例都不丢上下文。
///
/// 前提是 <see cref="ChatHistorySerializer"/> 的往返保真已经验证过 —— 见 ChatHistorySerializerTests，
/// 它比对的是「还原后重新发出的 messages 与原始请求是否逐字节一致」。
/// </summary>
public sealed class RedisConversationStore : IConversationStore
{
    private const string KeyPrefix = "merchant-ai:conv:";
    private const string LockPrefix = "merchant-ai:conv-lock:";
    private const int MaxScanPerList = 500;

    private static readonly JsonSerializerOptions MetaJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly RedisConnection _redis;
    private readonly ILogger<RedisConversationStore> _logger;
    private readonly int _maxMessages;
    /// <summary>null = 永不过期（长期保留会话）。</summary>
    private readonly TimeSpan? _ttl;
    private readonly TimeSpan _lockTtl = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _localGates = new(StringComparer.Ordinal);

    public RedisConversationStore(RedisConnection redis, IConfiguration configuration, ILogger<RedisConversationStore> logger)
    {
        _redis = redis;
        _logger = logger;
        _maxMessages = Math.Max(8, configuration.GetValue("Ai:History:MaxMessages", 500));

        // TtlMinutes <= 0 表示永不过期
        var ttlMinutes = configuration.GetValue("Ai:History:TtlMinutes", 0);
        _ttl = ttlMinutes > 0 ? TimeSpan.FromMinutes(ttlMinutes) : null;
    }

    public int SessionCount => -1;   // 分布式存储无法廉价统计

    public async Task<T> UseAsync<T>(string sessionId, long userId, string userName, Func<ChatHistory, Task<T>> action, CancellationToken ct = default)
    {
        var database = _redis.Database;
        if (database is null) throw new InvalidOperationException("Redis 不可用");

        // 会话按「用户 + sessionId」分命名空间：sessionId 是客户端生成的，
        // 只看它做键的话，同一个浏览器换账号登录（localStorage 还留着上一个人的 id）就会撞车。
        var storageKey = StorageKey(userId, sessionId);

        // 同一进程内先串行（避免本机并发抢锁），再拿 Redis 锁（避免跨实例并发改同一条会话）
        var gate = _localGates.GetOrAdd(storageKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);

        var lockToken = Guid.NewGuid().ToString("N");
        var lockKey = LockPrefix + storageKey;
        var locked = false;

        try
        {
            for (var attempt = 0; attempt < 20 && !locked; attempt++)
            {
                locked = await database.StringSetAsync(lockKey, lockToken, _lockTtl, When.NotExists);
                if (!locked) await Task.Delay(100, ct);
            }

            if (!locked)
                _logger.LogWarning("会话 {Session} 未能取得 Redis 锁，本次仍会继续（并发极端情况下可能丢一条消息）", sessionId);

            var (meta, history) = await LoadAsync(database, storageKey);

            // 兜底：键里已经带了 userId，正常不会走到这里
            if (meta is not null && meta.UserId != userId)
                throw new SessionAccessDeniedException(sessionId);

            var result = await action(history);

            Trim(history);
            await SaveAsync(database, storageKey, userId, userName, meta?.CreatedAt, history);

            return result;
        }
        finally
        {
            if (locked)
            {
                // 只删自己的锁，避免把别人的锁释放掉
                const string releaseScript = "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";
                try { await database.ScriptEvaluateAsync(releaseScript, new RedisKey[] { lockKey }, new RedisValue[] { lockToken }); }
                catch (Exception ex) { _logger.LogWarning(ex, "释放会话锁失败：{Session}", sessionId); }
            }

            gate.Release();
        }
    }

    public void Clear(string sessionId, long userId)
    {
        var database = _redis.Database;
        database?.KeyDelete(KeyPrefix + StorageKey(userId, sessionId));
    }

    public int PurgeExpired(TimeSpan ttl) => 0;   // Redis 的 key TTL 自己会过期

    private static string StorageKey(long userId, string sessionId) => $"{userId}:{sessionId}";

    public async Task<IReadOnlyList<ChatMessageContent>?> GetMessagesAsync(string sessionId, long userId, CancellationToken ct = default)
    {
        var database = _redis.Database;
        if (database is null) return null;

        try
        {
            var json = await database.StringGetAsync(KeyPrefix + StorageKey(userId, sessionId));
            if (json.IsNullOrEmpty) return null;

            var (meta, history) = ParseEnvelope(json!);
            // 不是自己的会话一律当作"不存在"，避免泄露"这个会话存在但不属于你"
            if (meta is not null && meta.UserId != userId) return null;

            return history.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取会话 {Session} 失败", sessionId);
            return null;
        }
    }

    /// <summary>
    /// 该用户的会话列表。会话本体里就带着元信息，所以这里直接 SCAN 一遍再按 userId 过滤即可，
    /// 不需要再维护一个可能和本体脱节的索引。
    /// 代价是 O(会话数) 的读取 —— 对「查看会话记录」这种管理动作是可以接受的量级。
    /// </summary>
    public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(long userId, int limit = 50, CancellationToken ct = default)
    {
        var database = _redis.Database;
        var server = _redis.Server;
        if (database is null || server is null) return Array.Empty<SessionSummary>();

        try
        {
            var summaries = new List<SessionSummary>();
            var scanned = 0;

            await foreach (var key in server.KeysAsync(pattern: KeyPrefix + "*", pageSize: 250))
            {
                if (ct.IsCancellationRequested || ++scanned > MaxScanPerList) break;

                var raw = key.ToString()[KeyPrefix.Length..];
                var json = await database.StringGetAsync(key);
                if (json.IsNullOrEmpty) continue;

                var (meta, history) = ParseEnvelope(json!);

                // 没有 meta 的会话是加鉴权之前写的，**无法判定归属**，一律不暴露（宁可看不见，也不能让所有人看见）
                if (meta is null) continue;
                if (meta.UserId != userId) continue;

                // 键形如 {userId}:{sessionId}
                var separator = raw.IndexOf(':');
                var sessionId = separator >= 0 ? raw[(separator + 1)..] : raw;

                summaries.Add(new SessionSummary(
                    sessionId, meta.Title, history.Count, meta.CreatedAt, meta.LastActiveAt,
                    SessionMetadata.BuildPreview(history)));
            }

            return summaries
                .OrderByDescending(s => s.LastActiveAt)
                .Take(Math.Clamp(limit, 1, 200))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "列取会话列表失败");
            return Array.Empty<SessionSummary>();
        }
    }

    /// <summary>
    /// 会话的存储格式：{ "meta": {...}, "history": {...} }。
    /// 老数据是裸的 history（没有 meta），这里也能读出来 —— meta 从内容现推，时间是未知。
    /// </summary>
    private static string SerializeEnvelope(SessionMetadata meta, string historyJson)
    {
        var envelope = new JsonObject
        {
            ["meta"] = JsonSerializer.SerializeToNode(meta, MetaJson),
            ["history"] = JsonNode.Parse(historyJson)
        };
        return envelope.ToJsonString(MetaJson);
    }

    private static (SessionMetadata? Meta, ChatHistory History) ParseEnvelope(string json)
    {
        var node = JsonNode.Parse(json);

        if (node is JsonObject obj && obj["history"] is not null)
        {
            var meta = obj["meta"] is { } metaNode
                ? metaNode.Deserialize<SessionMetadata>(MetaJson)
                : null;
            return (meta, ChatHistorySerializer.Deserialize(obj["history"]!.ToJsonString()));
        }

        // 老格式：整份就是一个 ChatHistory
        return (null, ChatHistorySerializer.Deserialize(json));
    }

    private async Task<(SessionMetadata? Meta, ChatHistory History)> LoadAsync(IDatabase database, string storageKey)
    {
        try
        {
            var json = await database.StringGetAsync(KeyPrefix + storageKey);
            return json.IsNullOrEmpty
                ? (null, new ChatHistory())
                : ParseEnvelope(json!);
        }
        catch (Exception ex)
        {
            // 反序列化失败不应该让用户卡住：当作新会话继续
            _logger.LogWarning(ex, "读取会话 {Session} 失败，按空会话继续", storageKey);
            return (null, new ChatHistory());
        }
    }

    private async Task SaveAsync(IDatabase database, string storageKey, long userId, string userName, DateTime? createdAt, ChatHistory history)
    {
        try
        {
            // sessionId 传的是原始值（显示用），storageKey 才是真正落库的键
            var separator = storageKey.IndexOf(':');
            var sessionId = separator >= 0 ? storageKey[(separator + 1)..] : storageKey;

            var meta = SessionMetadata.FromHistory(sessionId, userId, userName, history, createdAt);
            var payload = SerializeEnvelope(meta, ChatHistorySerializer.Serialize(history));

            // _ttl 为 null 时不传过期时间 —— SET 不带 TTL 就是永久保留
            if (_ttl is { } ttl)
                await database.StringSetAsync(KeyPrefix + storageKey, payload, ttl);
            else
                await database.StringSetAsync(KeyPrefix + storageKey, payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存会话 {Session} 失败（本轮对话仍然返回给用户）", storageKey);
        }
    }

    /// <summary>压缩历史。细节见 <see cref="ChatHistoryCompactor"/>。</summary>
    private void Trim(ChatHistory history) => ChatHistoryCompactor.Compact(history, _maxMessages);
}
