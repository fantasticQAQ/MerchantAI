using MerchantAdmin.AI.API.Harness;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace MerchantAdmin.AI.API.Tests;

/// <summary>
/// 「从界面移除」= 软删除。
///
/// 起因是一次真实的困惑：用户删掉会话之后，左侧列表里又冒出一条「（会话已删除）／仅剩轨迹」——
/// 因为当时的实现把「会话删了」和「轨迹还在」拼在了一起展示。
/// 现在的契约：
/// - 删会话 → 会话**和它的轨迹**一起从界面消失；
/// - 删轨迹 → 只藏轨迹，会话还在；
/// - **两种情况都不动 Redis 里的原始数据**（会话历史和审计轨迹都要求永久保留）。
/// </summary>
public class SoftDeleteTests
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

    // ------------------------------------------------------------ 内存实现

    [Fact]
    public void 隐藏会话会连它的轨迹一起隐藏()
    {
        var store = new InMemoryVisibilityStore();

        Assert.False(store.IsSessionHidden(7, "s1"));
        Assert.False(store.IsTraceHidden(7, "s1"));

        store.HideSession(7, "s1");

        Assert.True(store.IsSessionHidden(7, "s1"));
        // 关键：会话藏了，轨迹也必须藏 —— 否则列表里就只剩一条没有会话的孤儿轨迹
        Assert.True(store.IsTraceHidden(7, "s1"));
    }

    [Fact]
    public void 只隐藏轨迹时会话照常可见()
    {
        var store = new InMemoryVisibilityStore();
        store.HideTraces(7, "s1");

        Assert.False(store.IsSessionHidden(7, "s1"));
        Assert.True(store.IsTraceHidden(7, "s1"));
    }

    /// <summary>隐藏是按用户隔离的：同一个 sessionId 在不同用户下是两个会话，不能互相藏。</summary>
    [Fact]
    public void 隐藏标记按用户隔离()
    {
        var store = new InMemoryVisibilityStore();
        store.HideSession(7, "shared-id");

        Assert.True(store.IsSessionHidden(7, "shared-id"));
        Assert.False(store.IsSessionHidden(8, "shared-id"));
        Assert.Contains("shared-id", store.HiddenSessions(7));
        Assert.DoesNotContain("shared-id", store.HiddenSessions(8));
    }

    /// <summary>
    /// 最重要的一条：隐藏**不是删除**。
    /// 会话历史必须原封不动地留在存储里 —— 不然「永久保留」就成了一句空话。
    /// </summary>
    [Fact]
    public async Task 隐藏之后会话数据仍然完整保留()
    {
        var conversations = new InMemoryConversationStore(Config());
        var visibility = new InMemoryVisibilityStore();

        await conversations.UseAsync("s1", 7, "tester", history =>
        {
            history.AddUserMessage("有哪些商品？");
            history.AddAssistantMessage("共 5 个。");
            return Task.FromResult(0);
        });

        visibility.HideSession(7, "s1");

        // 列表里看不见了……
        Assert.True(visibility.IsSessionHidden(7, "s1"));
        // ……但存储层一条没少
        var restored = await conversations.GetMessagesAsync("s1", 7);
        Assert.NotNull(restored);
        Assert.Equal(2, restored!.Count);
        Assert.Equal("有哪些商品？", restored[0].Content);
    }

    // ------------------------------------------------------------ Redis 实现

    [SkippableFact]
    public void Redis隐藏标记跨实例可见()
    {
        using var connection = TryConnect();
        Skip.If(connection is null, "本机没有可用的 Redis");

        var sessionId = $"test-{Guid.NewGuid():N}";
        var configuration = Config(("Ai:Redis:ConnectionString", ConnectionString));

        var storeA = new RedisVisibilityStore(connection!, NullLogger<RedisVisibilityStore>.Instance);
        storeA.HideSession(TestUser.Id, sessionId);

        // 另一个实例（模拟重启/多实例）也应该看到这条标记
        var storeB = new RedisVisibilityStore(connection!, NullLogger<RedisVisibilityStore>.Instance);
        Assert.True(storeB.IsSessionHidden(TestUser.Id, sessionId));
        Assert.True(storeB.IsTraceHidden(TestUser.Id, sessionId));
        Assert.Contains(sessionId, storeB.HiddenSessions(TestUser.Id));

        // 别人的标记不受影响
        Assert.DoesNotContain(sessionId, storeB.HiddenSessions(TestUser.Id + 1));

        connection!.Database!.SetRemove("merchant-ai:hidden:sessions", $"{TestUser.Id}:{sessionId}");
    }

    /// <summary>Redis 里的会话键不该因为「删除」而消失 —— 这就是整个软删除的意义。</summary>
    [SkippableFact]
    public async Task Redis里隐藏会话后原始数据仍在()
    {
        using var connection = TryConnect();
        Skip.If(connection is null, "本机没有可用的 Redis");

        var sessionId = $"test-{Guid.NewGuid():N}";
        var configuration = Config(("Ai:Redis:ConnectionString", ConnectionString));

        var conversations = new RedisConversationStore(
            connection!, configuration, NullLogger<RedisConversationStore>.Instance);

        await conversations.UseAsync(sessionId, TestUser.Id, TestUser.Name, history =>
        {
            history.AddUserMessage("删除测试");
            return Task.FromResult(0);
        });

        var key = $"merchant-ai:conv:{TestUser.Id}:{sessionId}";
        var database = connection!.Database!;
        Assert.True(await database.KeyExistsAsync(key));

        var visibility = new RedisVisibilityStore(connection, NullLogger<RedisVisibilityStore>.Instance);
        visibility.HideSession(TestUser.Id, sessionId);

        // 界面上藏了，Redis 里那条会话键必须还在，内容也没变
        Assert.True(visibility.IsSessionHidden(TestUser.Id, sessionId));
        Assert.True(await database.KeyExistsAsync(key));
        Assert.NotNull(await conversations.GetMessagesAsync(sessionId, TestUser.Id));

        await database.KeyDeleteAsync(key);
        database.SetRemove("merchant-ai:hidden:sessions", $"{TestUser.Id}:{sessionId}");
        conversations.Clear(sessionId, TestUser.Id);
    }
}
