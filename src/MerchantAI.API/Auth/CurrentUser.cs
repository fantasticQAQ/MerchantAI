using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace MerchantAdmin.AI.API.Auth;

/// <summary>JWT 配置，与 Identity.API 及各业务服务保持同一套 issuer/key。</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;

    /// <summary>是否在启动时强制要求配置（本机联调可以放宽，生产必须配）。</summary>
    public bool Required { get; set; } = true;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Issuer) && !string.IsNullOrWhiteSpace(Key);
}

/// <summary>
/// 当前登录用户。
///
/// 注意：AI 后端调业务接口时用的是配置里的**服务账号** token，不是调用者本人的 token。
/// 所以「模型能不能做某件事」跟「这个人有没有权限」是两回事 —— 必须靠工具的角色限制来补，
/// 否则任何登录用户都能通过 AI 做到服务账号才有的权限（比如删商品）。
/// </summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    long UserId { get; }
    string UserName { get; }
    IReadOnlyList<string> Roles { get; }
    bool IsAdmin { get; }

    /// <summary>调用者是否被允许使用这个工具。</summary>
    bool IsInAnyRole(IReadOnlyList<string>? allowed);
}

public sealed class HttpContextCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;

    public HttpContextCurrentUser(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;

    public long UserId =>
        long.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;

    public string UserName =>
        Principal?.FindFirstValue(ClaimTypes.Name)
        ?? Principal?.FindFirstValue("unique_name")
        ?? "unknown";

    public IReadOnlyList<string> Roles =>
        Principal?.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList() ?? new List<string>();

    public bool IsAdmin =>
        Roles.Any(r => r.Equals("Admin", StringComparison.OrdinalIgnoreCase)
                    || r.Equals("SuperAdmin", StringComparison.OrdinalIgnoreCase));

    public bool IsInAnyRole(IReadOnlyList<string>? allowed)
    {
        // 没声明 roles 的工具 = 任何登录用户都能用
        if (allowed is null || allowed.Count == 0) return true;
        return allowed.Any(a => Roles.Any(r => r.Equals(a, StringComparison.OrdinalIgnoreCase)));
    }
}

public static class JwtAuthentication
{
    /// <summary>
    /// 只做「签名 + 签发者 + 有效期」校验，参数与业务服务完全一致。
    ///
    /// 刻意不引用 MerchantAdmin.Shared.Authentication：那会把 AI 项目绑到主解决方案上，
    /// 破坏「独立项目、只依赖接口契约」的前提。代价是这里没有业务服务那套
    /// SecurityStamp 校验（改密码/停用后旧 token 立即失效）—— 需要的话可以按接口契约
    /// 去调 Identity 的内部接口补上，见 README。
    /// </summary>
    public static IServiceCollection AddAiJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

        if (!options.IsConfigured)
        {
            if (options.Required)
                throw new InvalidOperationException(
                    "缺少 Jwt:Issuer / Jwt:Key 配置。AI 后端现在要求鉴权，配置值与 Identity.API 保持一致。");

            // 没配又没强制要求：注册一个永远拒绝的认证，接口会返回 401 而不是崩掉
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(_ => { });
            services.AddAuthorization();
            return services;
        }

        services.AddAuthentication(opt =>
        {
            opt.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            opt.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(opt =>
        {
            opt.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = false,     // 与业务服务保持一致
                ValidateLifetime = true,
                ValidIssuer = options.Issuer,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Key))
            };
        });

        services.AddAuthorization();
        return services;
    }
}
