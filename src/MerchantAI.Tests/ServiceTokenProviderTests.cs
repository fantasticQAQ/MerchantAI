using System.Net;
using System.Text;
using System.Text.Json;
using MerchantAdmin.AI.API.Auth;
using MerchantAdmin.AI.API.Ai.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MerchantAdmin.AI.API.Tests;

/// <summary>
/// 自动续期：token 将过期时用服务账号换新的，而不是等它过期后所有工具集体 401。
/// 这里打的是真实的 ServiceTokenProvider + ServiceTokenHandler，只把 Identity 换成假的。
/// </summary>
public class ServiceTokenProviderTests
{
    private const string Account = "fantastic";
    private const string Password = "123456";

    [Fact]
    public async Task 没配置服务账号时不会去换token_直接用配置里那枚()
    {
        using var identity = new FakeIdentityApi();
        var provider = Build(identity, staticToken: SampleToken(TimeSpan.FromHours(1)), configureAccount: false);

        var token = await provider.GetTokenAsync();

        Assert.Equal(SampleToken(TimeSpan.FromHours(1)), token);
        Assert.Equal(0, identity.LoginCount);   // 没配账号就绝不该打登录接口
    }

    [Fact]
    public async Task token将过期时自动用服务账号换取新的()
    {
        using var identity = new FakeIdentityApi();
        var stale = SampleToken(TimeSpan.FromMinutes(1));   // 只剩 1 分钟，小于 5 分钟提前量

        var provider = Build(identity, staticToken: stale);
        var token = await provider.GetTokenAsync();

        Assert.Equal(1, identity.LoginCount);
        Assert.NotEqual(stale, token);
        Assert.Equal(identity.IssuedToken, token);
    }

    [Fact]
    public async Task token还早得很就不换_避免每次都打登录接口()
    {
        using var identity = new FakeIdentityApi();
        var fresh = SampleToken(TimeSpan.FromHours(2));

        var provider = Build(identity, staticToken: fresh);
        var first = await provider.GetTokenAsync();
        var second = await provider.GetTokenAsync();

        Assert.Equal(fresh, first);
        Assert.Equal(fresh, second);
        Assert.Equal(0, identity.LoginCount);   // 只要没过期就不该登录
    }

    [Fact]
    public async Task 并发请求只会换一次token()
    {
        using var identity = new FakeIdentityApi { LoginDelay = TimeSpan.FromMilliseconds(80) };
        var provider = Build(identity, staticToken: SampleToken(TimeSpan.FromMinutes(1)));

        // 20 个并发请求同时发现 token 要过期 —— 不能各自去登录一次把 Identity 打爆
        var tokens = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => provider.GetTokenAsync()));

        Assert.Equal(1, identity.LoginCount);
        Assert.All(tokens, t => Assert.Equal(identity.IssuedToken, t));
    }

    [Fact]
    public async Task 换token失败时沿用旧的_并且不会疯狂重试()
    {
        using var identity = new FakeIdentityApi { LoginStatusCode = HttpStatusCode.Unauthorized };
        var stale = SampleToken(TimeSpan.FromMinutes(1));

        var provider = Build(identity, staticToken: stale);

        // 第一次：尝试续期但失败，退回旧 token（它虽然快过期，但比没有强）
        Assert.Equal(stale, await provider.GetTokenAsync());
        Assert.Equal(1, identity.LoginCount);

        // 冷却期内的连续请求不应该继续打 Identity
        await provider.GetTokenAsync();
        await provider.GetTokenAsync();
        Assert.Equal(1, identity.LoginCount);
    }

    [Fact]
    public async Task 服务端返回401时作废缓存_下次请求重换()
    {
        using var identity = new FakeIdentityApi();
        var stale = SampleToken(TimeSpan.FromMinutes(1));

        var provider = Build(identity, staticToken: stale);
        var first = await provider.GetTokenAsync();
        Assert.Equal(1, identity.LoginCount);

        // 本地算出来还没到期，但服务端不认（改密码/停用/时钟偏差都会这样）
        provider.ReportUnauthorized(first);

        var second = await provider.GetTokenAsync();
        Assert.Equal(2, identity.LoginCount);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task 迟到的401不会把刚换好的新token丢掉()
    {
        using var identity = new FakeIdentityApi();
        var stale = SampleToken(TimeSpan.FromMinutes(1));

        var provider = Build(identity, staticToken: stale);
        var oldToken = stale;
        var newToken = await provider.GetTokenAsync();   // 换成新的
        Assert.Equal(1, identity.LoginCount);

        // 一个用旧 token 发出的慢请求现在才带着 401 回来
        provider.ReportUnauthorized(oldToken);

        // 新 token 必须还在，不能被这个迟到的 401 连坐作废
        Assert.Equal(newToken, await provider.GetTokenAsync());
        Assert.Equal(1, identity.LoginCount);
    }

    [Fact]
    public async Task Handler会给请求挂上Bearer头()
    {
        using var identity = new FakeIdentityApi();
        var expected = SampleToken(TimeSpan.FromHours(1));
        var provider = Build(identity, staticToken: expected);

        var stub = new StubHandler(HttpStatusCode.OK);
        using var invoker = new HttpClient(new ServiceTokenHandler(provider) { InnerHandler = stub })
        {
            BaseAddress = new Uri("http://localhost")
        };

        await invoker.GetAsync("/api/Products");

        Assert.Equal($"Bearer {expected}", Assert.Single(stub.Authorizations));
        Assert.Equal(0, identity.LoginCount);   // token 还有效，不该白白登录一次
    }

    [Fact]
    public async Task Handler遇到401会通知续期器作废缓存()
    {
        using var identity = new FakeIdentityApi();
        var provider = Build(identity, staticToken: SampleToken(TimeSpan.FromMinutes(1)));

        var stub = new StubHandler(HttpStatusCode.Unauthorized);
        using var invoker = new HttpClient(new ServiceTokenHandler(provider) { InnerHandler = stub })
        {
            BaseAddress = new Uri("http://localhost")
        };

        await invoker.GetAsync("/api/Products");   // stale 快过期 -> 换一次 -> 被 401 拒
        Assert.Equal(1, identity.LoginCount);

        await invoker.GetAsync("/api/Products");   // 已作废 -> 必须再换一次，而不是继续用被拒的那枚

        Assert.Equal(2, identity.LoginCount);
    }

    /// <summary>
    /// 直接校验真实的 appsettings.Development.json。
    ///
    /// 这条是为了防住一个已经踩过的坑：Identity 地址写在 IdentityApi 段、
    /// 服务账号写在 MerchantApi 段，如果 Options 类的键名和配置对不上，
    /// 自动续期会**静默失效** —— 单测手动构造 options 是发现不了的，
    /// 只能靠「配置真能绑出可用的续期条件」来兜。
    /// </summary>
    [Fact]
    public void 真实配置能绑出启用状态的自动续期()
    {
        var path = LocateApiSettings("appsettings.Development.json");
        var config = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false)
            .Build();

        var merchant = config.GetSection(MerchantApiOptions.SectionName).Get<MerchantApiOptions>();
        var identity = config.GetSection(IdentityApiOptions.SectionName).Get<IdentityApiOptions>();

        Assert.NotNull(merchant?.ServiceAccount);
        Assert.False(string.IsNullOrWhiteSpace(merchant!.ServiceAccount!.UserName),
            "MerchantApi:ServiceAccount:UserName 没绑上，检查 appsettings 的键名");
        Assert.False(string.IsNullOrWhiteSpace(identity?.BaseUrl),
            "IdentityApi:BaseUrl 没绑上，检查 appsettings 的键名");
    }

    private static string LocateApiSettings(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "MerchantAI.API", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"找不到 {fileName}");
    }

    // ---------------------------------------------------------------- 辅助

    /// <summary>签一枚只有 exp 有意义的假 JWT。Provider 不验签，只读 exp。</summary>
    private static string SampleToken(TimeSpan lifetime)
    {
        var exp = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds();
        var header = B64("{\"alg\":\"HS256\",\"typ\":\"JWT\"}");
        var payload = B64($"{{\"sub\":\"5\",\"exp\":{exp}}}");
        return $"{header}.{payload}.sig";
    }

    private static string B64(string s)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(s))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static ServiceTokenProvider Build(
        FakeIdentityApi identity, string? staticToken = null, bool configureAccount = true)
    {
        var merchant = new MerchantApiOptions
        {
            ServiceToken = staticToken ?? string.Empty,
            ServiceAccount = configureAccount
                ? new ServiceAccountOptions { UserName = Account, Password = Password }
                : null
        };
        var identityOptions = new IdentityApiOptions { BaseUrl = identity.BaseUrl + "/" };

        var factory = new SingleBaseAddressFactory(identity.BaseUrl + "/");
        return new ServiceTokenProvider(
            Options.Create(merchant),
            Options.Create(identityOptions),
            factory,
            NullLogger<ServiceTokenProvider>.Instance);
    }

    /// <summary>
    /// 只做「记下请求 + 返回指定状态码」的替身。
    /// Handler 测试不该为此起一个真实 HTTP 服务器 —— 那既慢又引入了无关的故障面。
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public StubHandler(HttpStatusCode status) => _status = status;

        public List<string?> Authorizations { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class SingleBaseAddressFactory : IHttpClientFactory
    {
        private readonly Uri _baseAddress;
        public SingleBaseAddressFactory(string baseUrl) => _baseAddress = new Uri(baseUrl);
        public HttpClient CreateClient(string name)
            => new() { BaseAddress = _baseAddress, Timeout = TimeSpan.FromSeconds(30) };
    }
}

/// <summary>假 Identity：/auth/login 返回一枚带指定寿命的 token。</summary>
public sealed class FakeIdentityApi : IDisposable
{
    private readonly HttpListener _listener;

    public FakeIdentityApi()
    {
        for (var port = 19160; port < 19240; port++)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { listener.Start(); }
            catch { continue; }

            _listener = listener;
            BaseUrl = $"http://127.0.0.1:{port}";
            _ = Task.Run(LoopAsync);
            return;
        }
        throw new InvalidOperationException("找不到空闲端口启动假 Identity");
    }

    public string BaseUrl { get; }
    public int LoginCount { get; private set; }
    public string IssuedToken { get; private set; } = string.Empty;

    public HttpStatusCode LoginStatusCode { get; set; } = HttpStatusCode.OK;
    public TimeSpan LoginDelay { get; set; } = TimeSpan.Zero;
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(2);

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; }

            _ = Task.Run(async () =>
            {
                try
                {
                    LoginCount++;
                    if (LoginDelay > TimeSpan.Zero) await Task.Delay(LoginDelay);

                    if (LoginStatusCode != HttpStatusCode.OK)
                    {
                        ctx.Response.StatusCode = (int)LoginStatusCode;
                        ctx.Response.Close();
                        return;
                    }

                    IssuedToken = MakeToken(TokenLifetime);
                    var json = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        token = IssuedToken,
                        userName = "fantastic",
                        roles = new[] { "Admin" }
                    });

                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    ctx.Response.ContentLength64 = json.Length;
                    await ctx.Response.OutputStream.WriteAsync(json);
                    ctx.Response.Close();
                }
                catch { /* 测试收尾时监听器被关掉，忽略 */ }
            });
        }
    }

    private static string MakeToken(TimeSpan lifetime)
    {
        var exp = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds();
        // 带上 jti：exp 只精确到秒，同一秒内两次登录会签出完全一样的字符串，
        // 让人误以为「没换 token」。真实 JWT 也有 jti/iat，这里照着来。
        var jti = Guid.NewGuid().ToString("N");
        string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("{\"alg\":\"HS256\",\"typ\":\"JWT\"}")}.{B64($"{{\"sub\":\"5\",\"exp\":{exp},\"jti\":\"{jti}\"}}")}.sig";
    }

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
