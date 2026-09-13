using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MerchantAdmin.AI.API.Ai.Tools;

/// <summary>
/// JSON 文本输出的小工具。
///
/// 集中在这里是有教训的：默认的 System.Text.Json 会把中文转义成 \uXXXX，
/// 这个项目里已经因此在三个地方出过问题（工具返回值、会话历史、待确认摘要），
/// 每次都表现为「功能没坏，但人能看到的/模型能看到的东西变成乱码」。
/// 所以统一从这里取，别再各写各的。
/// </summary>
internal static class JsonText
{
    /// <summary>不转义非 ASCII 的序列化选项。写给人看、写给模型看的内容都应该用它。</summary>
    public static readonly JsonSerializerOptions Relaxed = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>把 JsonNode 渲染成人能读的文本：字符串不带引号，其余走 JSON。</summary>
    public static string Render(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => node.ToJsonString(Relaxed)
    };
}
