using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MerchantAdmin.AI.API.Ai.Tools;

/// <summary>审批摘要格式化器注册表。tools.json 里用 summaryFormatter 按名字引用，避免把工具名硬编码进 Harness。</summary>
public sealed class PendingSummaryRegistry
{
    private readonly Dictionary<string, IPendingSummaryFormatter> _formatters;

    public PendingSummaryRegistry(IEnumerable<IPendingSummaryFormatter> formatters)
    {
        _formatters = formatters.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 程序集里所有摘要格式化器。Program 和测试都用这一个来源，
    /// 免得出现「新加了一个格式化器但忘了注册，线上悄悄退回默认渲染」这类问题。
    /// </summary>
    public static IEnumerable<Type> DiscoverFormatterTypes()
        => typeof(IPendingSummaryFormatter).Assembly.GetTypes()
            .Where(t => typeof(IPendingSummaryFormatter).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false });

    public string Format(ToolDescriptor descriptor, HttpRequestPlan plan)
    {
        // 模板最省事：简单写操作在 tools.json 里写一句 "取消订单 #{orderId}" 就够了，不用写 C# 类
        if (!string.IsNullOrWhiteSpace(descriptor.SummaryTemplate))
            return RenderTemplate(descriptor.SummaryTemplate!, plan);

        if (!string.IsNullOrWhiteSpace(descriptor.SummaryFormatter)
            && _formatters.TryGetValue(descriptor.SummaryFormatter, out var formatter))
        {
            return formatter.Format(descriptor, plan);
        }

        // 缺省：把归一化后的参数原样列出来。宁可朴素也不要编造。
        var parts = plan.Arguments
            .Where(kv => kv.Value is not null)
            .Select(kv => $"{kv.Key}={JsonText.Render(kv.Value)}")
            .ToList();

        return parts.Count == 0 ? "(无参数)" : string.Join("；", parts);
    }

    /// <summary>把 {param} 换成参数值；取不到的保留原样，让人一眼看出模板写错了。</summary>
    private static string RenderTemplate(string template, HttpRequestPlan plan)
        => Regex.Replace(template, @"\{([A-Za-z_][A-Za-z0-9_]*)\}", match =>
            plan.Arguments.TryGetValue(match.Groups[1].Value, out var value) && value is not null
                ? JsonText.Render(value)
                : match.Value);
}

/// <summary>下单摘要：把订单项数组渲染成「商品ID 1 × 2」这种可读形式。</summary>
public sealed class OrderItemsSummaryFormatter : IPendingSummaryFormatter
{
    public string Name => "orderItems";

    public string Format(ToolDescriptor descriptor, HttpRequestPlan plan)
    {
        // 参数名可能来自 tools.json（items）或由 swagger 契约推导（orderItems），两种都认
        var items = plan.Arguments.GetValueOrDefault("orderItems") as JsonArray
                    ?? plan.Arguments.GetValueOrDefault("items") as JsonArray;

        if (items is null || items.Count == 0)
            return "(订单项为空)";

        var parts = items.Select((item, index) =>
        {
            var pid = JsonText.Render(item?["productId"]);
            var qty = JsonText.Render(item?["quantity"]);
            return $"{index + 1}. 商品ID {pid} × {qty}";
        });

        return string.Join("；", parts);
    }
}

/// <summary>改商品摘要：把「改了什么」列清楚，尤其是库存增量 —— 增量语义最容易和「改成多少」搞混。</summary>
public sealed class ProductUpdateSummaryFormatter : IPendingSummaryFormatter
{
    public string Name => "productUpdate";

    public string Format(ToolDescriptor descriptor, HttpRequestPlan plan)
    {
        var productId = JsonText.Render(plan.Arguments.GetValueOrDefault("productId"));
        var body = plan.Arguments.GetValueOrDefault("body") as JsonObject;

        var parts = new List<string>();
        if (body?["name"] is { } name) parts.Add($"改名 → {JsonText.Render(name)}");
        if (body?["price"] is { } price) parts.Add($"单价 → {JsonText.Render(price)}");
        if (body?["stockDelta"] is { } delta) parts.Add($"库存增量 {SignedNumber(delta)}（不是改成这个数）");
        if (body?["isActive"] is { } active) parts.Add(active.GetValue<bool>() ? "上架" : "下架");

        return parts.Count == 0
            ? $"商品ID {productId}：（没有要修改的字段）"
            : $"商品ID {productId}：" + string.Join("；", parts);
    }

    private static string SignedNumber(JsonNode node)
    {
        var text = JsonText.Render(node);
        var positive = decimal.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0;
        return positive ? "+" + text : text;
    }
}

/// <summary>新建商品摘要。</summary>
public sealed class ProductCreateSummaryFormatter : IPendingSummaryFormatter
{
    public string Name => "productCreate";

    public string Format(ToolDescriptor descriptor, HttpRequestPlan plan)
    {
        var parts = new List<string>();
        if (plan.Arguments.GetValueOrDefault("name") is { } name) parts.Add($"名称 {JsonText.Render(name)}");
        if (plan.Arguments.GetValueOrDefault("price") is { } price) parts.Add($"单价 {JsonText.Render(price)}");
        if (plan.Arguments.GetValueOrDefault("stock") is { } stock) parts.Add($"初始库存 {JsonText.Render(stock)}");

        return parts.Count == 0 ? "新建商品：（缺少必要信息）" : "新建商品：" + string.Join("；", parts);
    }
}
