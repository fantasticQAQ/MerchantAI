using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MerchantAdmin.AI.API.Auth;
using MerchantAdmin.AI.API.Harness;

namespace MerchantAdmin.AI.API.Controllers;

[ApiController]
[Route("api/ai")]
[Authorize]
public class ChatController : ControllerBase
{
    public const string IdentityClientName = "identity";

    private readonly StoreAgent _agent;
    private readonly IPendingActionStore _pendingStore;
    private readonly HumanApprovalService _approvals;
    private readonly AgentTraceService _trace;
    private readonly SessionTitleService _titles;
    private readonly ICurrentUser _user;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ChatController> _logger;
    private readonly bool _autoContinue;
    private readonly int _maxContinuationRounds;
    private readonly int _maxToolCallsPerTurn;

    public ChatController(
        StoreAgent agent,
        IPendingActionStore pendingStore,
        HumanApprovalService approvals,
        AgentTraceService trace,
        SessionTitleService titles,
        ICurrentUser user,
        IHttpClientFactory httpFactory,
        IConfiguration configuration,
        ILogger<ChatController> logger)
    {
        _agent = agent;
        _pendingStore = pendingStore;
        _approvals = approvals;
        _trace = trace;
        _titles = titles;
        _user = user;
        _httpFactory = httpFactory;
        _logger = logger;
        _autoContinue = configuration.GetValue("Ai:Agent:AutoContinueAfterConfirm", true);
        _maxContinuationRounds = Math.Max(1, configuration.GetValue("Ai:Agent:MaxContinuationRounds", 5));
        _maxToolCallsPerTurn = Math.Max(1, configuration.GetValue("Ai:Agent:MaxToolCallsPerTurn", 60));
    }

    /// <summary>
    /// 登录代理。把凭据转发给 Identity.API 换成 JWT。
    ///
    /// 为什么要代理：浏览器直连 :5001 会被 Identity 的 CORS 挡掉（它只允许自己的前端），
    /// 而改业务服务的 CORS 又违反「业务服务不动」的前提。让 AI 后端做服务端转发最省事且不越界。
    /// </summary>
    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.UserName) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { message = "用户名和密码不能为空" });

        try
        {
            var client = _httpFactory.CreateClient(IdentityClientName);
            // 相对路径**不能**以 / 开头：那会按 Uri 规则整个替换掉 BaseAddress 的路径，
            // 把 /api/identity 前缀吃掉，经网关上就变成 404。
            using var response = await client.PostAsJsonAsync("auth/login", new { req.UserName, req.Password }, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return StatusCode((int)response.StatusCode, ParseOrWrap(body));

            return Content(body, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "转发登录请求到 Identity 失败");
            return StatusCode(503, new { message = "认证服务暂时不可用，请稍后重试" });
        }
    }

    /// <summary>当前登录者（前端用来显示用户名/角色）。</summary>
    [HttpGet("me")]
    public IActionResult Me() => Ok(new
    {
        userId = _user.UserId,
        userName = _user.UserName,
        roles = _user.Roles,
        isAdmin = _user.IsAdmin
    });

    /// <summary>Agent 对话入口。多轮由会话历史支撑，多步工具调用由 FunctionChoiceBehavior.Auto 编排。</summary>
    [HttpPost("chat")]
    public async Task<IActionResult> Chat([FromBody] ChatRequest req)
    {
        // 前端驱动的续跑：长任务（建 1000 个商品）单次请求跑完会撞上传超时，
        // 所以后端跑满一轮预算就把 hasMore 交回前端，前端拿同一个会话自动再发一次 —— 用户不用打「继续」。
        var isAutoContinue = req.AutoContinue == true;
        var message = isAutoContinue ? AgentContinuation.Message : req.Message;

        if (string.IsNullOrWhiteSpace(message))
            return BadRequest(new { message = "message 不能为空" });

        var sessionId = string.IsNullOrWhiteSpace(req.SessionId) ? Guid.NewGuid().ToString("N") : req.SessionId;
        HttpContext.Items["SessionId"] = sessionId;

        // 权限级别由前端下拉框决定，默认「需确认」。漏传不会变成无确认执行。
        var permissionMode = AgentPermission.Parse(req.Mode);
        AgentPermission.Set(HttpContext, permissionMode);

        // 续跑是机器自己发的，不该在会话记录里冒充用户消息
        if (!isAutoContinue)
            _trace.Record(sessionId, "user_input", string.Empty,
                new { message, mode = permissionMode.ToWire() }, _user.UserId);

        string reply;
        try
        {
            reply = await _agent.InvokeAsync(
                sessionId, _user.UserId, _user.UserName, message, permissionMode, HttpContext.RequestAborted);
        }
        catch (SessionAccessDeniedException)
        {
            // 不告诉对方"这个会话存在但属于别人"
            return StatusCode(403, new { message = "无权访问该会话" });
        }
        catch (Exception ex)
        {
            _trace.Record(sessionId, "agent_error", string.Empty, ex.Message, _user.UserId);
            return StatusCode(500, new { message = "AI 调用失败：" + ex.Message, sessionId });
        }

        // 「完全权限」下没有确认环节，本轮被工具调用上限截断时不会有任何东西把它推起来 ——
        // 只能等用户说「继续」。这里自动接着跑，直到做完或达到续跑轮次上限。
        var continuationRounds = 0;
        while (permissionMode == AiPermissionMode.Full
               && continuationRounds < _maxContinuationRounds
               && WasTruncated())
        {
            continuationRounds++;
            ResetTurnCounters();

            _trace.Record(sessionId, "auto_continue_round", "truncated", new { round = continuationRounds }, _user.UserId);

            try
            {
                reply = await _agent.InvokeAsync(
                    sessionId, _user.UserId, _user.UserName, AgentContinuation.Message, permissionMode, HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                _trace.Record(sessionId, "agent_error", "auto_continue_round", ex.Message, _user.UserId);
                reply += $"\n\n（继续执行时出错：{ex.Message}）";
                break;
            }
        }

        // 本轮产生的全部待确认操作（一轮里可能登记了多个写操作），交给前端弹卡片
        var pendingActions = CollectPendingActions();

        // 跑满续跑预算还被截断 → 把「还没做完」交给前端，由它自动再发一轮，避免单个 HTTP 请求长到超时。
        // 有待确认操作时不能续跑：那是在等用户点确认，不是被截断。
        var hasMore = pendingActions.Count == 0
                      && permissionMode == AiPermissionMode.Full
                      && WasTruncated();

        // 用模型把这一轮概括成标题（已有手动命名则不动）。失败就返回 null，前端继续用旧标题。
        var title = await _titles.MaybeGenerateAsync(_user.UserId, sessionId, message, reply, HttpContext.RequestAborted);

        // 回答只记摘要，不整段存。轨迹的用处是「模型当时调了什么、系统怎么处理的」，
        // 回答全文既没人会逐行读，又会把轨迹撑得很大 —— 想看回答，会话记录里本来就有。
        _trace.Record(sessionId, "answer", string.Empty, new
        {
            chars = reply.Length,
            preview = reply.Length <= 120 ? reply : reply[..120] + "…"
        }, _user.UserId);
        return Ok(new
        {
            sessionId,
            mode = permissionMode.ToWire(),
            reply,
            title,
            hasMore,
            pendingActions = pendingActions.Select(pa => new { actionId = pa.ActionId, summary = pa.Summary }).ToArray(),
            pendingAction = pendingActions.Count > 0
                ? new { actionId = pendingActions[^1].ActionId, summary = pendingActions[^1].Summary }
                : null
        });
    }

    /// <summary>收集本轮登记下来的待确认操作，按归属过滤。</summary>
    private List<PendingAction> CollectPendingActions()
    {
        var list = new List<PendingAction>();
        if (HttpContext.Items["PendingActionIds"] is List<string> ids)
        {
            foreach (var id in ids)
                if (_pendingStore.Get(id) is { } pa && (pa.UserId == 0 || pa.UserId == _user.UserId))
                    list.Add(pa);
        }
        return list;
    }

    /// <summary>
    /// 本轮是否被工具调用上限截断。
    ///
    /// 不能只看 <see cref="AgentLoopState.TruncatedKey"/>：那个标志只在「第 61 次调用被拒」时才置位。
    /// 模型如果自己数着次数、做完第 60 个就直接总结收尾（实测就是这么干的），标志永远不会置位，
    /// 自动续跑就静默失效了 —— 这正是「建 1000 个商品只跑了一批就停下」的原因。
    /// 所以按实际用量判断：用满了额度就当作被截断。
    /// </summary>
    private bool WasTruncated()
    {
        if (HttpContext.Items[AgentLoopState.TruncatedKey] as bool? == true) return true;
        return AgentLoopState.Read(HttpContext, AgentLoopState.ToolCallCountKey) >= _maxToolCallsPerTurn;
    }

    /// <summary>开始新一轮之前把计数清零，否则上一轮的用量会被误判成本轮已截断。</summary>
    private void ResetTurnCounters()
    {
        HttpContext.Items.Remove(AgentLoopState.TruncatedKey);
        HttpContext.Items.Remove(AgentLoopState.ToolCallCountKey);
        HttpContext.Items.Remove("PendingActionIds");
    }

    /// <summary>人工确认执行：把已登记的待确认操作原样重放。这里没有任何按工具名分支的逻辑。</summary>
    [HttpPost("confirm")]
    public async Task<IActionResult> Confirm([FromBody] ConfirmRequest req)
    {
        var ids = ParseActionIds(req.ActionId, req.ActionIds);
        if (ids.Count == 0)
            return BadRequest(new { message = "actionId 不能为空" });

        var sessionId = string.IsNullOrWhiteSpace(req.SessionId) ? "unknown" : req.SessionId;
        HttpContext.Items["SessionId"] = sessionId;

        var autoContinue = _autoContinue && req.Continue != false;
        // 续跑这一轮也要按当前权限级别来（前端会带上）
        var permissionMode = AgentPermission.Parse(req.Mode);

        var outcomes = await _approvals.ConfirmManyAsync(ids, sessionId, _user.UserId, HttpContext.RequestAborted);

        var succeeded = outcomes.Where(o => o.Success).ToList();
        var failed = outcomes.Where(o => !o.Success).ToList();

        var sb = new StringBuilder();
        if (succeeded.Count > 0)
        {
            sb.AppendLine($"已成功执行 {succeeded.Count} 项：");
            foreach (var o in succeeded)
                sb.AppendLine($"- {o.Summary} → 业务返回：{o.Data}");
        }
        if (failed.Count > 0)
        {
            sb.AppendLine($"以下 {failed.Count} 项未执行成功：");
            foreach (var o in failed)
            {
                // 找不到操作时 Summary / ToolName 都是空的，回退到 actionId，避免打出一行没头没尾的错误
                var label = !string.IsNullOrWhiteSpace(o.Summary) ? o.Summary
                    : !string.IsNullOrWhiteSpace(o.ToolName) ? o.ToolName
                    : o.ActionId;
                sb.AppendLine($"- {label}：{o.Error}");
            }
        }

        var reply = sb.ToString().Trim();

        // 把执行结果写回会话历史，模型下一轮才知道这件事已经办过了
        if (reply.Length > 0)
        {
            try
            {
                await _agent.NoteAsync(sessionId, _user.UserId, _user.UserName, reply, HttpContext.RequestAborted);
            }
            catch (SessionAccessDeniedException)
            {
                // 只是记不进历史，不影响本次执行结果
            }
        }

        // 自动续跑：确认完就让模型接着做下一步，用户不用再打一次「继续」。
        // 每一步写操作照样要用户点确认 —— 少掉的只是中间的「继续」对话。
        //
        // 这里也要按预算循环：一次确认可能解掉 50 条操作，模型接下来可能还要跑满好几轮工具调用
        // （查库存、核对、再登记下一批）。只续跑一轮的话，大批量任务会在半路静默停下。
        string? continuation = null;
        var newPending = new List<PendingAction>();
        var confirmHasMore = false;

        if (autoContinue && succeeded.Count > 0)
        {
            for (var round = 0; round < _maxContinuationRounds; round++)
            {
                ResetTurnCounters();
                try
                {
                    continuation = await _agent.InvokeAsync(
                        sessionId, _user.UserId, _user.UserName, AgentContinuation.Message, permissionMode, HttpContext.RequestAborted);

                    newPending = CollectPendingActions();

                    // 又有卡片要确认 / 没被截断（模型自己收尾了）→ 这一轮就到这儿
                    if (newPending.Count > 0 || !WasTruncated()) break;
                }
                catch (SessionAccessDeniedException)
                {
                    // 会话归属变了，忽略续跑
                    break;
                }
                catch (Exception ex)
                {
                    _trace.Record(sessionId, "agent_error", "auto_continue", ex.Message, _user.UserId);
                    continuation = "（后续步骤自动执行失败：" + ex.Message + "，你可以说「继续」重试）";
                    break;
                }
            }

            // 跑满预算还没做完，且没有等用户确认的卡片 → 让前端自动再发一轮
            confirmHasMore = newPending.Count == 0 && WasTruncated();
        }

        return Ok(new
        {
            reply,
            continuation,
            hasMore = confirmHasMore,
            pendingActions = newPending.Select(pa => new { actionId = pa.ActionId, summary = pa.Summary }).ToArray(),
            successCount = succeeded.Count,
            failedCount = failed.Count,
            errors = failed.Select(o => o.Error).ToArray()
        });
    }

    /// <summary>取消待确认操作：从队列里丢弃，之后即使拿到 actionId 也执行不了。</summary>
    [HttpPost("cancel")]
    public IActionResult Cancel([FromBody] ConfirmRequest req)
    {
        var ids = ParseActionIds(req.ActionId, req.ActionIds);
        if (ids.Count == 0)
            return BadRequest(new { message = "actionId 不能为空" });

        var sessionId = string.IsNullOrWhiteSpace(req.SessionId) ? "unknown" : req.SessionId;

        // 只能取消自己登记的
        var cancelled = 0;
        foreach (var id in ids)
        {
            var action = _pendingStore.Get(id);
            if (action is null || (action.UserId != 0 && action.UserId != _user.UserId)) continue;
            if (_pendingStore.Remove(id)) cancelled++;
        }

        _trace.Record(sessionId, "approval_cancelled", string.Empty, new { ids, cancelled }, _user.UserId);

        return Ok(new
        {
            reply = cancelled > 0 ? $"已取消 {cancelled} 项待确认操作" : "这些操作已不存在或已执行",
            cancelledCount = cancelled
        });
    }

    private static List<string> ParseActionIds(string? single, string[]? many)
    {
        var ids = new List<string>();
        if (many is { Length: > 0 }) ids.AddRange(many.Where(x => !string.IsNullOrWhiteSpace(x)));
        else if (!string.IsNullOrWhiteSpace(single)) ids.Add(single);
        return ids;
    }

    private static object ParseOrWrap(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(body);
        }
        catch (JsonException)
        {
            return new { message = body };
        }
    }
}

/// <param name="AutoContinue">
/// 前端驱动的续跑。为 true 时后端忽略 <paramref name="Message"/>，改用固定的续跑指令接着做未完成的任务，
/// 并且不会在会话记录里留下一条冒充用户的消息。
/// </param>
public record ChatRequest(string Message, string? SessionId = null, string? Mode = null, bool? AutoContinue = null);

public record LoginRequest(string UserName, string Password);

/// <summary>确认请求：推荐用 actionIds（批量），actionId 保留向后兼容。</summary>
/// <param name="Continue">确认执行完后是否让模型自动继续下一步（默认 true）。</param>
/// <param name="Mode">当前权限级别，用于让续跑那一轮也知道自己是什么权限。</param>
public record ConfirmRequest(
    string? ActionId = null,
    string[]? ActionIds = null,
    string? SessionId = null,
    bool? Continue = null,
    string? Mode = null);
