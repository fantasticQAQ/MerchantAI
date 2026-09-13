using MerchantAdmin.AI.API.Ai.Tools;
using MerchantAdmin.AI.API.Harness;
using Xunit;

namespace MerchantAdmin.AI.API.Tests;

/// <summary>
/// Semantic Kernel 1.80 的 FunctionChoiceBehavior.Auto 没有「最多自动调用几轮工具」的上限，
/// 护栏只能自己做。这组测试锁住这个行为，防止以后被无意改掉。
/// </summary>
public class AgentLoopGuardTests
{
    private static AgentLoopOptions Options(int toolCalls, int approvals) => new()
    {
        MaxToolCallsPerTurn = toolCalls,
        MaxApprovalsPerTurn = approvals,
        TurnTimeoutSeconds = 30
    };

    [Fact]
    public async Task 超过工具调用上限后强制停止调用工具()
    {
        using var harness = new TestHarness(Options(toolCalls: 3, approvals: 10));
        harness.ResetTurn();

        var first = await harness.CallAsync("list_products", "{}");
        var second = await harness.CallAsync("list_products", "{}");
        var third = await harness.CallAsync("list_products", "{}");
        var fourth = await harness.CallAsync("list_products", "{}");

        Assert.DoesNotContain("上限", first);
        Assert.DoesNotContain("上限", second);
        Assert.DoesNotContain("上限", third);
        Assert.Contains("已达本轮工具调用上限 3 次", fourth);
        Assert.True(harness.HttpContext.Items[AgentLoopState.TruncatedKey] as bool?);
    }

    [Fact]
    public async Task 超过审批上限后不再登记新的写操作()
    {
        using var harness = new TestHarness(Options(toolCalls: 50, approvals: 2));
        harness.ResetTurn();

        await harness.CallAsync("create_order", """{"orderItems":[{"productId":1,"quantity":1}]}""");
        await harness.CallAsync("create_order", """{"orderItems":[{"productId":2,"quantity":1}]}""");
        var third = await harness.CallAsync("create_order", """{"orderItems":[{"productId":3,"quantity":1}]}""");

        Assert.Contains("待确认操作已达到上限 2 条", third);
        Assert.Equal(2, harness.PendingIds.Count);          // 限流前登记的仍然有效
    }

    [Fact]
    public void 循环护栏的计数是每轮独立的()
    {
        using var harness = new TestHarness(Options(toolCalls: 3, approvals: 3));
        harness.ResetTurn();

        Assert.Equal(0, AgentLoopState.Read(harness.HttpContext, AgentLoopState.ToolCallCountKey));
        AgentLoopState.Bump(harness.HttpContext, AgentLoopState.ToolCallCountKey);
        AgentLoopState.Bump(harness.HttpContext, AgentLoopState.ToolCallCountKey);
        Assert.Equal(2, AgentLoopState.Read(harness.HttpContext, AgentLoopState.ToolCallCountKey));

        harness.ResetTurn();
        Assert.Equal(0, AgentLoopState.Read(harness.HttpContext, AgentLoopState.ToolCallCountKey));
    }
}
