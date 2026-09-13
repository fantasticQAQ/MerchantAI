using System.Text;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace MerchantAdmin.AI.API.Harness;

/// <summary>
/// 商店 Agent（对应 README 阶段 2 的 ChatCompletionAgent）。
/// 除了提供系统提示词，它还负责：
/// - 把「一轮对话」和「会话历史」串起来，让多轮指代真正可用；
/// - 给单轮对话加总超时（工具调用次数上限在 StoreGuardFilter 里，因为 SK 自己没有这个上限）。
/// 工具本身就是 Kernel 里由 tools.json 生成的那些，这里不关心有哪些工具。
/// </summary>
public sealed class StoreAgent
{
    public const string SystemPrompt = """
        你是「商户管理后台」的 AI 助手，负责帮店主查询商品、订单和经营数据，并在获得店主明确确认后才下单。

        必须遵守的规则：
        1. 涉及事实的问题一律先调用工具拿真实数据，绝对不要凭空编造商品、订单、金额或库存。
        2. 写操作（如下单）是固定两步，必须严格按顺序执行：
           第一步：店主提出写操作需求后，立刻调用对应工具进行登记。工具本身不会真的执行，
                   它只会生成一条「待人工确认」记录，所以调用它是安全的、也是必须的。
           第二步：拿到登记结果后，把登记到的内容如实列给店主，并询问是否确认执行。
           绝不能在没调用工具的情况下就说「请确认，确认后我再提交」——那样确认卡片不会出现，店主根本无法执行。
        3. 写操作会先被登记为「待人工确认」：在店主点击确认之前，绝不能说「已完成」「已下单」「已经扣减库存」。
        4. 工具返回错误时，如实转述错误并给出下一步建议，不要假装成功，也不要反复用同样的参数重试。
        5. 店主说「上面的商品」「刚才那些」时，指的是本次会话中此前出现过的内容。
        6. 只读工具可以直接调用，不需要征求同意；但同一轮里不要重复调用已经拿到结果的工具。
        7. 用中文回答，简洁清晰；列表类数据用 Markdown 表格呈现，金额保留两位小数。

        关于多步任务（很重要）：
        8. 接到一个包含多个步骤的任务时，**先把完整计划想清楚，然后尽量在同一轮里把所有能登记的步骤都登记完**，
           不要做一步就停下来等店主说「继续」。店主点完确认后系统会自动让你接着做下一步，你不需要询问是否继续。
        9. 订单/商品状态**不是可以直接改的字段**，只能通过业务动作逐步达成。规划「覆盖多种状态」这类任务时，
           先把动作链排出来：
           - 待支付(created)：新建订单后默认状态
           - 支付处理中 / 已支付：对待支付订单发起支付，结果由支付网关回调决定
           - 已取消(cancelled)：取消订单
           - 已退款(refunded)：**必须先有一张已支付订单，再对它退款** —— 不能跳过支付直接退款
           - 超时(timedout)：由系统在支付时限（15 分钟）后自动关闭，只能等，不能主动设置
           状态是单向的（取消/退款后不能再回到已支付），所以动作顺序必须提前排好。
        10. 如果店主的多个要求之间**互相冲突**（例如「删光所有订单」又想「每种状态都保留一张」），
            先指出冲突并给出可行方案，不要闷头执行一半。
        11. 批量操作（建/删/改 N 条）**做完之后要重新查询一次核对**：
            数一数实际结果和目标是 N 是否一致。对不上就如实说明差在哪，
            不要直接宣布「全部完成」——实测出现过列了 65 个目标却只发出 64 条删除的情况。
        12. 大规模批量任务（例如「建 1000 个商品」）会撞上系统的单轮工具调用额度，**自动分多轮连续执行**，
            不需要店主催「继续」。注意：**上一轮的工具明细不会带到下一轮**，你能看到的只有自己写下的总结，
            所以每一轮的收尾必须写成「可交接的进度」，至少包含这三样：
            - 目标总数 / 已完成总数 / 还差多少
            - 本轮实际创建或修改的对象编号区间（例如「测试商品121–180，ID 226–285」）
            - **下一轮从哪里接着做**（例如「下一轮从 测试商品181 继续」）
            绝对不要只写「已完成 60 个」这种含糊说法 —— 下一轮的你看不到明细，含糊必然导致重号、跳号或漏号。
        13. **调用工具之前不要先输出一句「我这就去查」之类的前言**，更不要用英文写这类过渡话
            （实测出现过整句英文的 "I'll query the current products and orders for you."）。
            需要数据就直接调用工具，拿到结果之后一次性用中文作答。
        """;

    private readonly ChatCompletionAgent _agent;
    private readonly IConversationStore _conversations;
    private readonly AgentLoopOptions _options;
    private readonly AgentTraceService _trace;
    private readonly ILogger<StoreAgent> _logger;

    public StoreAgent(
        Kernel kernel,
        IConversationStore conversations,
        IConfiguration configuration,
        AgentLoopOptions options,
        AgentTraceService trace,
        ILogger<StoreAgent> logger)
    {
        _conversations = conversations;
        _options = options;
        _trace = trace;
        _logger = logger;

        var settings = new OpenAIPromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
            MaxTokens = configuration.GetValue("Ai:MaxTokens", 2000)
        };

        _agent = new ChatCompletionAgent
        {
            Name = "StoreAgent",
            Instructions = SystemPrompt,
            Kernel = kernel,
            Arguments = new KernelArguments(settings)
        };
    }

    /// <summary>跑一轮对话（含自动工具调用），返回最终回答。历史由 ChatHistoryAgentThread 写进会话中。</summary>
    public Task<string> InvokeAsync(
        string sessionId,
        long userId,
        string userName,
        string userMessage,
        AiPermissionMode permissionMode = AiPermissionMode.Approve,
        CancellationToken ct = default)
        => _conversations.UseAsync(sessionId, userId, userName, async history =>
        {
            // 权限级别和系统上限是「本轮的上下文」，用完就摘掉：
            // 留在历史里，下一轮换了模式就会被旧信息误导。
            var modeNote = new ChatMessageContent(AuthorRole.System, BuildTurnContext(permissionMode));
            history.Add(modeNote);

            try
            {
                var thread = new ChatHistoryAgentThread(history);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(_options.TurnTimeoutSeconds));

                var buffer = new StringBuilder();
                string? lastAssistant = null;
                int? inputTokens = null, outputTokens = null;
                var startedAt = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    await foreach (var item in _agent.InvokeAsync(userMessage, thread, cancellationToken: timeout.Token))
                    {
                        var message = item.Message;
                        if (message.Role != AuthorRole.Assistant) continue;

                        // 中间步骤（工具调用）的 Content 通常为空，取最后一个有内容的助手消息作为最终回答
                        if (message.Content is { Length: > 0 } content)
                        {
                            lastAssistant = content;
                            buffer.Append(content);
                        }

                        // token 用量只在（通常是最后）带了 usage 的那一片上。拿不到就保持 null，
                        // 不要编一个估算值 —— 审计数据宁可缺，也不能假。
                        var (input, output) = ReadUsage(message.Metadata);
                        if (input is not null) inputTokens = input;
                        if (output is not null) outputTokens = output;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _trace.Record(sessionId, "agent_timeout", string.Empty, new { seconds = _options.TurnTimeoutSeconds }, userId);
                    _logger.LogWarning("会话 {Session} 本轮对话超过 {Seconds} 秒被中止", sessionId, _options.TurnTimeoutSeconds);

                    // 已经执行完的写操作是真的生效了，所以别说「什么都没做」，
                    // 也别建议拆小问题 —— 批量任务本来就该是整批跑的。
                    var done = buffer.Length > 0 ? buffer + "\n\n" : string.Empty;
                    return done +
                        $"（本轮超过 {_options.TurnTimeoutSeconds} 秒还没跑完，已中断。**上面已经执行完的部分是真实生效的**，" +
                        "跟我说一声「继续」我就接着往下做。）";
                }
                finally
                {
                    startedAt.Stop();
                }

                // 一轮的体检数据：慢在哪、烧了多少 token。排障和控成本都靠它。
                _trace.Record(sessionId, "turn_summary", string.Empty, new
                {
                    durationMs = (long)startedAt.Elapsed.TotalMilliseconds,
                    inputTokens,
                    outputTokens,
                    totalTokens = inputTokens is null && outputTokens is null ? (int?)null : (inputTokens ?? 0) + (outputTokens ?? 0),
                    replyChars = buffer.Length
                }, userId);

                return lastAssistant ?? buffer.ToString();
            }
            finally
            {
                history.Remove(modeNote);
            }
        }, ct);

    /// <summary>
    /// 从消息元数据里读 token 用量。
    /// OpenAI 只在（通常是最后）那一片流式响应里带 usage，拿不到就返回 null ——
    /// 审计数据宁可缺，也不能编一个估算值出来。
    /// </summary>
    private static (int? Input, int? Output) ReadUsage(IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue("Usage", out var value)) return (null, null);

        try
        {
            return value switch
            {
                OpenAI.Chat.ChatTokenUsage usage => (usage.InputTokenCount, usage.OutputTokenCount),
                _ => (null, null)
            };
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>把确认/执行结果写回会话历史，免得模型下一轮又把同一件事重新提议一遍。</summary>
    public Task NoteAsync(string sessionId, long userId, string userName, string note, CancellationToken ct = default)
        => _conversations.UseAsync(sessionId, userId, userName, history =>
        {
            history.AddAssistantMessage(note);
            return Task.FromResult(0);
        }, ct);

    /// <summary>
    /// 本轮要告诉模型的上下文：当前权限级别 + 系统硬上限。
    ///
    /// 上限必须明说：之前不告诉它，用户问「上限是多少」时它只能瞎猜
    /// （实测它答「我这边看不到具体阈值…大约 70 上下，不敢当准确答案」）——
    /// 典型的「因为不知道所以过度谨慎」。
    /// </summary>
    private string BuildTurnContext(AiPermissionMode mode) => string.Join("\n\n",
        AgentPermission.Describe(mode),
        $"""
         本轮的系统上限（这些是确定的数字，用户问起就直说，不要猜，也不要说"我这边看不到"）：
         - 单轮最多 {_options.MaxToolCallsPerTurn} 次工具调用；
         - 「需确认」模式下单轮最多登记 {_options.MaxApprovalsPerTurn} 条待确认操作；
         - 只读查询默认每页 50 条，最大也是 50。
         达到上限时不要慌：系统会自动让你接着做（「需确认」模式要等用户点完那批确认），
         所以**不要让用户说「继续」**，直接如实汇报"已完成 N 个、还剩 M 个"即可。

         汇报数量时必须数清楚：不要拿「ID 从 X 到 Y」当成个数
         （曾经把「ID 16–105」说成 105 个，实际是 90 个）。
         """);
}
