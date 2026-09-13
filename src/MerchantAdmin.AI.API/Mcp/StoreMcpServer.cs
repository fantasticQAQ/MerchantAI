using System.Text.Json;
using System.Text.Json.Nodes;
using MerchantAdmin.AI.API.Ai.Tools;
using MerchantAdmin.AI.API.Auth;
using MerchantAdmin.AI.API.Harness;
using Microsoft.SemanticKernel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MerchantAdmin.AI.API.Mcp;

/// <summary>
/// 把 ToolCatalog 同时暴露成一个 MCP server（Streamable HTTP，端点 /mcp）。
/// 只读工具直接执行；写工具只登记「待人工确认」，必须再调 confirm_pending_action 才真正执行——
/// 和聊天入口走的是同一套 HumanApprovalService / 审计轨迹，所以 HITL 语义在两条入口上是一致的。
/// 工具清单同样来自 tools.json，加工具不需要改这里。
/// </summary>
public static class StoreMcpServer
{
    public const string EndpointPattern = "/mcp";
    public const string ConfirmToolName = "confirm_pending_action";

    public static IServiceCollection AddStoreMcpServer(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue("Ai:Mcp:Enabled", true)) return services;

        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "merchant-admin-store", Version = "1.0.0" };
                options.ServerInstructions =
                    "商户后台工具集。只读工具可直接调用；写工具只会登记一条「待人工确认」记录并返回登记编号，" +
                    "需要先向用户确认，再用 confirm_pending_action 提交登记编号才会真正执行。";
            })
            .WithHttpTransport()
            .WithListToolsHandler((context, _) =>
            {
                var catalog = context.Services!.GetRequiredService<IToolCatalog>();
                return ValueTask.FromResult(new ListToolsResult
                {
                    Tools = catalog.Exposed.Select(ToProtocolTool).Append(ConfirmTool()).ToList()
                });
            })
            .WithCallToolHandler(CallAsync);

        return services;
    }

    private static async ValueTask<CallToolResult> CallAsync(
        RequestContext<CallToolRequestParams> context,
        CancellationToken ct)
    {
        var services = context.Services!;
        var name = context.Params?.Name ?? string.Empty;

        try
        {
            if (string.Equals(name, ConfirmToolName, StringComparison.Ordinal))
                return await ConfirmAsync(context, services, ct);

            var user = services.GetRequiredService<ICurrentUser>();
            var catalog = services.GetRequiredService<IToolCatalog>();
            var descriptor = catalog.Find(name);
            if (descriptor is null || !descriptor.IsExposedToModel)
                return Error($"未知工具：{name}");

            // 和聊天入口一样的角色护栏：MCP 不能绕过去
            if (!user.IsInAnyRole(descriptor.Roles))
                return Error($"工具 {name} 需要 {string.Join("/", descriptor.Roles)} 权限，当前账号没有。");

            var arguments = ToKernelArguments(context.Params?.Arguments);

            // 写操作：和聊天入口一样，只登记待确认，不执行
            if (descriptor.RequiresApproval)
            {
                var approvals = services.GetRequiredService<HumanApprovalService>();
                var registration = approvals.Request(descriptor, arguments, "mcp", user.UserId);
                return registration.Registered ? Text(registration.Message) : Error(registration.Message);
            }

            var invoker = services.GetRequiredService<IToolInvoker>();
            var plan = invoker.BuildPlan(descriptor, arguments);
            if (!plan.Ok) return Error(plan.Error!);

            var result = await invoker.SendAsync(descriptor, plan.Plan!, ct);
            return Text(result.Text);
        }
        catch (Exception ex)
        {
            return Error($"{name} 执行失败：{ex.Message}");
        }
    }

    private static async ValueTask<CallToolResult> ConfirmAsync(
        RequestContext<CallToolRequestParams> context,
        IServiceProvider services,
        CancellationToken ct)
    {
        var ids = ReadStringArray(context.Params?.Arguments, "actionIds");
        if (ids.Count == 0)
            return Error("actionIds 不能为空，请传入待确认操作返回的登记编号。");

        var sessionId = ReadString(context.Params?.Arguments, "sessionId") ?? "mcp";
        var user = services.GetRequiredService<ICurrentUser>();
        var approvals = services.GetRequiredService<HumanApprovalService>();
        var outcomes = await approvals.ConfirmManyAsync(ids, sessionId, user.UserId, ct);

        var lines = outcomes.Select(o => o.Success
            ? $"✔ {o.Summary} → 业务返回：{o.Data}"
            : $"✖ {o.ActionId}：{o.Error}");

        var text = string.Join(Environment.NewLine, lines);
        return outcomes.All(o => o.Success) ? Text(text) : new CallToolResult
        {
            IsError = true,
            Content = new List<ContentBlock> { new TextContentBlock { Text = text } }
        };
    }

    // ---------------------------------------------------------------- schema

    private static Tool ToProtocolTool(ToolDescriptor descriptor) => new()
    {
        Name = descriptor.Name,
        Description = descriptor.Description,
        InputSchema = BuildInputSchema(descriptor),
        Annotations = new ToolAnnotations
        {
            Title = descriptor.Name,
            ReadOnlyHint = !descriptor.RequiresApproval,
            DestructiveHint = descriptor.RequiresApproval,
            IdempotentHint = !descriptor.RequiresApproval,
            OpenWorldHint = false
        }
    };

    private static Tool ConfirmTool()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["actionIds"] = new JsonObject
                {
                    ["type"] = "array",
                    ["minItems"] = 1,
                    ["description"] = "待确认操作返回的登记编号列表",
                    ["items"] = new JsonObject { ["type"] = "string" }
                },
                ["sessionId"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "可选，会话标识，用于审计"
                }
            },
            ["required"] = new JsonArray("actionIds")
        };

        return new Tool
        {
            Name = ConfirmToolName,
            Description = "确认并真正执行此前登记的写操作。用户明确同意后再调用。",
            InputSchema = JsonSerializer.SerializeToElement(schema)
        };
    }

    private static JsonElement BuildInputSchema(ToolDescriptor descriptor)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var parameter in descriptor.Parameters)
        {
            properties[parameter.Name] = parameter.Schema.DeepClone();
            if (parameter.Required) required.Add(parameter.Name);
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required
        };

        return JsonSerializer.SerializeToElement(schema);
    }

    // ---------------------------------------------------------------- helpers

    private static KernelArguments ToKernelArguments(IDictionary<string, JsonElement>? arguments)
    {
        var result = new KernelArguments();
        if (arguments is null) return result;

        foreach (var (key, value) in arguments)
            result[key] = value;

        return result;
    }

    private static List<string> ReadStringArray(IDictionary<string, JsonElement>? arguments, string key)
    {
        if (arguments is null || !arguments.TryGetValue(key, out var element)) return new List<string>();

        return element.ValueKind switch
        {
            JsonValueKind.Array => element.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList(),
            JsonValueKind.String => new List<string> { element.GetString()! },
            _ => new List<string>()
        };
    }

    private static string? ReadString(IDictionary<string, JsonElement>? arguments, string key)
        => arguments is not null
           && arguments.TryGetValue(key, out var element)
           && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static CallToolResult Text(string text) => new()
    {
        Content = new List<ContentBlock> { new TextContentBlock { Text = text } }
    };

    private static CallToolResult Error(string text) => new()
    {
        IsError = true,
        Content = new List<ContentBlock> { new TextContentBlock { Text = text } }
    };
}
