using System.Text.Json.Nodes;
using MerchantAdmin.AI.API.Ai.Tools;
using Xunit;

namespace MerchantAdmin.AI.API.Tests;

public class ToolCatalogTests
{
    [Fact]
    public void OpenApi骨架_能解析ref与摊平请求体()
    {
        var createOrder = SwaggerFixture.Operations["POST /api/Orders/create"];

        // requestBody 只有一个顶层属性 -> 摊平成同名参数
        var orderItems = Assert.Single(createOrder.Parameters);
        Assert.Equal("orderItems", orderItems.Name);

        // $ref 必须已经解析成内联 schema
        Assert.Equal("array", orderItems.Schema["type"]!.GetValue<string>());
        var productId = orderItems.Schema["items"]!["properties"]!["productId"];
        Assert.NotNull(productId);

        // nullable 这类 OpenAPI 专有关键字应被丢掉
        Assert.Null(productId!["nullable"]);
    }

    [Fact]
    public void 真实toolsJson_能加载且路径来自swagger()
    {
        using var harness = new TestHarness();

        Assert.True(harness.Catalog.Tools.Count >= 4, $"实际 {harness.Catalog.Tools.Count} 个工具");
        Assert.Equal("/api/Products", harness.Catalog.GetRequired("list_products").Http.Path);
        Assert.True(harness.Catalog.GetRequired("create_order").RequiresApproval);
        Assert.False(harness.Catalog.GetRequired("list_products").RequiresApproval);
    }

    [Fact]
    public void 未暴露的后端接口会被识别出来()
    {
        using var harness = new TestHarness();

        // 刻意不暴露的两类：导出（返回 CSV 文件流，不适合当工具）和 /api/Test/*（自测用接口）
        Assert.Contains(harness.Catalog.UnexposedOperations,
            o => o.Key.Equals("GET /api/Orders/export", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(harness.Catalog.UnexposedOperations,
            o => o.Key.Equals("POST /api/Test/setRedisKey", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void 自测接口一个都不会暴露给模型()
    {
        using var harness = new TestHarness();

        Assert.DoesNotContain(harness.Catalog.Exposed,
            t => t.Http.Path.StartsWith("/api/Test", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void 缺少access的工具必须让启动失败()
    {
        var path = WriteTempTools("""
        { "tools": [ { "name":"danger", "description":"x", "http":{"method":"DELETE","path":"/api/all"} } ] }
        """);

        var ex = Assert.Throws<InvalidOperationException>(() => ToolCatalog.Load(path, "http://localhost", null, SwaggerFixture.Operations));
        Assert.Contains("access", ex.Message);
    }

    [Fact]
    public void forbidden的工具不注册给模型但仍可解析出路径参数()
    {
        var path = WriteTempTools("""
        { "tools": [ { "name":"delete_product", "description":"删除商品", "access":"forbidden",
                       "from":"DELETE /api/Products/{productId}" } ] }
        """);

        var catalog = ToolCatalog.Load(path, "http://localhost", null, SwaggerFixture.Operations);

        Assert.Empty(catalog.Exposed);
        Assert.False(catalog.Find("delete_product")!.IsExposedToModel);
        Assert.Equal("/api/Products/{productId}", catalog.Find("delete_product")!.Http.Path);
    }

    [Fact]
    public void from引用了不存在的操作会给出可读报错()
    {
        var path = WriteTempTools("""
        { "tools": [ { "name":"x", "description":"x", "access":"read", "from":"GET /api/ProductsTypo" } ] }
        """);

        var ex = Assert.Throws<InvalidOperationException>(() => ToolCatalog.Load(path, "http://localhost", null, SwaggerFixture.Operations));
        Assert.Contains("不存在", ex.Message);
    }

    [Fact]
    public void 后端字段改名时会指出契约漂移()
    {
        var path = WriteTempTools("""
        { "tools": [ { "name":"create_order", "description":"x", "access":"write",
            "from":"POST /api/Orders/create",
            "parameters":[ { "name":"items", "required":true, "schema": { "type":"array", "items":{"type":"object"} } } ] } ] }
        """);

        var ex = Assert.Throws<InvalidOperationException>(() => ToolCatalog.Load(path, "http://localhost", null, SwaggerFixture.Operations));
        Assert.Contains("orderItems", ex.Message);
    }

    [Fact]
    public void overlay可以合并enum与替换schema()
    {
        using var harness = new TestHarness();

        var status = harness.Catalog.GetRequired("list_orders").Parameters.First(p => p.Name == "status");
        Assert.NotNull(status.Schema["enum"]);

        // tools.json 用 schemaMode=replace 丢掉了 swagger 里的 productName
        var orderItems = harness.Catalog.GetRequired("create_order").Parameters[0].Schema;
        Assert.Null(orderItems["items"]!["properties"]!["productName"]);
        Assert.Equal(1, orderItems["minItems"]!.GetValue<int>());

        // 参数描述必须内联进 schema，否则连接器不会发给模型
        Assert.NotNull(orderItems["description"]);
    }

    [Fact]
    public void 参数描述会被内联进schema()
    {
        using var harness = new TestHarness();

        var page = harness.Catalog.GetRequired("list_products").Parameters.First(p => p.Name == "page");
        Assert.Equal("页码，默认1", page.Schema["description"]!.GetValue<string>());
        Assert.Equal(1, page.Schema["default"]!.GetValue<int>());
    }

    [Fact]
    public void 重复加载不会污染共享的swagger骨架()
    {
        var path = LocateTools();

        // 第一次加载会往参数 schema 里注入 description/default
        _ = ToolCatalog.Load(path, "http://localhost", null, SwaggerFixture.Operations);

        // 如果第一次是就地修改 swagger 对象，这里就会看到被污染的骨架
        var raw = SwaggerFixture.Operations["GET /api/Products"].Parameters.First(p => p.Name == "page");
        Assert.Null(raw.Schema["description"]);

        // 第二次加载仍要能正确注入 overlay 的描述（就地修改会让 ContainsKey 为 true 而跳过注入）
        var second = ToolCatalog.Load(path, "http://localhost", null, SwaggerFixture.Operations);
        var page = second.GetRequired("list_products").Parameters.First(p => p.Name == "page");
        Assert.Equal("页码，默认1", page.Schema["description"]!.GetValue<string>());
    }

    [Fact]
    public void 热加载后overlay的注入依然生效()
    {
        using var harness = new TestHarness();
        var before = harness.Catalog.GetRequired("list_products").Parameters.First(p => p.Name == "page");
        Assert.Equal("页码，默认1", before.Schema["description"]!.GetValue<string>());

        harness.Catalog.Reload(harness.Merchant.BaseUrl, null, SwaggerFixture.Operations);

        var after = harness.Catalog.GetRequired("list_products").Parameters.First(p => p.Name == "page");
        Assert.Equal("页码，默认1", after.Schema["description"]!.GetValue<string>());
    }

    private static string LocateTools() => TestHarness.LocateRealToolsJson();

    private static string WriteTempTools(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tools-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }
}
