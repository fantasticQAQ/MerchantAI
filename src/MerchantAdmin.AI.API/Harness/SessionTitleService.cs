using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace MerchantAdmin.AI.API.Harness;

/// <summary>
/// 用模型给会话起标题。
///
/// 之前标题就是「第一条用户消息的前 24 个字」—— 问题是第一条消息经常是
/// 「你好」「在吗」「有哪些商品？」，放在列表里完全看不出这个会话到底聊了什么。
/// 现在改成：**把对话内容喂给模型，让它概括成一个短标题**。
///
/// 三条刻意的规则：
/// - **一条会话只生成一次**：第一次对话结束后起个默认标题，之后不管再聊什么都不改。
///   标题是列表里的"目录"，来回变反而找不到东西；店主觉得不合适可以手动重命名。
/// - **用户手动改过名就永不覆盖**（`TitleIsAuto == false`）。
/// - **失败一律不影响主流程**：起不出标题就退回原来的「第一条用户消息」。
/// </summary>
public sealed class SessionTitleService
{
    private const int MaxTitleChars = 16;

    private const string Prompt = """
        你是会话标题生成器。下面是一个会话开头的内容，请概括出这个会话在聊什么。

        要求：
        - 用中文，不超过 12 个字
        - 像列表里的目录一样，一眼能看出主题（例如「查询商品列表」「批量创建 60 个商品」「给可乐下单」）
        - 只输出标题本身，不要标点、引号、书名号，不要任何解释或前缀
        """;

    private readonly Kernel _kernel;
    private readonly ISessionPrefsStore _prefs;
    private readonly IConversationStore _conversations;
    private readonly bool _enabled;
    private readonly ILogger<SessionTitleService> _logger;

    public SessionTitleService(
        Kernel kernel,
        ISessionPrefsStore prefs,
        IConversationStore conversations,
        IConfiguration configuration,
        ILogger<SessionTitleService> logger)
    {
        _kernel = kernel;
        _prefs = prefs;
        _conversations = conversations;
        _enabled = configuration.GetValue("Ai:SessionTitle:Enabled", true);
        _logger = logger;
    }

    /// <summary>
    /// 会话还没有标题时生成一个，返回新标题；已经有标题、或生成失败时返回 null。
    /// 调用方拿它去更新前端显示的标题，返回 null 就什么都不做。
    /// </summary>
    public async Task<string?> MaybeGenerateAsync(
        long userId, string sessionId, string? userMessage, string? assistantReply, CancellationToken ct = default)
    {
        if (!_enabled) return null;

        // 自动续跑（含「确认执行」之后的续跑）那轮的输入是系统合成的，不是店主说的话，不当素材
        var ask = AgentContinuation.IsContinuation(userMessage) ? null : userMessage;
        if (string.IsNullOrWhiteSpace(ask) && string.IsNullOrWhiteSpace(assistantReply)) return null;

        try
        {
            // 已经有标题了 —— 不管是店主手动命名的，还是第一次对话自动生成的 —— 都不再改。
            // 这里就是「只生成一次」的闸门：第一次对话时 Title 还是 null，之后永远不再是。
            if (_prefs.Get(userId, sessionId).Title is not null) return null;

            var history = await _conversations.GetMessagesAsync(sessionId, userId, ct);
            var title = await AskModelAsync(BuildDigest(history, ask, assistantReply), ct);
            if (string.IsNullOrWhiteSpace(title)) return null;

            // 再读一次：生成期间店主可能刚好重命名了，别把它盖回去
            var latest = _prefs.Get(userId, sessionId);
            if (latest.Title is not null) return latest.TitleIsAuto ? null : title;

            _prefs.Save(userId, sessionId, latest with { Title = title, TitleIsAuto = true });
            return title;
        }
        catch (Exception ex)
        {
            // 起标题是锦上添花，绝不能让它影响对话本身
            _logger?.LogWarning(ex, "生成会话标题失败：{Session}", sessionId);
            return null;
        }
    }

    /// <summary>
    /// 把会话开头压成一段素材交模型概括。
    ///
    /// 只喂**店主提过的要求**，不喂 AI 的回复：回复动辄几百字，塞进去既费 token，
    /// 又容易把标题带偏到某个细节上。最后附一句本轮回复当补充。
    /// </summary>
    private static string BuildDigest(
        IReadOnlyList<ChatMessageContent>? history, string? userMessage, string? assistantReply)
    {
        var asks = (history ?? Array.Empty<ChatMessageContent>())
            .Where(m => m.Role == AuthorRole.User)
            .Select(m => (m.Content ?? string.Empty).Replace('\n', ' ').Trim())
            .Where(t => t.Length > 0 && !AgentContinuation.IsContinuation(t))
            .ToList();

        // 首次生成时历史里就是这一轮；本轮是续跑（没有真人提问）时 userMessage 为空，那就完全靠历史
        if (!string.IsNullOrWhiteSpace(userMessage))
        {
            var ask = userMessage.Replace('\n', ' ').Trim();
            if (asks.Count == 0 || asks[^1] != ask) asks.Add(ask);
        }

        if (asks.Count == 0) return "# 暂无提问";

        var digest = string.Join('\n', asks.Select((text, i) => $"{i + 1}. {Clip(text, 60)}"));
        return string.IsNullOrWhiteSpace(assistantReply)
            ? digest
            : digest + "\n\n（助手本次回复：" + Clip(assistantReply, 200) + "）";
    }

    private async Task<string?> AskModelAsync(string digest, CancellationToken ct)
    {
        var chat = _kernel.GetRequiredService<IChatCompletionService>();

        var history = new ChatHistory();
        history.AddSystemMessage(Prompt);
        history.AddUserMessage(digest);

        var settings = new OpenAIPromptExecutionSettings
        {
            MaxTokens = 48,
            Temperature = 0.2
        };

        var result = await chat.GetChatMessageContentAsync(history, settings, _kernel, ct);
        return Clean(result.Content);
    }

    /// <summary>
    /// 模型经常不听话：带上引号、书名号、结尾句号，或者干脆写成「标题：xxx」。
    /// 这里统一收拾干净 —— 列表里的标题带这些符号会很脏。
    /// </summary>
    private static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var text = raw.Trim();

        // 只取第一行，模型偶尔会多写一句解释
        var newline = text.IndexOfAny(['\r', '\n']);
        if (newline >= 0) text = text[..newline].Trim();

        // 去掉「标题：」这类前缀
        foreach (var prefix in new[] { "标题：", "标题:", "标题 ", "Title:", "Title：" })
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                text = text[prefix.Length..].Trim();

        text = text.Trim('"', '\'', '「', '」', '《', '》', '“', '”', '‘', '’', '。', '，', '.', ',', ':', '：', ' ', '*', '#');
        text = text.Trim();

        if (text.Length == 0) return null;
        return text.Length <= MaxTitleChars ? text : text[..MaxTitleChars];
    }

    private static string Clip(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
