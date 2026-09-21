using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MerchantAdmin.AI.API.Auth;

/// <summary>MerchantApi 配置段。</summary>
public sealed class MerchantApiOptions
{
    public const string SectionName = "MerchantApi";

    public string BaseUrl { get; set; } = string.Empty;
    public string GatewayPrefix { get; set; } = string.Empty;

    /// <summary>静态 token。填了就作为起点，会自动续期（除非没配服务账号）。</summary>
    public string ServiceToken { get; set; } = string.Empty;

    /// <summary>服务账号。配了它才能自动续期 —— 这是长期跑起来不用管的前提。</summary>
    public ServiceAccountOptions? ServiceAccount { get; set; }
}

/// <summary>IdentityApi 配置段（和登录代理共用同一份地址）。</summary>
public sealed class IdentityApiOptions
{
    public const string SectionName = "IdentityApi";

    /// <summary>必须以 / 结尾，登录路径是 base + auth/login 拼出来的。</summary>
    public string BaseUrl { get; set; } = string.Empty;
}

public sealed class ServiceAccountOptions
{
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// 业务接口用的 access token 来源。
///
/// 之前这里只是一个从配置读出来的静态字符串，token 一到期（实测云上只发 2 小时）
    /// 所有工具调用就集体 401，表现是「AI 突然查不到数据」，且没有任何显式提示 ——
    /// 只能靠人记得去重跑一次脚本。现在改成：请求前检查有效期，将过期时优先用
    /// refresh token 换新（后端轮换、一次性），refresh 失效再回退到服务账号密码登录。
    /// </summary>
public interface IServiceTokenProvider
{
    /// <summary>是否具备自动续期条件（配了服务账号 + Identity 地址）。启动日志据此提示。</summary>
    bool AutoRenewEnabled { get; }

    /// <summary>用于续期的服务账号名，仅用于日志。</summary>
    string? ServiceAccountName { get; }

    /// <summary>拿一枚当前可用的 token。自动续期关闭时就是配置里那枚。</summary>
    Task<string> GetTokenAsync(CancellationToken ct = default);

    /// <summary>
    /// 上报一次「带着这枚 token 的请求被拒了」。
    /// 401 是续期机制唯一的真实信号来源 —— 本地时钟和服务器可能对不齐、
    /// token 也可能因为改密码/停用而提前失效，这些情况只看 exp 是发现不了的。
    /// </summary>
    void ReportUnauthorized(string? usedToken);
}

public sealed class ServiceTokenProvider : IServiceTokenProvider
{
    /// <summary>提前量：剩余寿命少于这个值就换新的，避免「刚好在请求途中过期」。</summary>
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(5);

    /// <summary>换 token 失败后的冷却时间，防止一堆并发请求一起把 Identity 打爆。</summary>
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(30);

    private readonly MerchantApiOptions _options;
    private readonly IdentityApiOptions _identity;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ServiceTokenProvider> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _cachedToken;
    private string? _cachedRefreshToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFailureAt = DateTimeOffset.MinValue;
    private bool _loggedNoRenewal;

    public ServiceTokenProvider(
        IOptions<MerchantApiOptions> options,
        IOptions<IdentityApiOptions> identity,
        IHttpClientFactory httpFactory,
        ILogger<ServiceTokenProvider> logger)
    {
        _options = options.Value;
        _identity = identity.Value;
        _httpFactory = httpFactory;
        _logger = logger;

        // 配置里那枚静态 token 作为起点：即使续期没配好，只要它还没过期就能用
        if (!string.IsNullOrWhiteSpace(_options.ServiceToken))
        {
            _cachedToken = _options.ServiceToken.Trim();
            _expiresAt = ReadExpiry(_cachedToken) ?? DateTimeOffset.MinValue;
        }
    }

    public bool AutoRenewEnabled =>
        _options.ServiceAccount is { } account
        && !string.IsNullOrWhiteSpace(account.UserName)
        && !string.IsNullOrWhiteSpace(account.Password)
        && !string.IsNullOrWhiteSpace(_identity.BaseUrl);

    public string? ServiceAccountName => _options.ServiceAccount?.UserName;

    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        if (!AutoRenewEnabled)
        {
            WarnOnceNoRenewal();
            return _cachedToken ?? string.Empty;
        }

        // 快路径：还早得很就直接用，不加锁
        var current = _cachedToken;
        if (!string.IsNullOrEmpty(current) && DateTimeOffset.UtcNow + RenewBefore < _expiresAt)
            return current;

        await _gate.WaitAsync(ct);
        try
        {
            // 拿到锁后重新判断：等锁期间可能已经有别的请求换好了
            current = _cachedToken;
            if (!string.IsNullOrEmpty(current) && DateTimeOffset.UtcNow + RenewBefore < _expiresAt)
                return current;

            if (DateTimeOffset.UtcNow - _lastFailureAt < FailureCooldown)
                return current ?? string.Empty;   // 刚失败过，先沿用旧的，别继续压 Identity

            return await RenewAsync(ct) ?? current ?? string.Empty;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void ReportUnauthorized(string? usedToken)
    {
        if (!AutoRenewEnabled) return;
        if (string.IsNullOrEmpty(usedToken)) return;

        // 只有「被拒的正是当前这枚」才作废缓存。
        // 否则一个迟到的 401 会把刚刚换好的新 token 一起丢掉，引发无谓的换发风暴。
        if (!string.Equals(usedToken, _cachedToken, StringComparison.Ordinal)) return;

        _logger.LogWarning("服务 token 被服务端拒绝（401），作废缓存并在下次请求时重新获取");
        _expiresAt = DateTimeOffset.MinValue;
    }

    private async Task<string?> RenewAsync(CancellationToken ct)
    {
        // 优先用 refresh token 换新：后端把它当一次性（轮换），每换一次就作废旧的一枚，
        // 所以拿到新值后必须替换缓存。refresh 失效（作废/过期/改了密码）时回退到账号密码登录。
        if (!string.IsNullOrEmpty(_cachedRefreshToken))
        {
            var refreshed = await TryRefreshAsync(ct);
            if (!string.IsNullOrEmpty(refreshed)) return refreshed;

            // 这枚 refresh token 已被服务端作废，清掉免得下轮又拿它打一遍。
            _cachedRefreshToken = null;
        }

        return await LoginAsync(ct);
    }

    private async Task<string?> TryRefreshAsync(CancellationToken ct)
    {
        try
        {
            var client = _httpFactory.CreateClient(ServiceTokenHttpClients.Identity);

            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                refreshToken = _cachedRefreshToken
            });
            using var content = new ByteArrayContent(payload);
            content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

            using var response = await client.PostAsync("auth/refresh", content, ct);

            if (!response.IsSuccessStatusCode)
            {
                _lastFailureAt = DateTimeOffset.UtcNow;
                _logger.LogWarning(
                    "refresh token 换新失败（HTTP {Status}），回退到服务账号登录", (int)response.StatusCode);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<TokenPair>(cancellationToken: ct);
            if (string.IsNullOrWhiteSpace(result?.Token) || string.IsNullOrWhiteSpace(result.RefreshToken))
            {
                _lastFailureAt = DateTimeOffset.UtcNow;
                _logger.LogWarning("refresh token 换新成功但响应里没有新的 token 对，回退到服务账号登录");
                return null;
            }

            var token = StoreTokens(result.Token, result.RefreshToken!);
            _logger.LogInformation(
                "已用 refresh token 续期，新 token 有效至 {Expiry:yyyy-MM-dd HH:mm:ss}",
                _expiresAt.ToLocalTime());
            return token;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastFailureAt = DateTimeOffset.UtcNow;
            _logger.LogWarning(ex, "调用刷新接口出错，回退到服务账号登录");
            return null;
        }
    }

    private async Task<string?> LoginAsync(CancellationToken ct)
    {
        var account = _options.ServiceAccount!;
        try
        {
            var client = _httpFactory.CreateClient(ServiceTokenHttpClients.Identity);

            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                userName = account.UserName,
                password = account.Password
            });
            using var content = new ByteArrayContent(payload);
            content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

            using var response = await client.PostAsync("auth/login", content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _lastFailureAt = DateTimeOffset.UtcNow;
                _logger.LogError(
                    "服务账号登录失败（HTTP {Status}），业务接口将无法调用：{Body}",
                    (int)response.StatusCode, Trim(body));
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<TokenPair>(cancellationToken: ct);
            if (string.IsNullOrWhiteSpace(result?.Token) || string.IsNullOrWhiteSpace(result.RefreshToken))
            {
                _lastFailureAt = DateTimeOffset.UtcNow;
                _logger.LogError("服务账号登录成功但响应里没有 token 对，业务接口将无法调用");
                return null;
            }

            var token = StoreTokens(result.Token, result.RefreshToken!);
            _logger.LogInformation(
                "已为服务账号 {User} 换取新的业务接口 token，有效至 {Expiry:yyyy-MM-dd HH:mm:ss}",
                account.UserName, _expiresAt.ToLocalTime());
            return token;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastFailureAt = DateTimeOffset.UtcNow;
            _logger.LogError(ex, "换取业务接口 token 时出错，沿用上一枚");
            return null;
        }
    }

    /// <summary>把换来的新 token 对写入缓存，并重置失败冷却。</summary>
    private string StoreTokens(string token, string refreshToken)
    {
        _cachedToken = token.Trim();
        _cachedRefreshToken = refreshToken.Trim();
        // 读不出 exp 也不能当永久有效：退而求其次按 30 分钟算，下次请求会再换
        _expiresAt = ReadExpiry(_cachedToken) ?? DateTimeOffset.UtcNow.AddMinutes(30);
        _lastFailureAt = DateTimeOffset.MinValue;
        return _cachedToken;
    }

    private void WarnOnceNoRenewal()
    {
        if (_loggedNoRenewal) return;
        _loggedNoRenewal = true;

        if (string.IsNullOrWhiteSpace(_options.ServiceAccount?.UserName))
        {
            _logger.LogWarning(
                "未配置 MerchantApi:ServiceAccount，业务接口 token 不会自动续期；" +
                "过期后所有工具调用会集体 401，需要重跑 refresh-service-token.ps1");
        }
        else
        {
            _logger.LogWarning(
                "MerchantApi:ServiceAccount 已配置，但缺少 IdentityApi:BaseUrl 或账号密码，无法自动续期");
        }
    }

    /// <summary>从 JWT 的 exp 读到期时间。读不出来返回 null（不验签，只当作调度依据）。</summary>
    private static DateTimeOffset? ReadExpiry(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2) return null;

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("exp", out var exp)) return null;
            if (!exp.TryGetInt64(out var seconds)) return null;

            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch
        {
            return null;   // 不是标准 JWT 就交给服务器去判，别在这儿抛
        }
    }

    private static string Trim(string s)
        => s.Length <= 300 ? s : s[..300] + "…";

    private sealed record TokenPair(string? Token, string? RefreshToken);
}

/// <summary>续期用的具名 HttpClient（和登录代理共用同一个 Identity 客户端）。</summary>
public static class ServiceTokenHttpClients
{
    public const string Identity = "identity";
}
