using System.Collections.Concurrent;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace MerchantAdmin.AI.API.Harness;

/// <summary>会话列表里的一条。</summary>
public sealed record SessionSummary(
    string SessionId,
    string Title,
    int MessageCount,
    DateTime CreatedAt,
    DateTime LastActiveAt,
    /// <summary>最后一条 AI 回复的摘要。列表里在标题下面显示两行，一眼看出这个会话聊了什么。</summary>
    string? Preview = null);

/// <summary>访问了不属于自己的会话。控制器据此返回 403。</summary>
public sealed class SessionAccessDeniedException(string sessionId)
    : Exception($"当前用户无权访问会话 {sessionId}");

/// <summary>
/// 会话历史。之前每一轮都是全新的单轮调用，所谓「多轮」全靠模型每轮重新查一遍数据撑着，
/// 用户说「给上面的商品下单」时「上面」其实并不在上下文里。这里把它补上。
/// 系统提示词由 Agent 的 Instructions 提供，不在这里重复塞。
///
/// 所有读写都带 userId：会话属于创建它的人，别人看不到也写不进去。
/// </summary>
public interface IConversationStore
{
    /// <summary>
    /// 在同一会话上串行执行一轮对话，并把该会话的 ChatHistory 交给回调。
    /// 串行是必要的：同一个 sessionId 并发进来（用户连点两次发送）会写乱同一份历史。
    /// 会话已属于别的用户时抛 <see cref="SessionAccessDeniedException"/>。
    /// </summary>
    Task<T> UseAsync<T>(string sessionId, long userId, string userName, Func<ChatHistory, Task<T>> action, CancellationToken ct = default);

    /// <summary>只读地取一份会话历史（用于「历史会话」回看）。不存在或不属于该用户时返回 null。</summary>
    Task<IReadOnlyList<ChatMessageContent>?> GetMessagesAsync(string sessionId, long userId, CancellationToken ct = default);

    /// <summary>该用户最近活跃的会话列表，按最后活跃时间倒序。</summary>
    Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(long userId, int limit = 50, CancellationToken ct = default);

    void Clear(string sessionId, long userId);
    int PurgeExpired(TimeSpan ttl);
    int SessionCount { get; }
}

/// <summary>会话元信息：列表要靠它显示标题和时间，与对话内容分开存，免得动到已经验证过保真的那个序列化器。</summary>
public sealed record SessionMetadata(
    string SessionId,
    long UserId,
    string UserName,
    string Title,
    DateTime CreatedAt,
    DateTime LastActiveAt,
    int MessageCount)
{
    /// <summary>用第一条用户消息当标题 —— 比"会话 #abc123"有用得多。</summary>
    public static SessionMetadata FromHistory(
        string sessionId, long userId, string userName, ChatHistory history, DateTime? createdAt = null)
    {
        var title = "（空会话）";
        foreach (var message in history)
        {
            if (message.Role != AuthorRole.User) continue;
            var text = message.Content?.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;
            title = text.Length <= 24 ? text : text[..24] + "…";
            break;
        }

        return new SessionMetadata(sessionId, userId, userName, title, createdAt ?? DateTime.Now, DateTime.Now, history.Count);
    }

    /// <summary>
    /// 取最后一条 AI 回复当摘要，给列表里的预览行用。
    ///
    /// 关键在于**把 Markdown 符号剥干净**：列表里只有两行位置，
    /// 摘要正文常以「## 当前数据」「`tools.json`」「**加粗**」开头，
    /// 原样带出来就是一堆 # 和反引号，又乱又占地方。
    /// </summary>
    public static string? BuildPreview(ChatHistory history)
    {
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var message = history[i];
            if (message.Role != AuthorRole.Assistant) continue;

            var text = message.Content;
            if (string.IsNullOrWhiteSpace(text)) continue;
            // 自动续跑那种合成指令不是给用户看的
            if (Harness.AgentContinuation.IsContinuation(text)) continue;

            var flat = FlattenMarkdown(text);
            if (flat.Length == 0) continue;
            return flat.Length <= 100 ? flat : flat[..100] + "…";
        }
        return null;
    }

    private static string FlattenMarkdown(string text)
    {
        // 先把每一行前面的结构符号去掉，再拼成一行 —— 顺序反了的话
        // 「## 标题」变成「 标题」就再也认不出它是标题了。
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line
                // 标题：## 当前数据
                .Replace("#", string.Empty)
                // 引用：> 说明
                .TrimStart('>', ' ')
                // 列表：- 项目 / * 项目 / 1. 项目
                .TrimStart('-', '*', '+', ' ')
                .Trim())
            .Where(line => line.Length > 0);

        var flat = string.Join(' ', lines);

        // 行内标记
        flat = flat
            .Replace("`", string.Empty)      // 行内代码
            .Replace("**", string.Empty)     // 加粗
            .Replace("__", string.Empty)
            .Replace("|", " ")               // 表格分隔
            .Replace("---", " ");

        // 链接 [文字](url) 只留文字
        flat = System.Text.RegularExpressions.Regex.Replace(flat, @"\[([^\]]*)\]\([^)]*\)", "$1");

        // 上面的替换会留下连续空格
        flat = System.Text.RegularExpressions.Regex.Replace(flat, @"\s{2,}", " ");

        return flat.Trim();
    }
}

public sealed class InMemoryConversationStore : IConversationStore
{
    private sealed class Session
    {
        public readonly ChatHistory History = new();
        public readonly SemaphoreSlim Gate = new(1, 1);
        public long UserId;
        public string UserName = string.Empty;
        public DateTime CreatedAt = DateTime.Now;
        public DateTime LastAccess = DateTime.Now;
        public SessionMetadata? Metadata;
    }

    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly int _maxMessages;

    public InMemoryConversationStore(IConfiguration configuration)
        => _maxMessages = Math.Max(8, configuration.GetValue("Ai:History:MaxMessages", 500));

    public int SessionCount => _sessions.Count;

    /// <summary>
    /// 会话按「用户 + sessionId」分命名空间。sessionId 是客户端生成的，只看它做键的话，
    /// 同一个浏览器换账号登录（localStorage 里还留着上一个人的 sessionId）就会撞车。
    /// </summary>
    private static string Key(long userId, string sessionId) => $"{userId}:{sessionId}";

    public async Task<T> UseAsync<T>(string sessionId, long userId, string userName, Func<ChatHistory, Task<T>> action, CancellationToken ct = default)
    {
        var session = _sessions.GetOrAdd(Key(userId, sessionId), _ => new Session { UserId = userId, UserName = userName });

        // 兜底：键里已经带了 userId，正常不会走到这里；防的是数据被改坏之类的情况
        if (session.UserId != userId) throw new SessionAccessDeniedException(sessionId);

        session.LastAccess = DateTime.Now;

        await session.Gate.WaitAsync(ct);
        try
        {
            var result = await action(session.History);
            Trim(session.History);
            session.Metadata = SessionMetadata.FromHistory(sessionId, userId, userName, session.History, session.CreatedAt);
            return result;
        }
        finally
        {
            session.LastAccess = DateTime.Now;
            session.Gate.Release();
        }
    }

    public Task<IReadOnlyList<ChatMessageContent>?> GetMessagesAsync(string sessionId, long userId, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(Key(userId, sessionId), out var session) || session.UserId != userId)
            return Task.FromResult<IReadOnlyList<ChatMessageContent>?>(null);

        lock (session.History)
            return Task.FromResult<IReadOnlyList<ChatMessageContent>?>(session.History.ToList());
    }

    public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(long userId, int limit = 50, CancellationToken ct = default)
    {
        var sessions = _sessions
            .Where(kv => kv.Value.UserId == userId)
            .Select(kv => kv.Value.Metadata ?? new SessionMetadata(
                kv.Key[(kv.Key.IndexOf(':') + 1)..], userId, kv.Value.UserName, "（空会话）",
                kv.Value.CreatedAt, kv.Value.LastAccess, kv.Value.History.Count))
            .OrderByDescending(m => m.LastActiveAt)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(m => new SessionSummary(
                m.SessionId, m.Title, m.MessageCount, m.CreatedAt, m.LastActiveAt,
                SessionMetadata.BuildPreview(_sessions[Key(userId, m.SessionId)].History)))
            .ToList();

        return Task.FromResult<IReadOnlyList<SessionSummary>>(sessions);
    }

    public void Clear(string sessionId, long userId)
    {
        if (_sessions.TryRemove(Key(userId, sessionId), out var session)) session.Gate.Dispose();
    }

    public int PurgeExpired(TimeSpan ttl)
    {
        var cutoff = DateTime.Now - ttl;
        var stale = _sessions.Where(kv => kv.Value.LastAccess < cutoff).Select(kv => kv.Key).ToList();
        foreach (var key in stale)
            if (_sessions.TryRemove(key, out var session)) session.Gate.Dispose();
        return stale.Count;
    }

    /// <summary>压缩历史。细节见 <see cref="ChatHistoryCompactor"/>。</summary>
    private void Trim(ChatHistory history) => ChatHistoryCompactor.Compact(history, _maxMessages);
}
