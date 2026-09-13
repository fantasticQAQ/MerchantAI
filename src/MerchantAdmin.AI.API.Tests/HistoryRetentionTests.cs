using MerchantAdmin.AI.API.Harness;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace MerchantAdmin.AI.API.Tests;

/// <summary>
/// 保留策略：
/// - 会话历史上限从 40 提到 500（工具脚手架已经被 ChatHistoryCompactor 丢掉了，500 条全是有效对话）；
/// - `Ai:History:TtlMinutes = 0` 表示**永不过期**；
/// - 审计轨迹除了本地 JSONL 还写 Redis，而且**不带过期时间**。
/// </summary>
public class HistoryRetentionTests
{
    private const string ConnectionString = "localhost:6379,defaultDatabase=15,abortConnect=false,connectTimeout=2000";

    private static IConfiguration Config(params (string Key, string? Value)[] settings)
    {
        var values = new Dictionary<string, string?>();
        foreach (var (key, value) in settings) values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static RedisConnection? TryConnect()
    {
        var connection = new RedisConnection(
            Config(("Ai:Redis:ConnectionString", ConnectionString)), NullLogger<RedisConnection>.Instance);
        if (connection.TryPing()) return connection;
        connection.Dispose();
        return null;
    }

    // ---------------------------------------------------------------- 内存实现

    /// <summary>回归：默认上限是 500，不是 40 —— 60 条普通对话不该被裁掉任何一条。</summary>
    [Fact]
    public async Task 默认保留500条历史而不是40条()
    {
        var store = new InMemoryConversationStore(Config());

        await store.UseAsync("s1", 7, "tester", history =>
        {
            for (var i = 0; i < 60; i++)
            {
                history.AddUserMessage($"问题 {i}");
                history.AddAssistantMessage($"答复 {i}");
            }
            return Task.FromResult(0);
        });

        var restored = await store.UseAsync("s1", 7, "tester", history => Task.FromResult(history.ToList()));

        Assert.Equal(120, restored.Count);
        Assert.Equal("问题 0", restored[0].Content);
        Assert.Equal("答复 59", restored[^1].Content);
    }

    /// <summary>上限配小了仍然照常裁剪（500 只是默认值，不是写死的）。</summary>
    [Fact]
    public async Task 上限配成10时只留最后10条()
    {
        var store = new InMemoryConversationStore(Config(("Ai:History:MaxMessages", "10")));

        await store.UseAsync("s1", 7, "tester", history =>
        {
            for (var i = 0; i < 20; i++) history.AddUserMessage($"问题 {i}");
            return Task.FromResult(0);
        });

        var restored = await store.UseAsync("s1", 7, "tester", history => Task.FromResult(history.ToList()));

        Assert.Equal(10, restored.Count);
        Assert.Equal("问题 10", restored[0].Content);
    }

    // ---------------------------------------------------------------- Redis

    /// <summary>TtlMinutes=0 → 会话键**没有过期时间**（Redis 里 TTL 为 -1）。</summary>
    [SkippableFact]
    public async Task 会话历史Ttl为0时不设过期时间()
    {
        using var connection = TryConnect();
        Skip.If(connection is null, "本机没有可用的 Redis");

        var sessionId = $"test-{Guid.NewGuid():N}";
        var configuration = Config(
            ("Ai:Redis:ConnectionString", ConnectionString),
            ("Ai:History:TtlMinutes", "0"));

        var store = new RedisConversationStore(connection!, configuration, NullLogger<RedisConversationStore>.Instance);
        await store.UseAsync(sessionId, TestUser.Id, TestUser.Name, history =>
        {
            history.AddUserMessage("永久保留测试");
            return Task.FromResult(0);
        });

        var database = connection!.Database!;
        var key = $"merchant-ai:conv:{TestUser.Id}:{sessionId}";
        var ttl = await database.KeyTimeToLiveAsync(key);

        Assert.True(await database.KeyExistsAsync(key), "会话键应该存在");
        Assert.Null(ttl);   // null = 没有设置过期时间

        await database.KeyDeleteAsync(key);
        store.Clear(sessionId, TestUser.Id);
    }

    /// <summary>TtlMinutes 配了正数时，过期时间照样生效 —— 别把「永久」做成写死的。</summary>
    [SkippableFact]
    public async Task 会话历史Ttl为正数时仍然会过期()
    {
        using var connection = TryConnect();
        Skip.If(connection is null, "本机没有可用的 Redis");

        var sessionId = $"test-{Guid.NewGuid():N}";
        var configuration = Config(
            ("Ai:Redis:ConnectionString", ConnectionString),
            ("Ai:History:TtlMinutes", "30"));

        var store = new RedisConversationStore(connection!, configuration, NullLogger<RedisConversationStore>.Instance);
        await store.UseAsync(sessionId, TestUser.Id, TestUser.Name, history =>
        {
            history.AddUserMessage("30 分钟后过期");
            return Task.FromResult(0);
        });

        var database = connection!.Database!;
        var key = $"merchant-ai:conv:{TestUser.Id}:{sessionId}";
        var ttl = await database.KeyTimeToLiveAsync(key);

        Assert.NotNull(ttl);
        Assert.InRange(ttl!.Value.TotalSeconds, 60, 30 * 60);

        await database.KeyDeleteAsync(key);
        store.Clear(sessionId, TestUser.Id);
    }

    /// <summary>审计轨迹写进 Redis，能原样读回来，而且**没有过期时间**。</summary>
    [SkippableFact]
    public async Task 审计轨迹写入Redis永久保留()
    {
        using var connection = TryConnect();
        Skip.If(connection is null, "本机没有可用的 Redis");

        var sessionId = $"test-{Guid.NewGuid():N}";
        var date = DateOnly.FromDateTime(DateTime.Now);
        var listKey = $"merchant-ai:trace:{date:yyyyMMdd}";
        var datesKey = "merchant-ai:trace:dates";

        var database = connection!.Database!;
        await database.KeyDeleteAsync(listKey);

        var configuration = Config(
            ("Ai:Redis:ConnectionString", ConnectionString),
            ("Ai:Trace:Redis", "true"),
            ("Ai:Trace:File", "false"));   // 这条用例只关心 Redis，别在本地留文件

        var trace = new AgentTraceService(configuration, connection, NullLogger<AgentTraceService>.Instance);

        trace.Record(sessionId, "tool_pre", "create_product", new { name = "测试商品001" }, TestUser.Id);
        trace.Record(sessionId, "answer", string.Empty, "已经建好了", TestUser.Id);

        var entries = trace.Read(limit: 10, sessionId: sessionId);

        Assert.Equal(2, entries.Count);
        // 倒序：最新的在前
        Assert.Equal("answer", entries[0].EventType);
        Assert.Equal("create_product", entries[1].FunctionName);
        Assert.Equal(TestUser.Id, entries[1].UserId);
        // 中文没有被转义成 \uXXXX，审计是给人看的
        Assert.Contains("测试商品001", entries[1].Payload?.ToString());

        Assert.Contains(date, trace.AvailableDates());
        Assert.Null(await database.KeyTimeToLiveAsync(listKey));   // 永久
        Assert.Null(await database.KeyTimeToLiveAsync(datesKey));

        await database.KeyDeleteAsync(listKey);
        await database.SetRemoveAsync(datesKey, date.ToString("yyyyMMdd"));
    }

    /// <summary>只写文件时（Redis 关掉）读取走文件，行为不变。</summary>
    [Fact]
    public void 只写文件时也能正常读写()
    {
        var temp = Directory.CreateTempSubdirectory("merchant-ai-trace-");
        try
        {
            var trace = new AgentTraceService(temp.FullName);
            trace.Record("s-file", "tool_post", "list_products", new { total = 5 }, 9);

            var entries = trace.Read(limit: 10, sessionId: "s-file");

            Assert.Single(entries);
            Assert.Equal("list_products", entries[0].FunctionName);
            Assert.Equal(9, entries[0].UserId);
            Assert.Contains(DateOnly.FromDateTime(DateTime.Now), trace.AvailableDates());
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }
}
