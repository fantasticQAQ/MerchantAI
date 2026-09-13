using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MerchantAdmin.AI.API.Auth;
using MerchantAdmin.AI.API.Harness;
using MerchantAdmin.AI.API.Ai.Tools;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace MerchantAdmin.AI.API.Controllers;

/// <summary>
/// 会话记录与审计轨迹的查看接口。
///
/// 在此之前这些数据是"只写不读"的：会话在 Redis 里，轨迹在 traces/*.jsonl 里，但没有任何地方能看到。
/// 现在全部要求登录，而且**只能看自己的**：
/// - 会话按 userId 过滤，别人的会话既不列出来也不给读（返回 404 而不是 403，避免泄露"存在但不属于你"）
/// - 审计轨迹带 userId，非管理员只看自己的
/// </summary>
[ApiController]
[Route("api/ai")]
[Authorize]
public class SessionsController : ControllerBase
{
    private readonly IConversationStore _conversations;
    private readonly IPendingActionStore _pending;
    private readonly AgentTraceService _trace;
    private readonly IVisibilityStore _visibility;
    private readonly ISessionPrefsStore _prefs;
    private readonly ICurrentUser _user;

    public SessionsController(
        IConversationStore conversations,
        IPendingActionStore pending,
        AgentTraceService trace,
        IVisibilityStore visibility,
        ISessionPrefsStore prefs,
        ICurrentUser user)
    {
        _conversations = conversations;
        _pending = pending;
        _trace = trace;
        _visibility = visibility;
        _prefs = prefs;
        _user = user;
    }

    /// <summary>
    /// 当前用户最近的会话列表（按最后活跃时间倒序）。
    ///
    /// **被隐藏（= 用户点了删除）的会话不会出现在这里。**
    /// 删除是软删除：Redis 里的会话和轨迹一条都不动，只是不再展示。
    /// 置顶的排在前面；每条带上所属分组，前端按分组渲染。
    /// </summary>
    [HttpGet("sessions")]
    public async Task<IActionResult> ListSessions([FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var hidden = _visibility.HiddenSessions(_user.UserId);
        var hiddenTraces = _visibility.HiddenTraces(_user.UserId);
        var prefs = _prefs.All(_user.UserId);

        // 排序分三层：置顶的抬到最前 → 用户拖出来的顺序 → 最后活跃时间。
        // 没拖过的（SortOrder 为 null）排在拖过的后面、按时间倒序，这样第一次拖动之前
        // 列表仍然是熟悉的「最近聊过的在最上面」。
        var ordered = (await _conversations.ListSessionsAsync(_user.UserId, limit, ct))
            .Where(s => !hidden.Contains(s.SessionId))
            .OrderBy(s => EffectivePinned(s.SessionId) ? 0 : 1)
            .ThenBy(s => PrefsOf(s.SessionId).SortOrder ?? int.MaxValue)
            .ThenByDescending(s => s.LastActiveAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToList();

        return Ok(new
        {
            count = ordered.Count,
            groups = _prefs.Groups(_user.UserId)
                .OrderByDescending(g => g.Pinned)
                .ThenBy(g => g.CreatedAt)
                .Select(g => new
                {
                    groupId = g.Id,
                    name = g.Name,
                    pinned = g.Pinned,
                    sessionCount = ordered.Count(s => PrefsOf(s.SessionId).GroupId == g.Id)
                }),
            sessions = ordered.Select(s => new
            {
                sessionId = s.SessionId,
                // 手动命名的 > 模型总结的 > 第一条用户消息
                title = EffectiveTitle(s.SessionId, s.Title),
                titleIsAuto = PrefsOf(s.SessionId).TitleIsAuto,
                // 标题下面那两行摘要，一眼看出这个会话聊了什么
                preview = s.Preview,
                messageCount = s.MessageCount,
                pinned = EffectivePinned(s.SessionId),
                groupId = PrefsOf(s.SessionId).GroupId,
                // 本次改动之前写入的旧会话没有元信息，时间确实是未知的 —— 返回 null 而不是编一个
                createdAt = s.CreatedAt == DateTime.MinValue ? (DateTime?)null : s.CreatedAt,
                lastActiveAt = s.LastActiveAt == DateTime.MinValue ? (DateTime?)null : s.LastActiveAt,
                // 该会话还有多少条待确认操作没处理（只算自己的）
                pendingCount = OwnPending(s.SessionId).Count,
                // 该会话有多少条审计轨迹 —— 左侧列表用它提示「点进去能看什么」
                traceCount = hiddenTraces.Contains(s.SessionId)
                    ? 0
                    : _trace.CountBySession(s.SessionId, _user.UserId)
            })
        });

        SessionPrefs PrefsOf(string sessionId)
            => prefs.TryGetValue(sessionId, out var p) ? p : new SessionPrefs();

        bool EffectivePinned(string sessionId) => PrefsOf(sessionId).Pinned;

        string EffectiveTitle(string sessionId, string fallback)
        {
            var prefs = PrefsOf(sessionId);
            return string.IsNullOrWhiteSpace(prefs.Title) ? fallback : prefs.Title;
        }
    }

    /// <summary>改会话的名字 / 置顶 / 所属分组。只传要改的字段，没传的保持不变。</summary>
    [HttpPatch("sessions/{sessionId}")]
    public async Task<IActionResult> UpdateSession(string sessionId, [FromBody] UpdateSessionRequest req)
    {
        if (await _conversations.GetMessagesAsync(sessionId, _user.UserId) is null)
            return NotFound(new { message = "会话不存在或已过期" });

        var prefs = _prefs.Get(_user.UserId, sessionId);

        if (req.Title is not null)
        {
            var title = req.Title.Trim();
            if (title.Length == 0) return BadRequest(new { message = "标题不能为空" });
            if (title.Length > 40) return BadRequest(new { message = "标题最长 40 个字符" });
            // 手动命名：TitleIsAuto=false，之后模型就不许再覆盖它了
            prefs = prefs with { Title = title, TitleIsAuto = false };
        }

        if (req.Pinned is { } pinned) prefs = prefs with { Pinned = pinned };

        if (req.GroupId is not null)
        {
            // 空字符串 = 移出分组
            var groupId = string.IsNullOrWhiteSpace(req.GroupId) ? null : req.GroupId.Trim();
            if (groupId is not null && _prefs.Groups(_user.UserId).All(g => g.Id != groupId))
                return BadRequest(new { message = "分组不存在" });
            prefs = prefs with { GroupId = groupId };
        }

        _prefs.Save(_user.UserId, sessionId, prefs);

        return Ok(new
        {
            sessionId,
            title = prefs.Title,
            titleIsAuto = prefs.TitleIsAuto,
            pinned = prefs.Pinned,
            groupId = prefs.GroupId
        });
    }

    // ---------------------------------------------------------------- 会话分组

    /// <summary>
    /// 用拖动后的顺序重排一批会话。
    /// 前端把它所在那个区（分组 / 未分组 / 置顶）的完整 id 列表发过来，后端按数组下标写 <c>SortOrder</c>。
    /// 整体重排而不是算中间值：一个区里几十个会话，这点写入量可以忽略，逻辑却简单得多。
    /// </summary>
    [HttpPost("sessions/order")]
    public IActionResult ReorderSessions([FromBody] ReorderRequest req)
    {
        var ids = req.SessionIds ?? new List<string>();
        if (ids.Count == 0) return BadRequest(new { message = "sessionIds 不能为空" });
        if (ids.Count > 500) return BadRequest(new { message = "一次最多重排 500 个会话" });

        for (var i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            if (string.IsNullOrWhiteSpace(id)) continue;

            // 键本来就带 userId，写别人的 sessionId 只会留一条对自己无意义的记录，读不回来
            var prefs = _prefs.Get(_user.UserId, id);
            _prefs.Save(_user.UserId, id, prefs with { SortOrder = i });
        }

        return Ok(new { count = ids.Count });
    }

    /// <summary>新建一个分组。</summary>
    [HttpPost("groups")]
    public IActionResult CreateGroup([FromBody] GroupRequest req)
    {
        var name = (req.Name ?? string.Empty).Trim();
        if (name.Length == 0) return BadRequest(new { message = "分组名称不能为空" });
        if (name.Length > 20) return BadRequest(new { message = "分组名称最长 20 个字符" });

        var group = _prefs.CreateGroup(_user.UserId, name);
        return Ok(new { groupId = group.Id, name = group.Name, pinned = group.Pinned });
    }

    /// <summary>改分组名字 / 置顶。</summary>
    [HttpPatch("groups/{groupId}")]
    public IActionResult UpdateGroup(string groupId, [FromBody] GroupRequest? req = null)
    {
        if (!_prefs.UpdateGroup(_user.UserId, groupId, req?.Name, req?.Pinned))
            return NotFound(new { message = "分组不存在" });

        var group = _prefs.Groups(_user.UserId).First(g => g.Id == groupId);
        return Ok(new { groupId = group.Id, name = group.Name, pinned = group.Pinned });
    }

    /// <summary>
    /// 删除分组。**组里的会话不会被删**，只是回到「未分组」——
    /// 删一个整理用的抽屉，不该把抽屉里的东西一起扔掉。
    /// </summary>
    [HttpDelete("groups/{groupId}")]
    public IActionResult DeleteGroup(string groupId)
    {
        if (_prefs.Groups(_user.UserId).All(g => g.Id != groupId))
            return NotFound(new { message = "分组不存在" });

        foreach (var (sessionId, prefs) in _prefs.All(_user.UserId))
            if (prefs.GroupId == groupId)
                _prefs.Save(_user.UserId, sessionId, prefs with { GroupId = null });

        _prefs.DeleteGroup(_user.UserId, groupId);
        return Ok(new { reply = "分组已删除，组里的会话已移到未分组" });
    }

    /// <summary>某个会话的完整记录：对话内容 + 还没处理的确认卡片。前端「点开继续聊」用它恢复现场。</summary>
    [HttpGet("sessions/{sessionId}")]
    public async Task<IActionResult> GetSession(string sessionId, CancellationToken ct = default)
    {
        // 隐藏过的会话按「不存在」处理 —— 否则从 URL 里还能把它翻出来
        if (_visibility.IsSessionHidden(_user.UserId, sessionId))
            return NotFound(new { message = "会话不存在或已删除" });

        var messages = await _conversations.GetMessagesAsync(sessionId, _user.UserId, ct);
        // 不存在、或属于别人 —— 一律当作不存在
        if (messages is null) return NotFound(new { message = "会话不存在或已过期" });

        var pending = OwnPending(sessionId);

        return Ok(new
        {
            sessionId,
            // 自动续跑用的合成消息不是用户说的话，展示时过滤掉
            messages = messages
                .Where(m => !AgentContinuation.IsContinuation(m.Content))
                .Select(ToTranscript)
                .ToArray(),
            pendingActions = pending.Select(a => new
            {
                actionId = a.ActionId,
                toolName = a.ToolName,
                summary = a.Summary,
                createdAt = a.CreatedAt
            }).ToArray()
        });
    }

    /// <summary>
    /// 删除会话。**软删除**：只在可见性表里记一笔，Redis 里的会话历史和审计轨迹原样保留，
    /// 只是不再出现在任何列表/查询里（连同这个会话的轨迹一起隐藏）。
    ///
    /// 之所以不真删：轨迹和会话都要求永久保留，删掉就没法追溯了。
    /// 真要恢复，把 <c>merchant-ai:hidden:sessions</c> 里的成员去掉即可。
    /// </summary>
    [HttpDelete("sessions/{sessionId}")]
    public async Task<IActionResult> DeleteSession(string sessionId, CancellationToken ct = default)
    {
        // 先确认这个会话确实属于当前用户
        if (await _conversations.GetMessagesAsync(sessionId, _user.UserId, ct) is null)
            return NotFound(new { message = "会话不存在或已过期" });

        // 待确认操作是真删的：它本来就只活到用户点完为止，留着反而可能被误执行
        foreach (var action in OwnPending(sessionId))
            _pending.Remove(action.ActionId);

        _visibility.HideSession(_user.UserId, sessionId);
        _trace.Record(sessionId, "session_hidden", string.Empty, new { soft = true }, _user.UserId);

        return Ok(new { reply = "会话已删除" });
    }

    // 刻意**没有**「只删轨迹」的接口：轨迹是会话的审计记录，跟着会话一起走。
    // 允许单独抹掉轨迹，等于给审计链开了个后门 —— 想删就删会话，语义清楚，也不会留下
    // 「会话还在、但发生过什么没人知道」的空档。

    /// <summary>
    /// 审计轨迹：谁在什么时候用了什么工具、发了什么参数、结果如何、有没有触发审批。
    ///
    /// 两种读法：
    /// - **带 sessionId**：读这个会话自己的轨迹（跨天合并），**按时间正序**返回 —— 前端是「选中会话看它的时间线」，
    ///   从早到晚读才像一条流程。此时忽略 date。
    /// - **不带 sessionId**：读某一天的全部轨迹（默认今天），倒序返回，用于运维视角。
    ///
    /// 非管理员只能看自己的。
    /// </summary>
    [HttpGet("traces")]
    public IActionResult GetTraces(
        [FromQuery] int limit = 200,
        [FromQuery] string? sessionId = null,
        [FromQuery] string? eventType = null,
        [FromQuery] string? date = null)
    {
        // 管理员能看全部；普通用户只看自己的
        var ownerFilter = _user.IsAdmin ? (long?)null : _user.UserId;

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            // 隐藏过的轨迹一律当作没有 —— 用户点了「删除轨迹」之后就不该再看到
            if (_visibility.IsTraceHidden(_user.UserId, sessionId))
                return Ok(new
                {
                    scope = _user.IsAdmin ? "all" : "mine",
                    sessionId,
                    order = "asc",
                    count = 0,
                    entries = Array.Empty<object>()
                });

            // 按会话读。归属校验：普通用户只能读自己的会话轨迹。
            // 会话本身也要属于他 —— 否则拿到别人的 sessionId 就能把轨迹翻出来。
            var owner = ownerFilter ?? OwnSessionOwner(sessionId);
            var sessionEntries = _trace.ReadSession(sessionId, owner, limit);

            if (!string.IsNullOrWhiteSpace(eventType))
                sessionEntries = sessionEntries
                    .Where(e => string.Equals(e.EventType, eventType, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            return Ok(new
            {
                scope = _user.IsAdmin ? "all" : "mine",
                sessionId,
                order = "asc",
                count = sessionEntries.Count,
                entries = sessionEntries.Select(ToTraceDto)
            });
        }

        DateOnly? target = null;
        if (!string.IsNullOrWhiteSpace(date))
        {
            if (!DateOnly.TryParse(date, out var parsed))
                return BadRequest(new { message = "date 格式应为 yyyy-MM-dd" });
            target = parsed;
        }

        var entries = _trace.Read(limit, null, eventType, target, ownerFilter)
            .Where(e => !_visibility.IsTraceHidden(
                e.UserId == 0 ? _user.UserId : e.UserId,
                e.SessionId))
            .ToList();

        return Ok(new
        {
            scope = _user.IsAdmin ? "all" : "mine",
            date = (target ?? DateOnly.FromDateTime(DateTime.Now)).ToString("yyyy-MM-dd"),
            availableDates = _trace.AvailableDates().Select(d => d.ToString("yyyy-MM-dd")),
            count = entries.Count,
            entries = entries.Select(ToTraceDto)
        });
    }

    private static object ToTraceDto(TraceEntry e) => new
    {
        time = e.Time,
        sessionId = e.SessionId,
        userId = e.UserId,
        eventType = e.EventType,
        functionName = e.FunctionName,
        payload = e.Payload
    };

    /// <summary>
    /// 从轨迹里反查某个会话属于谁。会话本体已经被删掉时（轨迹还在），
    /// 管理员之外的人就不该再看到它 —— 找不到归属就返回一个不可能匹配的 id。
    /// </summary>
    private long OwnSessionOwner(string sessionId)
    {
        var entry = _trace.Read(1, sessionId, null, null, null).FirstOrDefault();
        return entry?.UserId ?? -1;
    }

    /// <summary>轨迹里出现过的会话（含事件数），用于审计视角的会话清单。</summary>
    [HttpGet("traces/sessions")]
    public IActionResult GetTraceSessions([FromQuery] string? date = null)
    {
        DateOnly? target = null;
        if (!string.IsNullOrWhiteSpace(date) && DateOnly.TryParse(date, out var parsed)) target = parsed;

        var ownerFilter = _user.IsAdmin ? (long?)null : _user.UserId;
        var hiddenTraces = _visibility.HiddenTraces(_user.UserId);
        var hiddenSessions = _visibility.HiddenSessions(_user.UserId);

        var entries = _trace.Read(2000, null, null, target, ownerFilter)
            .Where(e => !hiddenTraces.Contains(e.SessionId) && !hiddenSessions.Contains(e.SessionId))
            .ToList();

        var sessions = entries
            .GroupBy(e => e.SessionId)
            .Select(g => new
            {
                sessionId = g.Key,
                eventCount = g.Count(),
                lastActive = g.Max(e => e.Time),
                userInputs = g.Count(e => e.EventType == "user_input"),
                toolCalls = g.Count(e => e.EventType is "tool_post" or "tool_pre"),
                approvals = g.Count(e => e.EventType == "approval_pending"),
                blocked = g.Count(e => e.EventType is "tool_blocked" or "tool_denied" or "loop_guard" or "approval_invalid")
            })
            .OrderByDescending(s => s.lastActive)
            .ToList();

        return Ok(new
        {
            scope = _user.IsAdmin ? "all" : "mine",
            date = (target ?? DateOnly.FromDateTime(DateTime.Now)).ToString("yyyy-MM-dd"),
            count = sessions.Count,
            sessions
        });
    }

    private List<PendingAction> OwnPending(string sessionId)
        => _pending.ListBySession(sessionId)
            .Where(a => a.UserId == 0 || a.UserId == _user.UserId)
            .ToList();

    /// <summary>把 SK 的消息转成前端好渲染的形状。工具调用/结果一并带上，聊天视图和审计视图共用。</summary>
    private static object ToTranscript(ChatMessageContent message)
    {
        var toolCalls = message.Items.OfType<FunctionCallContent>()
            .Select(c => new
            {
                id = c.Id,
                name = string.IsNullOrEmpty(c.PluginName) ? c.FunctionName : $"{c.PluginName}-{c.FunctionName}",
                arguments = c.Arguments is null ? null : JsonText.Render(ToNode(c.Arguments))
            })
            .ToList();

        var resultName = message.Items.OfType<FunctionResultContent>().FirstOrDefault()?.FunctionName;

        return new
        {
            role = message.Role.Label.ToLowerInvariant(),
            text = message.Content,
            toolName = resultName,
            toolCalls = toolCalls.Count > 0 ? toolCalls : null
        };
    }

    private static System.Text.Json.Nodes.JsonNode? ToNode(KernelArguments arguments)
    {
        var node = new System.Text.Json.Nodes.JsonObject();
        foreach (var (key, value) in arguments)
        {
            node[key] = value switch
            {
                null => null,
                System.Text.Json.Nodes.JsonNode n => n.DeepClone(),
                System.Text.Json.JsonElement element => System.Text.Json.Nodes.JsonNode.Parse(element.GetRawText()),
                var other => System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(other))
            };
        }
        return node;
    }
}

/// <summary>改会话。只传要改的字段 —— 全是可空，没传的保持原样。</summary>
/// <param name="GroupId">空字符串表示移出分组；null 表示不改。</param>
public record UpdateSessionRequest(string? Title = null, bool? Pinned = null, string? GroupId = null);

/// <summary>新建 / 修改分组。</summary>
public record GroupRequest(string? Name = null, bool? Pinned = null);

/// <summary>拖完之后的完整顺序，按数组下标写 SortOrder。</summary>
public record ReorderRequest(List<string>? SessionIds = null);
