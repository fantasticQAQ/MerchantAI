using System.Text.Json;
using System.Text.Json.Nodes;
using MerchantAdmin.AI.API.Ai.Tools;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace MerchantAdmin.AI.API.Harness;

/// <summary>
/// ChatHistory 的持久化序列化器。
///
/// 为什么不直接用 System.Text.Json：
/// SK 的 KernelContent 虽然标了多态，直出能生成 JSON，但还原时会走样 ——
/// - FunctionCallContent.Arguments 里的 JsonElement 数字会被写成字符串（1 -> "1"）
/// - Metadata 里混着 OpenAI SDK 的 Usage / FinishReason 等对象，反序列化后只剩 JsonElement，语义丢失
/// 这些都会让「恢复出来的历史」和原始历史在语义上不再等价，轻则参数类型出错，重则丢掉工具调用序列。
///
/// 所以这里用一份显式的 wire format，只持久化真正需要的东西：
/// role + 三种 item（文本 / 函数调用 / 函数结果），并带版本号以便将来演进。
/// 遇到不认识的内容类型一律丢弃并计数，绝不让一个奇怪的消息把整条会话卡死。
/// </summary>
public static class ChatHistorySerializer
{
    public const int CurrentVersion = 1;

    private const string KindText = "text";
    private const string KindCall = "call";
    private const string KindResult = "result";

    /// <summary>不要把中文转义成 \uXXXX：会话里全是中文，转义后体积膨胀好几倍，Redis 里也没法直接看。统一走 JsonText。</summary>
    private static readonly JsonSerializerOptions JsonOut = JsonText.Relaxed;

    public static string Serialize(ChatHistory history)
    {
        var messages = new JsonArray();
        var dropped = 0;

        foreach (var message in history)
        {
            var items = new JsonArray();

            foreach (var item in message.Items)
            {
                switch (item)
                {
                    case TextContent text:
                        items.Add(new JsonObject
                        {
                            ["kind"] = KindText,
                            ["text"] = text.Text
                        });
                        break;

                    case FunctionCallContent call:
                        items.Add(new JsonObject
                        {
                            ["kind"] = KindCall,
                            ["id"] = call.Id,
                            ["plugin"] = call.PluginName,
                            ["function"] = call.FunctionName,
                            ["arguments"] = ArgumentsToNode(call)
                        });
                        break;

                    case FunctionResultContent result:
                        items.Add(new JsonObject
                        {
                            ["kind"] = KindResult,
                            ["callId"] = result.CallId,
                            ["plugin"] = result.PluginName,
                            ["function"] = result.FunctionName,
                            ["result"] = ToNode(result.Result)
                        });
                        break;

                    default:
                        dropped++;
                        break;
                }
            }

            // 没有任何可用内容的消息（例如只有图片）不写进去，否则 OpenAI 会拒绝空消息
            if (items.Count == 0) continue;

            messages.Add(new JsonObject
            {
                ["role"] = message.Role.Label,
                ["items"] = items
            });
        }

        return new JsonObject
        {
            ["v"] = CurrentVersion,
            ["dropped"] = dropped,
            ["messages"] = messages
        }.ToJsonString(JsonOut);
    }

    public static ChatHistory Deserialize(string json)
    {
        var history = new ChatHistory();
        if (string.IsNullOrWhiteSpace(json)) return history;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return history;   // 坏数据 → 当作空会话，不让它把请求打挂
        }

        if (root is not JsonObject document) return history;
        if (document["messages"] is not JsonArray messages) return history;

        foreach (var messageNode in messages)
        {
            if (messageNode is not JsonObject message) continue;

            var roleLabel = message["role"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(roleLabel)) continue;
            if (message["items"] is not JsonArray items || items.Count == 0) continue;

            var collection = new ChatMessageContentItemCollection();
            foreach (var itemNode in items)
            {
                if (itemNode is not JsonObject item) continue;
                var kind = item["kind"]?.GetValue<string>();
                var content = Rebuild(kind, item);
                if (content is not null) collection.Add(content);
            }

            if (collection.Count == 0) continue;

            history.Add(new ChatMessageContent(new AuthorRole(roleLabel), collection));
        }

        return history;
    }

    private static KernelContent? Rebuild(string? kind, JsonObject item) => kind switch
    {
        KindText => new TextContent(item["text"]?.GetValue<string>() ?? string.Empty),

        KindCall => new FunctionCallContent(
            item["function"]?.GetValue<string>() ?? string.Empty,
            item["plugin"]?.GetValue<string>(),
            item["id"]?.GetValue<string>(),
            NodeToArguments(item["arguments"])),

        KindResult => new FunctionResultContent(
            item["function"]?.GetValue<string>() ?? string.Empty,
            item["plugin"]?.GetValue<string>(),
            item["callId"]?.GetValue<string>(),
            NodeToResult(item["result"])),

        _ => null
    };

    /// <summary>
    /// 工具调用参数的序列化。
    ///
    /// 这里有个必须绕开的坑：SK 的 <see cref="FunctionCallContent.Arguments"/> 是个**解析视图**，
    /// 它会把 JSON 数字变成字符串（模型传的 {"page":1} 在里面是 "1"）。
    /// 直接照它写盘，还原后发出去的 tool_calls 就变成 {"page":"1"} —— 与原始请求不再等价。
    ///
    /// 所以优先取 provider 原始对象里的原始 JSON（实测 OpenAI SDK 的 ChatToolCall.FunctionArguments
    /// 保存着模型原样输出的 {"page":1}），拿不到再退回解析视图。
    /// 用反射而不是硬引 OpenAI 类型，是为了换 provider 时这段逻辑还能用。
    /// </summary>
    private static JsonNode? ArgumentsToNode(FunctionCallContent call)
    {
        if (TryGetRawArgumentsJson(call.InnerContent) is { } raw)
        {
            try
            {
                return JsonNode.Parse(raw);
            }
            catch (JsonException)
            {
                // 原始 JSON 不可用时静默退回解析视图
            }
        }

        if (call.Arguments is null || call.Arguments.Count == 0) return new JsonObject();

        var node = new JsonObject();
        foreach (var (key, value) in call.Arguments)
            node[key] = ToNode(value);

        return node;
    }

    private static string? TryGetRawArgumentsJson(object? innerContent)
    {
        if (innerContent is null) return null;

        var property = innerContent.GetType().GetProperty("FunctionArguments");
        var value = property?.GetValue(innerContent);

        return value switch
        {
            null => null,
            string text => text,
            BinaryData data => data.ToString(),
            _ => null       // 类型不认识就不猜，交给回退分支
        };
    }

    private static KernelArguments NodeToArguments(JsonNode? node)
    {
        var arguments = new KernelArguments();
        if (node is not JsonObject obj) return arguments;

        foreach (var (key, value) in obj)
        {
            // 还原成 JsonElement —— 这正是 OpenAI 连接器传参时的形态，
            // 也是 HttpToolInvoker / JsonSchemaLite 已经妥善处理过的形态
            arguments[key] = value is null
                ? null
                : JsonSerializer.Deserialize<JsonElement>(value.ToJsonString());
        }

        return arguments;
    }

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        _ => JsonNode.Parse(JsonSerializer.Serialize(value))
    };

    private static object? NodeToResult(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => JsonSerializer.Deserialize<JsonElement>(node.ToJsonString())
    };
}
