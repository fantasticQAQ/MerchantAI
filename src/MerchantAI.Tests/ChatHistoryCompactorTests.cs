using MerchantAdmin.AI.API.Harness;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

namespace MerchantAdmin.AI.API.Tests;

/// <summary>
/// 会话历史压缩的回归测试。
///
/// 这一组测试针对的是一个真实事故：一次「建 1000 个商品」跑完后，
/// Redis 里的会话从 12631 字符 / 31 条消息，被裁到只剩 636 字符 / 1 条消息 ——
/// 用户消息、进度、编号全部丢失，会话标题退化成「（空会话）」。
/// </summary>
public class ChatHistoryCompactorTests
{
    /// <summary>造一条「助手发起 N 次工具调用 + N 条工具结果」的消息块。</summary>
    private static void AddToolBatch(ChatHistory history, int count, string toolName = "create_product")
    {
        var calls = new ChatMessageContentItemCollection();
        for (var i = 0; i < count; i++)
            calls.Add(new FunctionCallContent(toolName, "Store", $"call_{i}", new KernelArguments { ["i"] = i }));

        history.Add(new ChatMessageContent(AuthorRole.Assistant, calls));

        for (var i = 0; i < count; i++)
            history.Add(new ChatMessageContent(AuthorRole.Tool, new ChatMessageContentItemCollection
            {
                new FunctionResultContent(toolName, "Store", $"call_{i}", $$"""{"ok":{{i}}}""")
            }));
    }

    /// <summary>
    /// 核心回归：一批 60 次工具调用（62 条消息）里，用户消息和助手的文字总结必须活下来。
    /// 旧实现在这里会返回 1 条（只剩最后的总结），用户消息被挤掉。
    /// </summary>
    [Fact]
    public void 批量工具调用不会把用户消息和总结挤掉()
    {
        var history = new ChatHistory();
        history.AddUserMessage("帮我建 1000 个商品");
        AddToolBatch(history, 60);
        history.AddAssistantMessage("本轮新建 60 个，ID 106–165，总计 165 个，还差 835 个。");

        ChatHistoryCompactor.Compact(history, maxMessages: 40);

        Assert.Equal(2, history.Count);
        Assert.Equal(AuthorRole.User, history[0].Role);
        Assert.Equal("帮我建 1000 个商品", history[0].Content);
        Assert.Equal("本轮新建 60 个，ID 106–165，总计 165 个，还差 835 个。", history[1].Content);
    }

    /// <summary>多轮批量之后，历史里仍然没有任何工具脚手架。</summary>
    [Fact]
    public void 多轮批量之后历史里不残留工具消息()
    {
        var history = new ChatHistory();
        for (var round = 0; round < 5; round++)
        {
            history.AddUserMessage($"第 {round} 轮");
            AddToolBatch(history, 60);
            history.AddAssistantMessage($"第 {round} 轮做完了");
        }

        ChatHistoryCompactor.Compact(history, maxMessages: 40);

        Assert.DoesNotContain(history, m => m.Role == AuthorRole.Tool);
        Assert.DoesNotContain(history, m => m.Items.OfType<FunctionCallContent>().Any());
        // 10 条叙述全部留得下（上限 40 远没到）
        Assert.Equal(10, history.Count);
    }

    /// <summary>
    /// 超过上限时从最旧的开始丢，且丢完仍然只剩「用户 / 助手文本」。
    /// 这是旧实现出第二个问题的地方：裁剪点落在一批工具结果中间会切出孤儿 tool 消息，被后续逻辑整段删光。
    /// </summary>
    [Fact]
    public void 超过上限时丢最旧的且不产生孤儿工具结果()
    {
        var history = new ChatHistory();
        for (var round = 0; round < 10; round++)
        {
            history.AddUserMessage($"问题 {round}");
            AddToolBatch(history, 5);
            history.AddAssistantMessage($"答复 {round}");
        }

        ChatHistoryCompactor.Compact(history, maxMessages: 6);

        Assert.Equal(6, history.Count);
        Assert.Equal("问题 7", history[0].Content);
        Assert.Equal("答复 9", history[^1].Content);
        Assert.DoesNotContain(history, m => m.Role == AuthorRole.Tool);
    }

    /// <summary>只带 tool_calls、没有文字的助手消息在清掉调用后就是个空壳，必须整条丢掉（空消息会被 OpenAI 拒绝）。</summary>
    [Fact]
    public void 清理后变空的助手消息被丢掉()
    {
        var history = new ChatHistory();
        history.AddUserMessage("查一下商品");
        AddToolBatch(history, 2);
        history.AddAssistantMessage("共 2 个商品。");

        ChatHistoryCompactor.Compact(history, maxMessages: 40);

        Assert.Equal(2, history.Count);
        Assert.All(history, m => Assert.False(string.IsNullOrWhiteSpace(m.Content)));
    }

    /// <summary>
    /// 带工具调用的助手消息 —— 哪怕它同时带了文字 —— 整条丢掉。
    ///
    /// 那条文字就是模型调工具前说的前言。实时对话只显示本轮的**最后一条**助手消息，
    /// 前言看不见；刷新页面走的是「列出全部历史消息」，它又冒出来了，
    /// 于是同一段对话刷新前后不一样（实测冒出来一句英文的
    /// "I'll query the current products and orders for you."）。
    /// </summary>
    [Fact]
    public void 带文字的工具调用消息整条丢掉()
    {
        var history = new ChatHistory();
        history.AddUserMessage("查一下商品");
        history.Add(new ChatMessageContent(AuthorRole.Assistant, new ChatMessageContentItemCollection
        {
            new TextContent("I'll query the current products for you."),
            new FunctionCallContent("list_products", "Store", "call_1", new KernelArguments())
        }));
        history.Add(new ChatMessageContent(AuthorRole.Tool, new ChatMessageContentItemCollection
        {
            new FunctionResultContent("list_products", "Store", "call_1", """{"total":2}""")
        }));
        history.AddAssistantMessage("共 2 个商品。");

        ChatHistoryCompactor.Compact(history, maxMessages: 40);

        Assert.Equal(2, history.Count);
        Assert.Equal("查一下商品", history[0].Content);
        Assert.Equal("共 2 个商品。", history[1].Content);
        Assert.DoesNotContain("I'll query", string.Join("|", history.Select(m => m.Content ?? string.Empty)));
    }

    /// <summary>没有工具调用时不该动任何东西 —— 普通的问答历史必须原样保留。</summary>
    [Fact]
    public void 普通对话不受影响()
    {
        var history = new ChatHistory();
        history.AddUserMessage("有哪些商品？");
        history.AddAssistantMessage("共 5 个商品。");
        history.AddUserMessage("第 3 个多少钱？");
        history.AddAssistantMessage("￥3.50。");

        ChatHistoryCompactor.Compact(history, maxMessages: 40);

        Assert.Equal(4, history.Count);
        Assert.Equal("有哪些商品？", history[0].Content);
        Assert.Equal("￥3.50。", history[^1].Content);
    }
}
