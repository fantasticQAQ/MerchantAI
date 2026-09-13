using MerchantAdmin.AI.API.Harness;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MerchantAdmin.AI.API.Tests;

/// <summary>
/// 会话偏好（标题 / 置顶 / 分组）。
///
/// 这几项刻意**不放在 SessionMetadata 里** —— 那份元信息是从对话内容推出来的
/// （标题取第一条用户消息、条数取 history.Count），每次保存都会整体重写，用户设置放进去会被冲掉。
/// 这里把它单独存，并保证它和会话一样永久保留。
/// </summary>
public class SessionPrefsTests
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

    // ------------------------------------------------------------ 基本读写

    [Fact]
    public void 没设置过时返回空偏好()
    {
        var store = new InMemorySessionPrefsStore();
        var prefs = store.Get(1, "s1");

        Assert.Null(prefs.Title);
        Assert.False(prefs.Pinned);
        Assert.Null(prefs.GroupId);
    }

    [Fact]
    public void 偏好按会话保存且互不影响()
    {
        var store = new InMemorySessionPrefsStore();

        store.Save(1, "s1", new SessionPrefs { Title = "甲", Pinned = true });
        store.Save(1, "s2", new SessionPrefs { Title = "乙" });

        Assert.Equal("甲", store.Get(1, "s1").Title);
        Assert.True(store.Get(1, "s1").Pinned);
        Assert.Equal("乙", store.Get(1, "s2").Title);
        Assert.False(store.Get(1, "s2").Pinned);
    }

    [Fact]
    public void 偏好按用户隔离()
    {
        var store = new InMemorySessionPrefsStore();
        store.Save(1, "same", new SessionPrefs { Title = "甲的" });

        Assert.Equal("甲的", store.Get(1, "same").Title);
        Assert.Null(store.Get(2, "same").Title);
    }

    [Fact]
    public void All只返回自己的且能一次取全()
    {
        var store = new InMemorySessionPrefsStore();
        store.Save(1, "a", new SessionPrefs { Title = "A" });
        store.Save(1, "b", new SessionPrefs { Pinned = true });
        store.Save(2, "c", new SessionPrefs { Title = "别人的" });

        var all = store.All(1);

        Assert.Equal(2, all.Count);
        Assert.Equal("A", all["a"].Title);
        Assert.True(all["b"].Pinned);
        Assert.DoesNotContain("c", all.Keys);
    }

    // ------------------------------------------------------------ 分组

    [Fact]
    public void 分组可以新建改名置顶删除()
    {
        var store = new InMemorySessionPrefsStore();

        var group = store.CreateGroup(1, "商品管理");
        Assert.Equal("商品管理", Assert.Single(store.Groups(1)).Name);
        Assert.False(group.Pinned);

        Assert.True(store.UpdateGroup(1, group.Id, name: "商品与库存", pinned: true));
        var updated = Assert.Single(store.Groups(1));
        Assert.Equal("商品与库存", updated.Name);
        Assert.True(updated.Pinned);

        // 只传一项时，另一项保持原样
        Assert.True(store.UpdateGroup(1, group.Id, name: null, pinned: false));
        var again = Assert.Single(store.Groups(1));
        Assert.Equal("商品与库存", again.Name);
        Assert.False(again.Pinned);

        store.DeleteGroup(1, group.Id);
        Assert.Empty(store.Groups(1));
    }

    [Fact]
    public void 改不存在的分组返回false()
    {
        var store = new InMemorySessionPrefsStore();
        Assert.False(store.UpdateGroup(1, "nope", "x", null));
    }

    [Fact]
    public void 分组按用户隔离()
    {
        var store = new InMemorySessionPrefsStore();
        store.CreateGroup(1, "甲的分组");

        Assert.Single(store.Groups(1));
        Assert.Empty(store.Groups(2));
    }

    // ------------------------------------------------------------ Redis

    [SkippableFact]
    public void Redis偏好跨实例可见()
    {
        using var connection = TryConnect();
        Skip.If(connection is null, "本机没有可用的 Redis");

        var user = 900_000 + Random.Shared.Next(1, 100_000);
        var sessionId = $"test-{Guid.NewGuid():N}";

        var storeA = new RedisSessionPrefsStore(connection!, NullLogger<RedisSessionPrefsStore>.Instance);
        storeA.Save(user, sessionId, new SessionPrefs { Title = "跨实例", Pinned = true });
        var group = storeA.CreateGroup(user, "甲组");

        // 另一个实例（模拟重启/多实例）
        var storeB = new RedisSessionPrefsStore(connection!, NullLogger<RedisSessionPrefsStore>.Instance);

        Assert.Equal("跨实例", storeB.Get(user, sessionId).Title);
        Assert.True(storeB.Get(user, sessionId).Pinned);
        Assert.Equal("甲组", Assert.Single(storeB.Groups(user)).Name);

        storeB.DeleteGroup(user, group.Id);
        connection!.Database!.KeyDelete($"merchant-ai:prefs:{user}");
        connection.Database.KeyDelete($"merchant-ai:groups:{user}");
    }

    [SkippableFact]
    public void Redis偏好不带过期时间()
    {
        using var connection = TryConnect();
        Skip.If(connection is null, "本机没有可用的 Redis");

        var user = 900_000 + Random.Shared.Next(1, 100_000);
        var store = new RedisSessionPrefsStore(connection!, NullLogger<RedisSessionPrefsStore>.Instance);
        store.Save(user, "s1", new SessionPrefs { Title = "永久" });
        store.CreateGroup(user, "永久分组");

        // 置顶和分组要是自己过期了，用户的整理就白做了 —— 必须没有 TTL
        Assert.Null(connection!.Database!.KeyTimeToLive($"merchant-ai:prefs:{user}"));
        Assert.Null(connection.Database.KeyTimeToLive($"merchant-ai:groups:{user}"));

        connection.Database.KeyDelete($"merchant-ai:prefs:{user}");
        connection.Database.KeyDelete($"merchant-ai:groups:{user}");
    }
}
