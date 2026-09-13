using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using MerchantAdmin.AI.API.Ai.Tools;
using StackExchange.Redis;

namespace MerchantAdmin.AI.API.Harness;

/// <summary>
/// Agent 轨迹审计：append-only，对齐 DSH 的 trajectory 思想，
/// 记录每次工具调用（触发前/后、函数名、参数、结果），出问题可回放。
///
/// **两处同时落**（都可以单独关掉）：
/// - Redis LIST `merchant-ai:trace:{yyyyMMdd}`，**不带 TTL，永久保留**，跨重启、多实例共享；
/// - 本地 JSONL 文件 `traces/trace-yyyyMMdd.jsonl`，兜底 + 方便直接翻文件/grep。
///
/// 读取优先走 Redis；某一天在 Redis 里没有（比如切到 Redis 之前写的老文件），自动回退到文件。
///
/// 几个刻意的健壮性处理：
/// - 文件锁是**按路径的进程级锁**，不是实例锁。同一进程里多个实例（测试、多租户）写同一个按天切分的文件时不会互相踩。
/// - 写失败一律吞掉并记日志。审计是旁路，绝不能因为写轨迹失败而让业务请求失败。
/// - 文件路径**每次写入时按当天重新算**。之前是构造时算一次，服务作为单例跑过午夜后，
///   第二天、第三天的轨迹会一直追加到启动那天的文件里 —— 按日期筛选就再也对不上了。
/// </summary>
public class AgentTraceService
{
    private const string RedisPrefix = "merchant-ai:trace:";
    private const string RedisDatesKey = "merchant-ai:trace:dates";
    /// <summary>按会话切的轨迹列表。前端是「选中一个会话 → 看这个会话的轨迹」，所以这份索引才是主查询路径。</summary>
    private const string RedisSessionPrefix = "merchant-ai:trace:s:";
    /// <summary>扫描旧数据时最多往回看多少天（回填用）。</summary>
    private const int BackfillMaxDays = 14;

    /// <summary>
    /// 单条轨迹 payload 的字符上限。
    /// 工具返回的商品列表动辄几千字，原样存进去会把轨迹撑得又大又没法读 ——
    /// 审计要的是「调了什么、成没成」，不是把返回的每一行都抄一遍。
    /// </summary>
    private const int MaxPayloadChars = 2000;

    private static readonly ConcurrentDictionary<string, object> PathLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _directory;
    private readonly RedisConnection? _redis;
    private readonly bool _writeFile;
    private readonly bool _writeRedis;
    private readonly int _maxScan;
    private readonly ILogger<AgentTraceService>? _logger;

    public AgentTraceService(
        IConfiguration configuration,
        RedisConnection? redis = null,
        ILogger<AgentTraceService>? logger = null)
        : this(
            System.IO.Path.Combine(AppContext.BaseDirectory, "traces"),
            redis,
            configuration.GetValue("Ai:Trace:File", true),
            configuration.GetValue("Ai:Trace:Redis", true),
            configuration.GetValue("Ai:Trace:MaxScan", 20000),
            logger)
    {
    }

    /// <summary>只写本地文件的构造（测试用，不牵扯 Redis）。</summary>
    public AgentTraceService(string directory, ILogger<AgentTraceService>? logger = null)
        : this(directory, null, writeFile: true, writeRedis: false, maxScan: 20000, logger)
    {
    }

    private AgentTraceService(
        string directory,
        RedisConnection? redis,
        bool writeFile,
        bool writeRedis,
        int maxScan,
        ILogger<AgentTraceService>? logger)
    {
        _directory = directory;
        _redis = redis;
        _writeFile = writeFile;
        _writeRedis = writeRedis;
        _maxScan = Math.Max(100, maxScan);
        _logger = logger;

        if (_writeFile) Directory.CreateDirectory(directory);
    }

    /// <summary>今天的文件路径。按天切分，所以每次都要现算。</summary>
    public string FilePath => PathFor(DateOnly.FromDateTime(DateTime.Now));

    private string PathFor(DateOnly date) => System.IO.Path.Combine(_directory, $"trace-{date:yyyyMMdd}.jsonl");

    private static string RedisKeyFor(DateOnly date) => RedisPrefix + date.ToString("yyyyMMdd");

    /// <summary>Redis 是否真的在用（启动时可以据此打日志）。</summary>
    public bool UsingRedis => _writeRedis && _redis?.Database is not null;

    public void Record(string sessionId, string eventType, string functionName, object? payload, long userId = 0)
    {
        var now = DateTime.Now;

        try
        {
            // 用 Relaxed：默认的 System.Text.Json 会把中文转成 \uXXXX，审计是给人看的
            var line = JsonSerializer.Serialize(new
            {
                time = now.ToString("O"),
                sessionId,
                userId,
                eventType,      // user_input / tool_pre / tool_post / approval_pending / approval_invalid / loop_guard / answer / turn_summary ...
                functionName,
                payload = ClipPayload(payload)
            }, JsonText.Relaxed);

            if (_writeFile) WriteToFile(line, DateOnly.FromDateTime(now));
            if (_writeRedis) WriteToRedis(line, DateOnly.FromDateTime(now), sessionId, userId);
        }
        catch (Exception ex)
        {
            // 审计是旁路：写不进去也不能影响正在处理的请求
            _logger?.LogWarning(ex, "写入审计轨迹失败");
        }
    }

    /// <summary>字符串类型的 payload 太长的截断，其余原样。避免一行轨迹几十 KB。</summary>
    private static object? ClipPayload(object? payload)
        => payload is string text && text.Length > MaxPayloadChars
            ? text[..MaxPayloadChars] + $"…（已截断，原文 {text.Length} 字）"
            : payload;

    private void WriteToFile(string line, DateOnly date)
    {
        var path = PathFor(date);
        var fileLock = PathLocks.GetOrAdd(path, _ => new object());

        lock (fileLock)
        {
            // 允许其它读/写方同时打开（例如日志采集、另一个实例），并对短暂占用做重试
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    using var writer = new StreamWriter(stream, Encoding.UTF8);
                    writer.Write(line + Environment.NewLine);
                    return;
                }
                catch (IOException) when (attempt < 3)
                {
                    Thread.Sleep(20 * (attempt + 1));
                }
            }
        }
    }

    /// <summary>
    /// 写 Redis。**刻意不设过期时间** —— 审计要长期保留，这正是要把轨迹从本地文件搬到 Redis 的原因之一。
    ///
    /// 用同步 API：本地 Redis 是亚毫秒级，而且这里本来就在同步写文件；
    /// 换成 fire-and-forget 会丢掉先后顺序，审计记录乱序基本就没用了。
    /// </summary>
    private void WriteToRedis(string line, DateOnly date, string sessionId, long userId)
    {
        var database = _redis?.Database;
        if (database is null) return;

        database.ListRightPush(RedisKeyFor(date), line);
        database.SetAdd(RedisDatesKey, date.ToString("yyyyMMdd"));

        // 再按会话存一份。前端的轨迹视图是「选中一个会话看它自己的轨迹」，
        // 按天存的话每次都得把所有天都捞一遍再过滤。
        if (!string.IsNullOrWhiteSpace(sessionId) && sessionId != "unknown")
            database.ListRightPush(SessionKeyFor(sessionId, userId), line);
    }

    private static string SessionKeyFor(string sessionId, long userId) => $"{RedisSessionPrefix}{userId}:{sessionId}";

    // ---------------------------------------------------------------- 读取（按会话）

    /// <summary>
    /// 某个会话的轨迹，**按时间正序**（时间线读法，最早的在最前）。
    ///
    /// Redis 里没有这个会话的索引时（本次改动之前写入的老轨迹只有按天的那份），
    /// 回退去扫最近若干天的按天列表，并按会话过滤 —— 顺便**回填**一份到会话索引里，
    /// 下次就直接命中了。
    /// </summary>
    public IReadOnlyList<TraceEntry> ReadSession(string sessionId, long userId, int limit = 500)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return Array.Empty<TraceEntry>();

        var lines = ReadRawSessionFromRedis(sessionId, userId);
        if (lines.Count == 0) lines = BackfillSession(sessionId, userId);

        var take = Math.Clamp(limit, 1, 5000);
        var entries = new List<TraceEntry>();
        for (var i = 0; i < lines.Count && entries.Count < take; i++)
            if (Parse(lines[i]) is { } entry)
                entries.Add(entry);

        return entries;
    }

    /// <summary>某个会话有多少条轨迹。会话列表左侧显示这个数字。</summary>
    public int CountBySession(string sessionId, long userId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return 0;

        if (_writeRedis && _redis?.Database is { } database)
        {
            try
            {
                var length = database.ListLength(SessionKeyFor(sessionId, userId));
                if (length > 0) return (int)Math.Min(length, int.MaxValue);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "统计会话轨迹条数失败：{Session}", sessionId);
            }
        }

        // 老数据还没回填：现扫一遍（会顺手回填），保证列表上的数字是对的
        return BackfillSession(sessionId, userId).Count;
    }

    private List<string> ReadRawSessionFromRedis(string sessionId, long userId)
    {
        var result = new List<string>();
        if (!_writeRedis) return result;

        try
        {
            var database = _redis?.Database;
            if (database is null) return result;

            var key = SessionKeyFor(sessionId, userId);
            var length = database.ListLength(key);
            if (length == 0) return result;

            var scan = (long)Math.Min(length, _maxScan);
            foreach (var value in database.ListRange(key, length - scan, -1))
                if (!value.IsNullOrEmpty) result.Add(value!);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "从 Redis 读取会话轨迹失败：{Session}", sessionId);
        }

        return result;
    }

    /// <summary>
    /// 从按天的列表里把某个会话的轨迹捞出来，并回填到会话索引。
    /// 只往回看 <see cref="BackfillMaxDays"/> 天 —— 再老的会话也不该让一次查看变成全库扫描。
    /// </summary>
    private List<string> BackfillSession(string sessionId, long userId)
    {
        var database = _redis?.Database;
        if (!_writeRedis || database is null) return new List<string>();

        var collected = new List<string>();

        try
        {
            var dates = AvailableDates().Take(BackfillMaxDays).ToList();
            foreach (var date in dates)
            {
                foreach (var line in ReadRawFromRedis(date).Concat(ReadRawFromFile(date)))
                {
                    var entry = Parse(line);
                    if (entry is null) continue;
                    if (entry.UserId != userId) continue;
                    if (!string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal)) continue;
                    if (!collected.Contains(line)) collected.Add(line);
                }
            }

            if (collected.Count > 0)
                database.ListRightPush(SessionKeyFor(sessionId, userId), collected.Select(l => (RedisValue)l).ToArray());
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "回填会话轨迹失败：{Session}", sessionId);
        }

        return collected;
    }

    // ---------------------------------------------------------------- 读取（按天）

    /// <summary>目录里 / Redis 里有哪些天的轨迹（合并去重，倒序）。</summary>
    public IReadOnlyList<DateOnly> AvailableDates()
    {
        var dates = new HashSet<DateOnly>();

        if (_writeRedis)
        {
            try
            {
                var database = _redis?.Database;
                if (database is not null)
                    foreach (var member in database.SetMembers(RedisDatesKey))
                        if (DateOnly.TryParseExact(member.ToString(), "yyyyMMdd", out var date))
                            dates.Add(date);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "从 Redis 读取审计日期失败，改用本地文件");
            }
        }

        // 一律也看看本地文件：切到 Redis 之前写的那些天只存在于文件里
        try
        {
            if (Directory.Exists(_directory))
                foreach (var file in Directory.EnumerateFiles(_directory, "trace-*.jsonl"))
                {
                    var name = System.IO.Path.GetFileNameWithoutExtension(file);
                    if (name.Length > "trace-".Length
                        && DateOnly.TryParseExact(name["trace-".Length..], "yyyyMMdd", out var date))
                        dates.Add(date);
                }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "枚举本地审计文件失败");
        }

        return dates.OrderByDescending(d => d).ToList();
    }

    /// <summary>
    /// 倒序读取某一天的轨迹（最新在前）。可按会话和事件类型过滤。
    /// 坏行（例如进程被杀时写了一半）直接跳过，不能因为一行脏数据让整个审计页面打不开。
    /// </summary>
    public IReadOnlyList<TraceEntry> Read(
        int limit = 200,
        string? sessionId = null,
        string? eventType = null,
        DateOnly? date = null,
        long? userId = null)
    {
        var target = date ?? DateOnly.FromDateTime(DateTime.Now);

        // 优先 Redis。Redis 里这一天没有内容时回退到文件 ——
        // 覆盖「切到 Redis 之前写下的老轨迹」以及「本机根本没连 Redis」两种情况。
        var raw = ReadRawFromRedis(target);
        if (raw.Count == 0) raw = ReadRawFromFile(target);

        return Filter(raw, limit, sessionId, eventType, userId);
    }

    private List<string> ReadRawFromRedis(DateOnly date)
    {
        var result = new List<string>();
        if (!_writeRedis) return result;

        try
        {
            var database = _redis?.Database;
            if (database is null) return result;

            var key = RedisKeyFor(date);
            var length = database.ListLength(key);
            if (length == 0) return result;

            // 从最新往前捞 maxScan 条，避免某天几十万条时把审计页面拖死
            var scan = (long)Math.Min(length, _maxScan);
            foreach (var value in database.ListRange(key, length - scan, -1))
                if (!value.IsNullOrEmpty) result.Add(value!);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "从 Redis 读取审计轨迹失败（{Date}）", date);
        }

        return result;
    }

    private List<string> ReadRawFromFile(DateOnly date)
    {
        var file = PathFor(date);
        if (!File.Exists(file)) return new List<string>();

        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
                if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);

            // 和 Redis 一致：只保留最后 maxScan 行
            return lines.Count <= _maxScan ? lines : lines.GetRange(lines.Count - _maxScan, _maxScan);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "读取审计轨迹失败：{File}", file);
            return new List<string>();
        }
    }

    private static List<TraceEntry> Filter(
        List<string> lines, int limit, string? sessionId, string? eventType, long? userId)
    {
        var take = Math.Clamp(limit, 1, 2000);
        var result = new List<TraceEntry>();

        // 从后往前读，未必最高效但实现简单；轨迹是排障用途，量级可以接受
        for (var i = lines.Count - 1; i >= 0 && result.Count < take; i--)
        {
            var entry = Parse(lines[i]);
            if (entry is null) continue;
            if (!string.IsNullOrWhiteSpace(sessionId) && !string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal)) continue;
            if (!string.IsNullOrWhiteSpace(eventType) && !string.Equals(entry.EventType, eventType, StringComparison.OrdinalIgnoreCase)) continue;
            // 非管理员（userId 有值）只能看自己的。
            // 加鉴权之前写入的条目没有 userId（=0），无法判定归属 —— 一律不暴露给普通用户，管理员仍可看。
            if (userId is { } owner && entry.UserId != owner) continue;
            result.Add(entry);
        }

        return result;
    }

    private static TraceEntry? Parse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            var time = root.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.String
                       && DateTime.TryParse(t.GetString(), out var parsed) ? parsed : DateTime.MinValue;

            // payload 必须 Clone：JsonDocument 一释放，它的 JsonElement 就失效了
            JsonElement? payload = root.TryGetProperty("payload", out var p) && p.ValueKind != JsonValueKind.Null
                ? p.Clone()
                : null;

            return new TraceEntry(
                time,
                root.TryGetProperty("sessionId", out var s) ? s.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("userId", out var u) && u.TryGetInt64(out var userId) ? userId : 0,
                root.TryGetProperty("eventType", out var e) ? e.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("functionName", out var f) ? f.GetString() ?? string.Empty : string.Empty,
                payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>审计轨迹里的一条。</summary>
public sealed record TraceEntry(
    DateTime Time,
    string SessionId,
    long UserId,
    string EventType,
    string FunctionName,
    JsonElement? Payload);
