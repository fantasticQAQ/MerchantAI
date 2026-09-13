using MerchantAdmin.AI.API.Ai.Tools;
using Xunit;

namespace MerchantAdmin.AI.API.Tests;

/// <summary>
/// tools.json 热加载：改完保存即生效，且改坏了不能把服务带走。
/// </summary>
public class HotReloadTests
{
    private const string OneTool = """
    { "tools": [ { "name":"list_products","description":"x","access":"read","from":"GET /api/Products" } ] }
    """;

    private const string TwoTools = """
    { "tools": [
        { "name":"list_products","description":"x","access":"read","from":"GET /api/Products" },
        { "name":"get_dashboard","description":"y","access":"read","from":"GET /api/Dashboard" } ] }
    """;

    [Fact]
    public async Task 热加载后同一个目录实例就能看到新工具()
    {
        using var harness = new TestHarness(useRealToolsJson: false, toolsPath: await WriteTemp(OneTool), openApi: SwaggerFixture.Operations);
        Assert.Single(harness.Catalog.Exposed);

        await File.WriteAllTextAsync(harness.Catalog.SourcePath, TwoTools);
        harness.Catalog.Reload(harness.Merchant.BaseUrl, null, null, SwaggerFixture.Operations);

        // 消费方（过滤器/审批/执行器/MCP）持有的是同一个引用，因此自动看到新工具
        Assert.Equal(2, harness.Catalog.Exposed.Count);
        Assert.NotNull(harness.Catalog.Find("get_dashboard"));
    }

    [Fact]
    public async Task 热加载遇到坏配置时保留上一份可用配置()
    {
        using var harness = new TestHarness(useRealToolsJson: false, toolsPath: await WriteTemp(TwoTools), openApi: SwaggerFixture.Operations);
        Assert.Equal(2, harness.Catalog.Exposed.Count);

        await File.WriteAllTextAsync(harness.Catalog.SourcePath, """
        { "tools": [ { "name":"broken","description":"x" } ] }
        """);

        Assert.Throws<InvalidOperationException>(() =>
            harness.Catalog.Reload(harness.Merchant.BaseUrl, null, null, SwaggerFixture.Operations));

        // 关键：改错一个字段不应该把正在跑的服务打挂
        Assert.Equal(2, harness.Catalog.Exposed.Count);
    }

    [Fact]
    public async Task 热加载后forbidden的工具依然不暴露()
    {
        using var harness = new TestHarness(useRealToolsJson: false, toolsPath: await WriteTemp(OneTool), openApi: SwaggerFixture.Operations);

        await File.WriteAllTextAsync(harness.Catalog.SourcePath, """
        { "tools": [
            { "name":"list_products","description":"x","access":"read","from":"GET /api/Products" },
            { "name":"delete_product","description":"z","access":"forbidden","from":"DELETE /api/Products/{productId}" } ] }
        """);
        harness.Catalog.Reload(harness.Merchant.BaseUrl, null, null, SwaggerFixture.Operations);

        Assert.Single(harness.Catalog.Exposed);
        Assert.False(harness.Catalog.Find("delete_product")!.IsExposedToModel);
    }

    private static async Task<string> WriteTemp(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tools-hot-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        return path;
    }
}
