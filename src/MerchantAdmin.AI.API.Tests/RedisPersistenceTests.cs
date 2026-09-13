using System.Text.Json;
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
/// Redis 持久化。本机没起 Redis 时整组跳过（用 [SkippableFact]），
/// 这样 CI / 别人的机器上不会因为外部依赖红掉，但起了 Redis 就一定会真跑。
/// </summary>
public class RedisPersistenceTests
{
    private const string ConnectionString = "localhost:6379,defaultDatabase=15,abortConnect=false,connectTimeout=2000";

    private static IConfiguration Config(params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?>
        {
            ["Ai:History:TtlMinutes"] = "10",
            ["Ai:Approval:TtlMinutes"] = "10"
        };
        foreach (var (key, value) in settings) values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static RedisConnection? TryConnect()
    {
        var connection = new RedisConnection(Config(("Ai:Redis:ConnectionString", ConnectionString)), NullLogger<RedisConnection>.Instance);
        if (connection.TryPing()) return connection;
        connection.Dispose();
        return null;
    }

    [SkippableFact]
    public async Task 会话历史写入Redis后新实例也能读到()
    {
        using var connection = TryConnect();
        Skip.If(connection is null, "本机没有可用的 Redis");

        var sessionId = $"test-{Guid.NewGuid():N}";
        var configuration = Config(("Ai:Redis:ConnectionString", ConnectionString));

        // 实例 A 写入
        var storeA = new RedisConversationStore(connection!, configuration, NullLogger<RedisConversationStore>.Instance);
        await storeA.UseAsync(sessionId, TestUser.Id, TestUser.Name, history =>
        {
            history.AddUserMessage("有哪些商品？");
            history.AddAssistantMessage("共 5 个商品。");
            return Task.FromResult(0);
        });

        // 实例 B（模拟另一个进程/重启后）读取
        var storeB = new RedisConversationStore(connection!, configuration, NullLogger<RedisConversationStore>.Instance);
        var restored = await storeB.UseAsync(sessionId, TestUser.Id, TestUser.Name, history => Task.FromResult(history.ToList()));

        Assert.Equal(2, restored.Count);
        Assert.Equal("有哪些商品？", restored[0].Content);
        Assert.Equal("共 5 个商品。", restored[1].Content);

        storeB.Clear(sessionId, TestUser.Id);
    }

    /// <summary>
    /// 工具脚手架在会话里不该长期留存。
    ///
    /// 回归背景：一次「建 1000 个商品」的操作每轮产生「1 条带 tool_calls 的助手消息 + 60 条工具结果」，
    /// 旧的裁剪逻辑（裁到 40 条后删开头的孤儿 tool 消息）会把剩下的几十条**全部删光**，
    /// 实测把一个 12631 字符 / 31 条的会话裁到只剩 1 条消息，标题退化成「（空会话）」，
    /// 模型彻底丢失进度，于是出现编号跳号、「补上测试商品120」这类胡言乱语。
    /// 现在的契约：工具脚手架丢掉，用户消息和助手的文字总结留着。
    /// </summary>
    [SkippableFact]
    public async Task 会话在Redis里往返后丢掉工具脚手架但保留叙述()
    {
        using var connection = TryConnect();
        Skip.If(connection is null, "本机没有可用的 Redis");

        var sessionId = $"test-{Guid.NewGuid():N}";
        var configuration = Config(("Ai:Redis:ConnectionString", ConnectionString));

        var store = new RedisConversationStore(connection!, configuration, NullLogger<RedisConversationStore>.Instance);
        await store.UseAsync(sessionId, TestUser.Id, TestUser.Name, history =>
        {
            history.AddUserMessage("有哪些商品？");

            // 一批工具调用：助手只带 tool_calls、没有文字
            history.Add(new ChatMessageContent(AuthorRole.Assistant, new ChatMessageContentItemCollection
            {
                new FunctionCallContent("list_products", "Store", "call_1",
                    new KernelArguments { ["page"] = JsonNode.Parse("1") })
            }));
            history.Add(new ChatMessageContent(AuthorRole.Tool, new ChatMessageContentItemCollection
            {
                new TextContent("""{"total":2}"""),
                new FunctionResultContent("list_products", "Store", "call_1", """{"total":2}""")
            }));

            // 助手随后的文字总结 —— 这个必须留下
            history.AddAssistantMessage("共 2 个商品。");
            return Task.FromResult(0);
        });

        var restored = await store.UseAsync(sessionId, TestUser.Id, TestUser.Name, history => Task.FromResult(history.ToList()));

        Assert.Equal(2, restored.Count);
        Assert.Equal("有哪些商品？", restored[0].Content);
        Assert.Equal("共 2 个商品。", restored[1].Content);

        // 脚手架确实清干净了：没有 tool 消息，也没有残留的 tool_calls（否则 OpenAI 会因为缺结果而拒绝请求）
        Assert.DoesNotContain(restored, m => m.Role == AuthorRole.Tool);
        Assert.DoesNotContain(restored, m => m.Items.OfType<FunctionCallContent>().Any());

        store.Clear(sessionId, TestUser.Id);
    }

    [SkippableFact]
    public void 待确认操作可以跨实例读取()
    {
        using var connection = TryConnect();
        Skip.If(connection is null, "本机没有可用的 Redis");

        var configuration = Config(("Ai:Redis:ConnectionString", ConnectionString));
        var actionId = Guid.NewGuid().ToString("N");

        // 实例 A 登记
        var storeA = new RedisPendingActionStore(connection!, configuration, NullLogger<RedisPendingActionStore>.Instance);
        storeA.Add(new PendingAction
        {
            ActionId = actionId,
            ToolName = "create_order",
            SessionId = "s1",
            Summary = "1. 商品ID 1 × 2",
            Plan = new HttpRequestPlan
            {
                Method = "POST",
                Path = "/api/Orders/create",
                Body = JsonNode.Parse("""{"orderItems":[{"productId":1,"quantity":2}]}"""),
                Arguments = new Dictionary<string, JsonNode?>
                {
                    ["orderItems"] = JsonNode.Parse("""[{"productId":1,"quantity":2}]""")
                }
            }
        });

        // 实例 B 确认 —— 这正是内存存储做不到的（会报「待确认操作不存在」）
        var storeB = new RedisPendingActionStore(connection!, configuration, NullLogger<RedisPendingActionStore>.Instance);
        var loaded = storeB.Get(actionId);

        Assert.NotNull(loaded);
        Assert.Equal("create_order", loaded!.ToolName);
        Assert.Equal("/api/Orders/create", loaded.Plan.Path);
        Assert.Equal(2, ((JsonArray)loaded.Plan.Arguments["orderItems"]!)[0]!["quantity"]!.GetValue<int>());

        Assert.True(storeB.Remove(actionId));
        Assert.Null(storeB.Get(actionId));
    }

    [SkippableFact]
    public void 连不上Redis时TryPing返回false以便优雅降级()
    {
        // 用一个几乎肯定没人监听的端口
        var connection = new RedisConnection(
            Config(("Ai:Redis:ConnectionString", "localhost:6399,abortConnect=false,connectTimeout=500")),
            NullLogger<RedisConnection>.Instance);

        using (connection)
        {
            Assert.True(connection.IsEnabled);
            Assert.False(connection.TryPing());
        }
    }

    [SkippableFact]
    public async Task 并发写同一会话不会丢消息()
    {
        using var connection = TryConnect();
        Skip.If(connection is null, "本机没有可用的 Redis");

        var sessionId = $"test-{Guid.NewGuid():N}";
        var configuration = Config(("Ai:Redis:ConnectionString", ConnectionString), ("Ai:History:MaxMessages", "200"));
        var store = new RedisConversationStore(connection!, configuration, NullLogger<RedisConversationStore>.Instance);

        // 20 个并发轮次，每轮追加一条
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => store.UseAsync(sessionId, TestUser.Id, TestUser.Name, history =>
        {
            history.AddUserMessage($"第 {i} 条");
            return Task.FromResult(0);
        })));

        var messages = await store.UseAsync(sessionId, TestUser.Id, TestUser.Name, history => Task.FromResult(history.ToList()));
        Assert.Equal(20, messages.Count);

        store.Clear(sessionId, TestUser.Id);
    }
}
