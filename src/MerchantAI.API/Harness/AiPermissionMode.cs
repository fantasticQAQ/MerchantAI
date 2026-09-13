namespace MerchantAdmin.AI.API.Harness;

/// <summary>
/// AI 的权限级别。对应前端页头那个下拉框，思路借鉴 DSH 的沙箱模式。
///
/// 注意分层：这个级别只决定「写操作要不要人工点确认」，
/// **不决定「这个人有没有资格用这个工具」** —— 后者由 tools.json 的 roles 单独把关。
/// 所以普通操作员即使选了「完全权限」，也依然调不动 Admin 专属的工具。
/// </summary>
public enum AiPermissionMode
{
    /// <summary>仅可查看：任何写操作直接拒绝。</summary>
    ReadOnly = 0,

    /// <summary>需确认（默认）：写操作登记为待人工确认。</summary>
    Approve = 1,

    /// <summary>完全权限：写操作直接执行，不弹确认卡片。每一次都记审计。</summary>
    Full = 2
}

public static class AgentPermission
{
    public const string ItemKey = "__ai_permission_mode__";

    public static AiPermissionMode Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "readonly" or "read-only" or "read_only" or "view" => AiPermissionMode.ReadOnly,
        "full" or "trusted" or "auto" => AiPermissionMode.Full,
        "approve" or "confirm" or null or "" => AiPermissionMode.Approve,
        _ => AiPermissionMode.Approve     // 认不出来就退回最安全的默认值
    };

    public static string ToWire(this AiPermissionMode mode) => mode switch
    {
        AiPermissionMode.ReadOnly => "readonly",
        AiPermissionMode.Full => "full",
        _ => "approve"
    };

    public static void Set(HttpContext? context, AiPermissionMode mode)
    {
        if (context is not null) context.Items[ItemKey] = mode;
    }

    /// <summary>没设置时返回默认的「需确认」—— 漏传参数不会变成无确认执行。</summary>
    public static AiPermissionMode Read(HttpContext? context)
        => context?.Items[ItemKey] as AiPermissionMode? ?? AiPermissionMode.Approve;

    /// <summary>
    /// 告诉模型当前是什么权限。
    ///
    /// 这段必须给模型看：不给的话，它在「完全权限」下仍然会按系统提示词里的说法
    /// 把**已经执行成功**的写操作汇报成「已登记，待人工确认，尚未执行」——
    /// 那就是在说假话，而这个项目存在的意义之一就是防这个。
    /// </summary>
    public static string Describe(AiPermissionMode mode) => mode switch
    {
        AiPermissionMode.ReadOnly =>
            "当前 AI 权限级别：仅可查看(readonly)。所有写操作都会被系统拒绝，不要尝试调用写工具；" +
            "如果用户要求写操作，如实告知需要先把权限级别切到「需确认」或「完全权限」。",

        AiPermissionMode.Full =>
            "当前 AI 权限级别：完全权限(full)。写操作会**立即真正执行**，工具返回的就是实际执行结果。\n" +
            "因此：绝对不要说「已登记」「待人工确认」「尚未执行」，也不要让用户去点什么确认卡片；" +
            "直接按工具返回的真实结果如实汇报（例如新建得到的 ID、是否成功）。",

        _ =>
            "当前 AI 权限级别：需确认(approve)。写操作会被登记为「待人工确认」，" +
            "由用户在确认卡片上点击后才真正执行；在用户确认前不要说已经完成。"
    };
}
