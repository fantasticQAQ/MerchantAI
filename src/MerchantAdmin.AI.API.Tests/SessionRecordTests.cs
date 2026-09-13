using System.Text.Json.Nodes;
using MerchantAdmin.AI.API.Ai.Tools;
using MerchantAdmin.AI.API.Harness;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using StackExchange.Redis;
using Xunit;

namespace MerchantAdmin.AI.API.Tests;

/// <summary>
/// 会话记录 / 审计轨迹的读取能力，以及**按用户隔离**。
/// 在此之前这些数据是"只写不读"的：写进去了，但没有任何地方能看；
/// 而且能看之后，必须保证每个账号只看得到自己的。
/// </summary>
public class SessionRecordTests
{
    private static IConfiguration Config(params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?> { ["Ai:History:MaxMessages"] = "40" };
        foreach (var (key, value) in settings) values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static async Task SeedAsync(
        IConversationStore store, string sessionId, string firstUserMessage,
        long userId = TestUser.Id, string userName = TestUser.Name, int extraTurns = 0)
    {
        await store.UseAsync(sessionId, userId, userName, history =>
        {
            history.AddUserMessage(firstUserMessage);
            history.AddAssistantMessage("好的。");
            for (var i = 0; i < extraTurns; i++)
            {
                history.AddUserMessage($"追加 {i}");
                history.AddAssistantMessage($"回复 {i}");
            }
            return Task.FromResult(0);
        });
    }

    // ------------------------------------------------------------ 列表与详情

    [Fact]
    public async Task 会话列表带标题和条数()
    {
        var store = new InMemoryConversationStore(Config());
        await SeedAsync(store, "s1", "有哪些商品？");
        await SeedAsync(store, "s2", "帮我看看经营统计");

        var sessions = await store.ListSessionsAsync(TestUser.Id);

        Assert.Equal(2, sessions.Count);
        Assert.Contains(sessions, s => s.SessionId == "s1" && s.Title == "有哪些商品？");
        Assert.Contains(sessions, s => s.SessionId == "s2" && s.Title == "帮我看看经营统计");
        Assert.All(sessions, s => Assert.Equal(2, s.MessageCount));
    }

    [Fact]
    public async Task 会话列表按最后活跃时间倒序()
    {
        var store = new InMemoryConversationStore(Config());
        await SeedAsync(store, "old", "第一条");
        await Task.Delay(15);
        await SeedAsync(store, "new", "第二条");

        var sessions = await store.ListSessionsAsync(TestUser.Id);

        Assert.Equal("new", sessions[0].SessionId);
    }

    [Fact]
    public async Task 标题太长会被截断()
    {
        var store = new InMemoryConversationStore(Config());
        await SeedAsync(store, "s1", new string('商', 60));

        var session = Assert.Single(await store.ListSessionsAsync(TestUser.Id));

        Assert.True(session.Title.Length <= 25, session.Title);
        Assert.EndsWith("…", session.Title);
    }

    [Fact]
    public async Task 取不到的历史返回null而不是空列表()
    {
        var store = new InMemoryConversationStore(Config());

        // null 表示"会话不存在"，前端据此提示会话过期；空列表会被当成"这是个空会话"
        Assert.Null(await store.GetMessagesAsync("not-exist", TestUser.Id));
    }

    [Fact]
    public async Task 对话内容可以按会话读回()
    {
        var store = new InMemoryConversationStore(Config());
        await SeedAsync(store, "s1", "有哪些商品？");

        var messages = await store.GetMessagesAsync("s1", TestUser.Id);

        Assert.NotNull(messages);
        Assert.Equal(2, messages!.Count);
        Assert.Equal("有哪些商品？", messages[0].Content);
    }

    // ------------------------------------------------------------ 按用户隔离

    [Fact]
    public async Task 看不到别人的会话()
    {
        var store = new InMemoryConversationStore(Config());
        await SeedAsync(store, "mine", "我的会话", TestUser.Id, TestUser.Name);
        await SeedAsync(store, "theirs", "别人的会话", TestUser.OtherId, TestUser.OtherName);

        var mine = await store.ListSessionsAsync(TestUser.Id);
        var theirs = await store.ListSessionsAsync(TestUser.OtherId);

        Assert.Single(mine);
        Assert.Equal("mine", mine[0].SessionId);
        Assert.Single(theirs);
        Assert.Equal("theirs", theirs[0].SessionId);

        // 别人的会话直接当作"不存在"，不泄露"它存在但不属于你"
        Assert.Null(await store.GetMessagesAsync("theirs", TestUser.Id));
        Assert.Null(await store.GetMessagesAsync("mine", TestUser.OtherId));
    }

    [Fact]
    public async Task 同一个会话ID在不同用户下互不干扰()
    {
        var store = new InMemoryConversationStore(Config());
        // sessionId 是客户端生成的，同一个浏览器换账号登录时会复用 —— 必须各用各的
        await SeedAsync(store, "same-id", "甲的消息", TestUser.Id, "甲");
        await SeedAsync(store, "same-id", "乙的消息", TestUser.OtherId, "乙");

        var a = await store.GetMessagesAsync("same-id", TestUser.Id);
        var b = await store.GetMessagesAsync("same-id", TestUser.OtherId);

        Assert.Equal("甲的消息", a![0].Content);
        Assert.Equal("乙的消息", b![0].Content);
        Assert.Single(await store.ListSessionsAsync(TestUser.Id));
        Assert.Single(await store.ListSessionsAsync(TestUser.OtherId));
    }

    // ------------------------------------------------------------ 待确认操作

    [Fact]
    public void 按会话能查出未确认的操作()
    {
        var store = new InMemoryPendingActionStore();
        store.Add(NewAction("a1", "session-1", TestUser.Id));
        store.Add(NewAction("a2", "session-1", TestUser.Id));
        store.Add(NewAction("a3", "session-2", TestUser.Id));

        Assert.Equal(2, store.ListBySession("session-1").Count);
        Assert.Single(store.ListBySession("session-2"));
        Assert.Empty(store.ListBySession("session-3"));

        store.Remove("a1");
        Assert.Single(store.ListBySession("session-1"));
    }

    [Fact]
    public async Task 确认别人的待确认操作会被拒绝()
    {
        var pending = new InMemoryPendingActionStore();
        var catalog = ToolCatalog.Load(TestHarness.LocateRealToolsJson(), "http://localhost", null, SwaggerFixture.Operations);
        var invoker = new HttpToolInvoker(new NeverUsedClientFactory(), catalog, NullLogger<HttpToolInvoker>.Instance);
        var summaries = new PendingSummaryRegistry(PendingSummaryRegistry.DiscoverFormatterTypes()
            .Select(t => (IPendingSummaryFormatter)Activator.CreateInstance(t)!));
        var approvals = new HumanApprovalService(
            pending, catalog, invoker, summaries,
            new AgentTraceService(Path.Combine(Path.GetTempPath(), "merchant-ai-tests", Guid.NewGuid().ToString("N")), NullLogger<AgentTraceService>.Instance),
            NullLogger<HumanApprovalService>.Instance);

        pending.Add(NewAction("theirs", "s1", TestUser.OtherId));

        var outcome = await approvals.ConfirmAsync("theirs", "s1", TestUser.Id);

        Assert.False(outcome.Success);
        Assert.Contains("无权确认", outcome.Error);
        Assert.NotNull(pending.Get("theirs"));   // 没有被消费掉
    }

    // ------------------------------------------------------------ 审计轨迹

    [Fact]
    public void 轨迹写入后能读回来并且最新在前()
    {
        using var temp = new TempDirectory();
        var trace = new AgentTraceService(temp.Path);

        trace.Record("s1", "user_input", string.Empty, "有哪些商品？", TestUser.Id);
        trace.Record("s1", "tool_pre", "list_products", null, TestUser.Id);
        trace.Record("s1", "answer", string.Empty, "共 5 个商品", TestUser.Id);

        var entries = trace.Read();

        Assert.Equal(3, entries.Count);
        Assert.Equal("answer", entries[0].EventType);          // 最新在前
        Assert.Equal("有哪些商品？", entries[2].Payload!.Value.GetString());
        Assert.All(entries, e => Assert.Equal(TestUser.Id, e.UserId));
    }

    [Fact]
    public void 轨迹可以按会话事件类型和用户过滤()
    {
        using var temp = new TempDirectory();
        var trace = new AgentTraceService(temp.Path);

        trace.Record("s1", "user_input", string.Empty, "A", TestUser.Id);
        trace.Record("s2", "user_input", string.Empty, "B", TestUser.OtherId);
        trace.Record("s1", "answer", string.Empty, "C", TestUser.Id);

        Assert.Equal(2, trace.Read(sessionId: "s1").Count);
        Assert.Equal(2, trace.Read(eventType: "user_input").Count);
        Assert.Single(trace.Read(sessionId: "s1", eventType: "answer"));

        // 非管理员只看自己的
        Assert.Equal(2, trace.Read(userId: TestUser.Id).Count);
        Assert.Single(trace.Read(userId: TestUser.OtherId));
    }

    [Fact]
    public void 轨迹里的坏行会被跳过而不是让整个读取失败()
    {
        using var temp = new TempDirectory();
        var trace = new AgentTraceService(temp.Path);

        trace.Record("s1", "user_input", string.Empty, "正常的一行");
        // 模拟进程被杀时写了一半
        File.AppendAllText(Path.Combine(temp.Path, $"trace-{DateTime.Now:yyyyMMdd}.jsonl"), """{"time":"2026-0""" + Environment.NewLine);
        trace.Record("s1", "answer", string.Empty, "又一行正常的");

        var entries = trace.Read();

        Assert.Equal(2, entries.Count);
        Assert.DoesNotContain(entries, e => e.EventType.Length == 0);
    }

    [Fact]
    public void 没有轨迹文件时返回空而不是抛异常()
    {
        using var temp = new TempDirectory();
        var trace = new AgentTraceService(temp.Path);

        Assert.Empty(trace.Read());
        Assert.Empty(trace.AvailableDates());
    }

    [Fact]
    public void 能列出有轨迹的日期()
    {
        using var temp = new TempDirectory();
        var trace = new AgentTraceService(temp.Path);
        trace.Record("s1", "user_input", string.Empty, "hi");

        var dates = trace.AvailableDates();

        Assert.Single(dates);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Now), dates[0]);
    }

    // ------------------------------------------------------------ Redis 上的元信息与隔离

    [SkippableFact]
    public async Task Redis里会话带元信息且能列出()
    {
        var connection = TryConnect();
        Skip.If(connection is null, "本机没有可用的 Redis");

        using (connection)
        {
            var store = NewRedisStore(connection!);
            var sessionId = $"rec-{Guid.NewGuid():N}";

            await SeedAsync(store, sessionId, "会话记录测试");

            var found = (await store.ListSessionsAsync(TestUser.Id)).FirstOrDefault(s => s.SessionId == sessionId);

            Assert.NotNull(found);
            Assert.Equal("会话记录测试", found!.Title);
            Assert.Equal(2, found.MessageCount);
            Assert.NotEqual(DateTime.MinValue, found.LastActiveAt);

            store.Clear(sessionId, TestUser.Id);
            Assert.DoesNotContain(await store.ListSessionsAsync(TestUser.Id), s => s.SessionId == sessionId);
        }
    }

    [SkippableFact]
    public async Task Redis里别人的会话也看不到()
    {
        var connection = TryConnect();
        Skip.If(connection is null, "本机没有可用的 Redis");

        using (connection)
        {
            var store = NewRedisStore(connection!);
            var sessionId = $"iso-{Guid.NewGuid():N}";

            await SeedAsync(store, sessionId, "甲建的会话", TestUser.Id, "甲");

            Assert.Null(await store.GetMessagesAsync(sessionId, TestUser.OtherId));
            Assert.DoesNotContain(await store.ListSessionsAsync(TestUser.OtherId), s => s.SessionId == sessionId);

            // 同一个 sessionId 在别人名下是一个全新的会话，互不影响
            await SeedAsync(store, sessionId, "乙自己的会话", TestUser.OtherId, "乙");
            var mine = await store.GetMessagesAsync(sessionId, TestUser.Id);
            var theirs = await store.GetMessagesAsync(sessionId, TestUser.OtherId);
            Assert.Equal("甲建的会话", mine![0].Content);
            Assert.Equal("乙自己的会话", theirs![0].Content);

            store.Clear(sessionId, TestUser.Id);
            store.Clear(sessionId, TestUser.OtherId);
        }
    }

    [SkippableFact]
    public async Task 加鉴权之前的旧会话不再可见()
    {
        var connection = TryConnect();
        Skip.If(connection is null, "本机没有可用的 Redis");

        using (connection)
        {
            var database = connection!.Database!;
            var sessionId = $"legacy-{Guid.NewGuid():N}";

            // 加鉴权之前写入的格式：裸 ChatHistory，没有 meta，也没有 userId 前缀
            var history = new ChatHistory();
            history.AddUserMessage("旧格式的会话");
            history.AddAssistantMessage("收到了");
            database.StringSet("merchant-ai:conv:" + sessionId, ChatHistorySerializer.Serialize(history), TimeSpan.FromMinutes(5));

            var store = NewRedisStore(connection);

            // 无法判定归属 —— 宁可看不见，也不能让所有人都看见
            Assert.Null(await store.GetMessagesAsync(sessionId, TestUser.Id));
            Assert.DoesNotContain(await store.ListSessionsAsync(TestUser.Id), s => s.SessionId == sessionId);

            database.KeyDelete("merchant-ai:conv:" + sessionId);
        }
    }

    [SkippableFact]
    public void Redis待确认操作能按会话查出()
    {
        var connection = TryConnect();
        Skip.If(connection is null, "本机没有可用的 Redis");

        using (connection)
        {
            var configuration = Config(("Ai:Redis:ConnectionString", "localhost:6379,defaultDatabase=15"));
            var store = new RedisPendingActionStore(connection!, configuration, NullLogger<RedisPendingActionStore>.Instance);
            var sessionId = $"pend-{Guid.NewGuid():N}";

            var a1 = NewAction(Guid.NewGuid().ToString("N"), sessionId, TestUser.Id);
            var a2 = NewAction(Guid.NewGuid().ToString("N"), sessionId, TestUser.Id);
            store.Add(a1);
            store.Add(a2);

            Assert.Equal(2, store.ListBySession(sessionId).Count);
            Assert.Equal(TestUser.Id, store.Get(a1.ActionId)!.UserId);   // 归属信息被持久化

            store.Remove(a1.ActionId);
            Assert.Single(store.ListBySession(sessionId));

            store.Remove(a2.ActionId);
            Assert.Empty(store.ListBySession(sessionId));
        }
    }

    // ------------------------------------------------------------ 辅助

    private static RedisConversationStore NewRedisStore(RedisConnection connection)
        => new(connection, Config(("Ai:Redis:ConnectionString", "localhost:6379,defaultDatabase=15")), NullLogger<RedisConversationStore>.Instance);

    private static PendingAction NewAction(string actionId, string sessionId, long userId) => new()
    {
        ActionId = actionId,
        ToolName = "create_order",
        SessionId = sessionId,
        UserId = userId,
        Summary = "1. 商品ID 1 × 1",
        Plan = new HttpRequestPlan
        {
            Method = "POST",
            Path = "/api/Orders/create",
            Body = JsonNode.Parse("""{"orderItems":[{"productId":1,"quantity":1}]}"""),
            Arguments = new Dictionary<string, JsonNode?>
            {
                ["orderItems"] = JsonNode.Parse("""[{"productId":1,"quantity":1}]""")
            }
        }
    };

    private static RedisConnection? TryConnect()
    {
        var connection = new RedisConnection(
            Config(("Ai:Redis:ConnectionString", "localhost:6379,defaultDatabase=15,abortConnect=false,connectTimeout=2000")),
            NullLogger<RedisConnection>.Instance);

        if (connection.TryPing()) return connection;

        connection.Dispose();
        return null;
    }

    /// <summary>确认别人的操作时不该真的发请求，所以这个工厂只会抛。</summary>
    private sealed class NeverUsedClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "merchant-ai-traces", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* ignore */ }
        }
    }
}
