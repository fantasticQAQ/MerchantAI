using System.Net;
using System.Net.Http.Headers;
using MerchantAdmin.AI.API.Auth;

namespace MerchantAdmin.AI.API.Ai.Tools;

/// <summary>
/// 给业务接口的每个请求挂上当前可用的 token，并在被拒时通知续期器。
///
/// 为什么必须放在 Handler 里、而不是注册 HttpClient 时设一次 Authorization 头：
/// HttpClient 是长生命周期的，注册时设的头会一直用同一枚 token，
/// 自动续期换来的新 token 根本挂不上去。
/// </summary>
public sealed class ServiceTokenHandler : DelegatingHandler
{
    private readonly IServiceTokenProvider _tokens;

    public ServiceTokenHandler(IServiceTokenProvider tokens) => _tokens = tokens;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _tokens.GetTokenAsync(cancellationToken);

        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await base.SendAsync(request, cancellationToken);

        // 401 说明服务器不认这枚 token —— 可能是本地算的到期时间不准，
        // 也可能是账号被停用/改密导致提前失效。让续期器作废缓存，下次请求重换。
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            _tokens.ReportUnauthorized(token);

        return response;
    }
}
