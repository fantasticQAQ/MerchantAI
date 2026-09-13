using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MerchantAdmin.AI.API.Ai.Tools;
using StackExchange.Redis;

namespace MerchantAdmin.AI.API.Harness;

/// <summary>
/// 基于 Redis 的待确认操作存储。
///
/// 存在的意义：待确认操作内存化时，「模型在 A 实例登记、用户点确认打到 B 实例」会直接失败
/// （表现为「待确认操作不存在或已失效」）。放 Redis 后多实例部署才成立。
///
/// 存的内容天然就是 JSON 友好的：HttpRequestPlan 里是 method / path / query / body / arguments。
/// </summary>
public sealed class RedisPendingActionStore : IPendingActionStore
{
    private const string KeyPrefix = "merchant-ai:pending:";
    private const string SessionIndexPrefix = "merchant-ai:pending-session:";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping   // 摘要里是中文，别转义
    };

    private readonly RedisConnection _redis;
    private readonly TimeSpan _ttl;
    private readonly ILogger<RedisPendingActionStore> _logger;

    public RedisPendingActionStore(RedisConnection redis, IConfiguration configuration, ILogger<RedisPendingActionStore> logger)
    {
        _redis = redis;
        _logger = logger;
        _ttl = TimeSpan.FromMinutes(Math.Max(5, configuration.GetValue("Ai:Approval:TtlMinutes", 30)));
    }

    public void Add(PendingAction action)
    {
        var database = _redis.Database;
        if (database is null) throw new InvalidOperationException("Redis 不可用");

        var payload = new StoredAction
        {
            ActionId = action.ActionId,
            ToolName = action.ToolName,
            SessionId = action.SessionId,
            UserId = action.UserId,
            Summary = action.Summary,
            CreatedAt = action.CreatedAt,
            Method = action.Plan.Method,
            Path = action.Plan.Path,
            Query = action.Plan.Query,
            Body = action.Plan.Body?.ToJsonString(),
            Arguments = action.Plan.Arguments.ToDictionary(
                kv => kv.Key,
                kv => kv.Value?.ToJsonString())
        };

        database.StringSet(KeyPrefix + action.ActionId, JsonSerializer.Serialize(payload, JsonOpts), _ttl);
        // 按会话建索引，刷新页面后才能把确认卡片恢复出来
        database.SetAdd(SessionIndexPrefix + action.SessionId, action.ActionId);
        database.KeyExpire(SessionIndexPrefix + action.SessionId, _ttl);
    }

    public PendingAction? Get(string actionId)
    {
        var database = _redis.Database;
        if (database is null) return null;

        var json = database.StringGet(KeyPrefix + actionId);
        if (json.IsNullOrEmpty) return null;

        try
        {
            var stored = JsonSerializer.Deserialize<StoredAction>(json!, JsonOpts);
            return stored is null ? null : ToPendingAction(stored);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取待确认操作 {ActionId} 失败", actionId);
            return null;
        }
    }

    public bool Remove(string actionId)
    {
        var database = _redis.Database;
        if (database is null) return false;

        // 先把会话索引里的成员清掉，再删本体
        var stored = Get(actionId);
        if (stored is not null)
            database.SetRemove(SessionIndexPrefix + stored.SessionId, actionId);

        return database.KeyDelete(KeyPrefix + actionId);
    }

    public IReadOnlyList<PendingAction> ListBySession(string sessionId)
    {
        var database = _redis.Database;
        if (database is null) return Array.Empty<PendingAction>();

        try
        {
            var ids = database.SetMembers(SessionIndexPrefix + sessionId);
            if (ids.Length == 0) return Array.Empty<PendingAction>();

            var result = new List<PendingAction>();
            foreach (var id in ids)
            {
                var action = Get(id!);
                if (action is not null) result.Add(action);
                else database.SetRemove(SessionIndexPrefix + sessionId, id);   // 本体已过期，顺手清索引
            }

            return result.OrderBy(a => a.CreatedAt).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "列取会话 {Session} 的待确认操作失败", sessionId);
            return Array.Empty<PendingAction>();
        }
    }

    public int PurgeExpired(TimeSpan ttl) => 0;   // 交给 Redis 的 key TTL

    private static PendingAction ToPendingAction(StoredAction stored)
    {
        var arguments = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in stored.Arguments)
            arguments[key] = value is null ? null : JsonNode.Parse(value);

        return new PendingAction
        {
            ActionId = stored.ActionId,
            ToolName = stored.ToolName,
            SessionId = stored.SessionId,
            UserId = stored.UserId,
            Summary = stored.Summary,
            CreatedAt = stored.CreatedAt,
            Plan = new HttpRequestPlan
            {
                Method = stored.Method,
                Path = stored.Path,
                Query = stored.Query,
                Body = stored.Body is null ? null : JsonNode.Parse(stored.Body),
                Arguments = arguments
            }
        };
    }

    private sealed class StoredAction
    {
        public string ActionId { get; set; } = string.Empty;
        public string ToolName { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public long UserId { get; set; }
        public string Summary { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public string Method { get; set; } = "POST";
        public string Path { get; set; } = string.Empty;
        public Dictionary<string, string> Query { get; set; } = new();
        public string? Body { get; set; }
        public Dictionary<string, string?> Arguments { get; set; } = new();
    }
}
