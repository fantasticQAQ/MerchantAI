using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace MerchantAdmin.AI.API.Harness;

/// <summary>
/// 会话历史的压缩。
///
/// 为什么需要它：批量任务（「建 1000 个商品」）每一轮都会产生「1 条带 tool_calls 的助手消息 + N 条工具结果」。
/// 60 次调用就是 62 条消息，几轮下来历史里全是工具脚手架：
/// - 把上下文撑爆（token 全花在已经没用的工具返回值上）
/// - 把用户消息挤出去（实测把会话裁到只剩 1 条，标题都变成了「（空会话）」）
/// - 之前那段「裁到 40 条再删开头的孤儿 tool 消息」的逻辑更糟：裁剪点落在一批工具结果中间时，
///   它会把剩下的几十条**全部删光**，历史直接归零，模型彻底丢失进度
///
/// 正确做法：先丢掉工具脚手架（助手随后会用自己的话总结结果，那些原始返回值留一份在审计轨迹里就够了），
/// 再按条数裁剪 —— 此时历史里只剩「用户消息 / 助手文本」，怎么裁都不会产生孤儿工具结果。
/// </summary>
public static class ChatHistoryCompactor
{
    public static void Compact(ChatHistory history, int maxMessages, bool dropToolScaffolding = true)
    {
        if (dropToolScaffolding) DropToolScaffolding(history);
        TrimTo(history, maxMessages);
    }

    /// <summary>
    /// 丢掉所有「工具调用步骤」—— 包括那些**带了文字**的。
    ///
    /// 带文字的助手消息 + tool_calls，就是模型在调工具前说的那句前言
    /// （实测出现过英文的「I'll query the current products and orders for you.」）。
    /// 它有两个坏处：
    /// - 实时对话只显示本轮的**最后一条**助手消息，这句前言看不见；
    ///   刷新页面走的是「列出全部历史消息」这条路，它又冒出来了 —— 同一段对话刷新前后不一样。
    /// - 模型偶尔会用英文写前言，而系统提示词要求全程中文。
    ///
    /// 所以整条丢掉，只留下真正的答复。丢掉的内容不影响上下文：
    /// 真正的结论在最后那条答复里，而前言只是「我准备去查了」这种过渡话。
    /// </summary>
    private static void DropToolScaffolding(ChatHistory history)
    {
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var message = history[i];

            if (message.Role == AuthorRole.Tool)
            {
                history.RemoveAt(i);
                continue;
            }

            // 带工具调用的助手消息 = 中间步骤，连它带的文字一起丢掉
            if (message.Items.OfType<FunctionCallContent>().Any())
            {
                history.RemoveAt(i);
                continue;
            }

            // 走到这里只剩纯文本消息；空壳（例如只剩个空串）也清掉，否则会被 OpenAI 拒绝
            if (message.Items.Count == 0 && string.IsNullOrWhiteSpace(message.Content))
                history.RemoveAt(i);
        }
    }

    /// <summary>
    /// 按条数裁剪，永远保留最后一条（正在生成的这条通常是最终答复）。
    /// 赶在这里做是安全的：工具脚手架已经清掉，不可能再裁出孤儿工具结果。
    /// </summary>
    private static void TrimTo(ChatHistory history, int maxMessages)
    {
        var limit = Math.Max(2, maxMessages);
        while (history.Count > limit)
            history.RemoveAt(0);
    }
}
