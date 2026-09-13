using System.Text.Json.Nodes;
using Xunit;

namespace MerchantAdmin.AI.API.Tests;

public class ApprovalFlowTests
{
    [Fact]
    public async Task 只读工具直接执行并解包响应()
    {
        using var harness = new TestHarness();

        var result = await harness.CallAsync("list_products", """{"page":1,"pageSize":10}""");

        Assert.Contains("\"name\":\"雪碧\"", result);
        Assert.DoesNotContain("\"success\"", result);       // 外层包装已解掉
        Assert.DoesNotContain("\\u96ea", result);           // 中文没有被转义
    }

    [Fact]
    public async Task 写工具只登记不执行()
    {
        using var harness = new TestHarness();

        var result = await harness.CallAsync("create_order",
            """{"orderItems":[{"productId":1,"quantity":1},{"productId":3,"quantity":2}]}""");

        Assert.Contains("待人工确认", result);
        Assert.Equal(0, harness.Merchant.OrderPostCount);

        var id = Assert.Single(harness.PendingIds);
        var action = harness.Pending.Get(id)!;
        Assert.Equal("create_order", action.ToolName);
        Assert.Equal(2, (action.Plan.Arguments["orderItems"] as JsonArray)!.Count);
        Assert.Equal("/api/Orders/create", action.Plan.Path);
    }

    [Fact]
    public async Task 确认后按契约重放请求()
    {
        using var harness = new TestHarness();

        await harness.CallAsync("create_order", """{"orderItems":[{"productId":1,"quantity":1},{"productId":3,"quantity":2}]}""");
        var id = Assert.Single(harness.PendingIds);

        var outcome = await harness.Approvals.ConfirmAsync(id, "test-session", harness.UserId);

        Assert.True(outcome.Success);
        Assert.Equal("1001", outcome.Data);
        Assert.Equal(1, harness.Merchant.OrderPostCount);
        Assert.Null(harness.Pending.Get(id));                 // 已消费，避免重复点击重复下单

        // $map 投影把模型没传、但后端隐式必填的字段补齐了
        var body = harness.Merchant.LastOrderBody!;
        Assert.Contains("\"orderItems\"", body);
        Assert.Contains("\"productName\":\"\"", body);
        Assert.Contains("\"productId\":3", body);
    }

    [Fact]
    public async Task 摘要使用orderItems格式化()
    {
        using var harness = new TestHarness();

        await harness.CallAsync("create_order", """{"orderItems":[{"productId":1,"quantity":1},{"productId":3,"quantity":2}]}""");
        var action = harness.Pending.Get(Assert.Single(harness.PendingIds))!;

        Assert.Contains("商品ID 1 × 1", action.Summary);
        Assert.Contains("商品ID 3 × 2", action.Summary);
    }

    [Theory]
    [InlineData("""{"orderItems":[]}""", "至少需要 1 项")]
    [InlineData("""{"orderItems":[{"productId":1}]}""", "quantity")]
    [InlineData("{}", "缺少必填参数 orderItems")]
    public async Task 参数非法时绝不登记待确认操作(string args, string expectedMessage)
    {
        using var harness = new TestHarness();

        var result = await harness.CallAsync("create_order", args);

        Assert.Contains("参数校验未通过", result);
        Assert.Contains(expectedMessage, result);
        Assert.Empty(harness.PendingIds);                     // 关键：不会生成一张内容为空的确认卡片
        Assert.Equal(0, harness.Merchant.OrderPostCount);
    }

    [Fact]
    public async Task 数组被序列化成字符串时能自动还原()
    {
        using var harness = new TestHarness();

        // 实测 DeepSeek 会这样传参
        var result = await harness.CallAsync("create_order",
            """{"orderItems":"[{\"productId\":2,\"quantity\":5}]"}""");

        Assert.Contains("待人工确认", result);
        var action = harness.Pending.Get(Assert.Single(harness.PendingIds))!;
        Assert.Equal(5, (action.Plan.Arguments["orderItems"] as JsonArray)![0]!["quantity"]!.GetValue<double>());
    }

    [Fact]
    public async Task 数字被写成字符串时能自动还原()
    {
        using var harness = new TestHarness();

        var result = await harness.CallAsync("list_products", """{"page":"1","pageSize":"10"}""");

        Assert.Contains("\"total\"", result);
        Assert.Contains("page=1&pageSize=10", harness.Merchant.LastUrl);

        var bad = await harness.CallAsync("list_products", """{"page":"abc"}""");
        Assert.Contains("参数校验未通过", bad);     // 真解析不了仍然拦下
    }

    [Fact]
    public async Task 批量确认可以一次执行多条()
    {
        using var harness = new TestHarness();
        harness.ResetTurn();

        for (var productId = 1; productId <= 5; productId++)
            await harness.CallAsync("create_order", $$"""{"orderItems":[{"productId":{{productId}},"quantity":1}]}""");

        var ids = harness.PendingIds.ToArray();
        Assert.Equal(5, ids.Length);

        var outcomes = await harness.Approvals.ConfirmManyAsync(ids, "test-session", harness.UserId);

        Assert.All(outcomes, o => Assert.True(o.Success));
        Assert.Equal(5, harness.Merchant.OrderPostCount);
        Assert.All(ids, id => Assert.Null(harness.Pending.Get(id)));
    }

    [Fact]
    public async Task 业务层失败也会消费掉待确认操作避免重复下单()
    {
        using var harness = new TestHarness();
        harness.Merchant.ReturnBusinessFailure = true;

        await harness.CallAsync("create_order", """{"orderItems":[{"productId":1,"quantity":1}]}""");
        var id = Assert.Single(harness.PendingIds);

        var outcome = await harness.Approvals.ConfirmAsync(id, "test-session", harness.UserId);

        Assert.True(outcome.Success);                          // 请求已送达
        Assert.Contains("库存不足", outcome.Data);             // 业务错误如实透出
        Assert.Null(harness.Pending.Get(id));                  // 但不会留着让用户重复点
    }
}
