using Xunit;

namespace MerchantAdmin.AI.API.Tests;

/// <summary>
/// 新增一个写工具（改商品）的回归。这个工具是「加功能只改 tools.json 一处」的实例：
/// 除了 tools.json 里的声明和一个摘要格式化器，Harness 的其它代码一行都不用动。
/// </summary>
public class UpdateProductToolTests
{
    [Fact]
    public async Task 改商品只登记不执行并按路径参数发请求()
    {
        using var harness = new TestHarness();

        var result = await harness.CallAsync("update_product",
            """{"productId":1,"body":{"name":"芬达","price":3.5,"stockDelta":42,"isActive":false}}""");

        Assert.Contains("待人工确认", result);
        Assert.Equal(0, harness.Merchant.OrderPostCount);

        var action = harness.Pending.Get(Assert.Single(harness.PendingIds))!;
        Assert.Equal("update_product", action.ToolName);
        Assert.Equal("PUT", action.Plan.Method);
        // {productId} 路径模板被正确替换
        Assert.Equal("/api/Products/1", action.Plan.Path);
        // body 原样透传，没有多余的 productId
        Assert.Equal("芬达", action.Plan.Body!["name"]!.GetValue<string>());
        Assert.Null(action.Plan.Body["productId"]);
    }

    [Fact]
    public async Task 摘要里把库存写成增量并提醒不是绝对值()
    {
        using var harness = new TestHarness();

        await harness.CallAsync("update_product",
            """{"productId":1,"body":{"name":"芬达","price":3.5,"stockDelta":42,"isActive":false}}""");

        var summary = harness.Pending.Get(Assert.Single(harness.PendingIds))!.Summary;

        Assert.Contains("商品ID 1", summary);
        Assert.Contains("芬达", summary);
        Assert.Contains("+42", summary);
        Assert.Contains("不是改成这个数", summary);
        Assert.Contains("下架", summary);
    }

    [Fact]
    public async Task 只改单价也能正确登记()
    {
        using var harness = new TestHarness();

        await harness.CallAsync("update_product", """{"productId":3,"body":{"price":9.9}}""");

        var action = harness.Pending.Get(Assert.Single(harness.PendingIds))!;
        Assert.Equal("/api/Products/3", action.Plan.Path);
        Assert.Contains("单价 → 9.9", action.Summary);
        Assert.DoesNotContain("下架", action.Summary);
    }

    [Fact]
    public async Task 空body不能登记空卡片()
    {
        using var harness = new TestHarness();

        var result = await harness.CallAsync("update_product", """{"productId":1,"body":{}}""");

        Assert.Contains("参数校验未通过", result);
        Assert.Contains("至少需要指定 1 个字段", result);
        Assert.Empty(harness.PendingIds);
    }

    [Fact]
    public async Task 缺商品ID会被拦下()
    {
        using var harness = new TestHarness();

        var result = await harness.CallAsync("update_product", """{"body":{"name":"芬达"}}""");

        Assert.Contains("参数校验未通过", result);
        Assert.Contains("productId", result);
        Assert.Empty(harness.PendingIds);
    }

    [Fact]
    public async Task 确认后按契约重放PUT请求()
    {
        using var harness = new TestHarness();

        await harness.CallAsync("update_product", """{"productId":1,"body":{"isActive":false}}""");
        var id = Assert.Single(harness.PendingIds);

        var outcome = await harness.Approvals.ConfirmAsync(id, "test-session", harness.UserId);

        Assert.True(outcome.Success);
        Assert.Equal("PUT", harness.Merchant.LastMethod);
        Assert.Equal("/api/Products/1", harness.Merchant.LastUrl!.Split('?')[0]);
        Assert.Contains("\"isActive\":false", harness.Merchant.LastBody);
    }
}
