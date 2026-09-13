using Xunit;

namespace MerchantAdmin.AI.API.Tests;

/// <summary>
/// 后来补上的一批写工具：订单取消/支付/退款/删除、商品新建/删除。
/// 它们基本都靠 tools.json 的声明 + summaryTemplate 完成，没有为每个工具写 C# 代码。
/// </summary>
public class OrderActionToolsTests
{
    [Theory]
    [InlineData("cancel_order", "POST", "/api/Orders/7/cancel", "取消订单 #7")]
    [InlineData("pay_order", "POST", "/api/Orders/7/pay", "为订单 #7 发起支付")]
    [InlineData("refund_order", "POST", "/api/Orders/7/refund", "退款订单 #7")]
    [InlineData("delete_order", "DELETE", "/api/Orders/7", "删除订单 #7")]
    public async Task 订单类写操作按路径参数登记且不执行(string tool, string method, string path, string summaryFragment)
    {
        using var harness = new TestHarness();

        var result = await harness.CallAsync(tool, """{"orderId":7}""");

        Assert.Contains("待人工确认", result);
        Assert.Equal(0, harness.Merchant.OrderPostCount);

        var action = harness.Pending.Get(Assert.Single(harness.PendingIds))!;
        Assert.Equal(tool, action.ToolName);
        Assert.Equal(method, action.Plan.Method);
        Assert.Equal(path, action.Plan.Path);
        Assert.Empty(action.Plan.Query);            // 只有路径参数，不该拼出 querystring
        Assert.Null(action.Plan.Body);
        Assert.Contains(summaryFragment, action.Summary);
    }

    [Fact]
    public async Task 订单类写操作缺orderId会被拦下()
    {
        using var harness = new TestHarness();

        var result = await harness.CallAsync("cancel_order", "{}");

        Assert.Contains("参数校验未通过", result);
        Assert.Contains("orderId", result);
        Assert.Empty(harness.PendingIds);
    }

    [Fact]
    public async Task 退款摘要会提醒回补库存()
    {
        using var harness = new TestHarness();

        await harness.CallAsync("refund_order", """{"orderId":12}""");

        Assert.Contains("会回补库存", harness.Pending.Get(Assert.Single(harness.PendingIds))!.Summary);
    }

    [Fact]
    public async Task 删除商品摘要会提醒不可恢复()
    {
        using var harness = new TestHarness();

        await harness.CallAsync("delete_product", """{"productId":4}""");

        var action = harness.Pending.Get(Assert.Single(harness.PendingIds))!;
        Assert.Equal("DELETE", action.Plan.Method);
        Assert.Equal("/api/Products/4", action.Plan.Path);
        Assert.Contains("不可恢复", action.Summary);
    }

    [Fact]
    public async Task 新建商品把参数包进productDto()
    {
        using var harness = new TestHarness();

        await harness.CallAsync("create_product", """{"name":"芬达","price":3.5,"stock":50}""");
        var action = harness.Pending.Get(Assert.Single(harness.PendingIds))!;

        Assert.Equal("POST", action.Plan.Method);
        Assert.Equal("/api/Products", action.Plan.Path);
        // 后端契约要求包在 productDto 里
        var dto = action.Plan.Body!["productDto"]!;
        Assert.Equal("芬达", dto["name"]!.GetValue<string>());
        Assert.Equal(3.5, dto["price"]!.GetValue<double>());
        Assert.Equal(50, dto["stock"]!.GetValue<int>());
        Assert.Contains("新建商品：名称 芬达", action.Summary);
    }

    [Theory]
    [InlineData("""{"name":"芬达","price":0,"stock":1}""", "必须大于 0")]
    [InlineData("""{"name":"芬达","price":1,"stock":-5}""", "不能小于 0")]
    [InlineData("""{"name":"芬达","stock":1}""", "缺少必填参数 price")]
    public async Task 新建商品参数非法时不登记(string args, string expected)
    {
        using var harness = new TestHarness();

        var result = await harness.CallAsync("create_product", args);

        Assert.Contains("参数校验未通过", result);
        Assert.Contains(expected, result);
        Assert.Empty(harness.PendingIds);
    }

    [Fact]
    public async Task 确认后按契约重放订单操作()
    {
        using var harness = new TestHarness();

        await harness.CallAsync("cancel_order", """{"orderId":9}""");
        var id = Assert.Single(harness.PendingIds);

        var outcome = await harness.Approvals.ConfirmAsync(id, "test-session", harness.UserId);

        Assert.True(outcome.Success);
        Assert.Equal("POST", harness.Merchant.LastMethod);
        Assert.Equal("/api/Orders/9/cancel", harness.Merchant.LastUrl);
        Assert.Null(harness.Merchant.LastBody);
    }

    [Fact]
    public void 工具清单里的写操作都要求审批()
    {
        using var harness = new TestHarness();

        foreach (var name in new[] { "create_order", "create_product", "update_product", "delete_product",
                                     "cancel_order", "pay_order", "refund_order", "delete_order" })
        {
            var tool = harness.Catalog.GetRequired(name);
            Assert.Equal(Ai.Tools.ToolAccess.Write, tool.Access);
            Assert.True(tool.RequiresApproval, $"{name} 应当需要人工确认");
        }
    }
}
