using MerchantAdmin.AI.API.Harness;
using Xunit;

namespace MerchantAdmin.AI.API.Tests;

/// <summary>
/// AI 权限级别（前端下拉框）。三层：仅可查看 / 需确认 / 完全权限。
///
/// 关键设计：这个级别只决定「写操作要不要人工点确认」，
/// 不决定「这个人有没有资格用这个工具」—— 后者由 tools.json 的 roles 单独把关。
/// </summary>
public class PermissionModeTests
{
    [Fact]
    public async Task 仅可查看时写操作被拒绝且不登记()
    {
        using var harness = new TestHarness();
        harness.SetMode(AiPermissionMode.ReadOnly);

        var result = await harness.CallAsync("create_order", """{"orderItems":[{"productId":1,"quantity":1}]}""");

        Assert.Contains("仅可查看", result);
        Assert.Empty(harness.PendingIds);                       // 连卡片都不该生成
        Assert.Equal(0, harness.Merchant.OrderPostCount);       // 更不能真的执行
    }

    [Fact]
    public async Task 仅可查看时只读工具照常可用()
    {
        using var harness = new TestHarness();
        harness.SetMode(AiPermissionMode.ReadOnly);

        var result = await harness.CallAsync("list_products", "{}");

        Assert.Contains("雪碧", result);
    }

    [Fact]
    public async Task 需确认时写操作登记待确认而不执行()
    {
        using var harness = new TestHarness();
        harness.SetMode(AiPermissionMode.Approve);

        var result = await harness.CallAsync("create_order", """{"orderItems":[{"productId":1,"quantity":1}]}""");

        Assert.Contains("待人工确认", result);
        Assert.Single(harness.PendingIds);
        Assert.Equal(0, harness.Merchant.OrderPostCount);
    }

    [Fact]
    public async Task 完全权限时写操作直接执行且不产生卡片()
    {
        using var harness = new TestHarness();
        harness.SetMode(AiPermissionMode.Full);

        var result = await harness.CallAsync("create_order", """{"orderItems":[{"productId":1,"quantity":1}]}""");

        Assert.Contains("1001", result);                        // 直接拿到业务返回的订单号
        Assert.Empty(harness.PendingIds);                       // 没有待确认卡片
        Assert.Equal(1, harness.Merchant.OrderPostCount);       // 真的发出去了
    }

    [Fact]
    public async Task 完全权限下仍然受角色限制()
    {
        using var harness = new TestHarness();
        harness.SetMode(AiPermissionMode.Full);
        // delete_product 在 tools.json 里限定了 Admin/SuperAdmin
        harness.SetUser(TestUser.OtherId, "operator", "Operator");

        var result = await harness.CallAsync("delete_product", """{"productId":5}""");

        Assert.Contains("权限", result);
        Assert.Equal(0, harness.Merchant.OrderPostCount);
    }

    [Fact]
    public async Task 完全权限下的直接执行会留下审计痕迹()
    {
        using var harness = new TestHarness();
        harness.SetMode(AiPermissionMode.Full);

        await harness.CallAsync("update_product", """{"productId":1,"body":{"isActive":false}}""");

        var events = harness.Trace.Read(sessionId: "test-session", limit: 50);
        Assert.Contains(events, e => e.EventType == "tool_auto_executed" && e.FunctionName == "update_product");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("approve")]
    [InlineData("readonly")]
    [InlineData("full")]
    [InlineData("不认识的模式")]
    public void 模式解析认不出来就退回最安全的默认值(string? text)
    {
        var mode = AgentPermission.Parse(text);

        var expected = text switch
        {
            "readonly" => AiPermissionMode.ReadOnly,
            "full" => AiPermissionMode.Full,
            _ => AiPermissionMode.Approve
        };
        Assert.Equal(expected, mode);
    }

    [Fact]
    public void 没设置模式时默认需确认()
    {
        // 漏传参数绝不能变成「无确认执行」
        Assert.Equal(AiPermissionMode.Approve, AgentPermission.Read(null));
        Assert.Equal(AiPermissionMode.Approve, AgentPermission.Read(new Microsoft.AspNetCore.Http.DefaultHttpContext()));
    }
}
