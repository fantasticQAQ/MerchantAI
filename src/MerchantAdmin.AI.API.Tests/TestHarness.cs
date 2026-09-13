using System.Security.Claims;
using System.Text.Json;
using MerchantAdmin.AI.API.Ai.Tools;
using MerchantAdmin.AI.API.Auth;
using MerchantAdmin.AI.API.Harness;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;

namespace MerchantAdmin.AI.API.Tests;

/// <summary>测试里用的固定身份。要验证隔离时会用第二个身份。</summary>
internal static class TestUser
{
    public const long Id = 2;
    public const string Name = "tester";

    public const long OtherId = 9;
    public const string OtherName = "someone-else";
}

/// <summary>
/// 把 Harness 按 Program.cs 的方式装起来（假业务服务 + 真 Catalog/执行器/审批/护栏 + 真 CurrentUser），
/// 这样测试覆盖的是真实链路，而不是各自 mock 出来的假象。
/// 登录身份通过 <see cref="SetUser"/> 换，走的是和线上一样的 ClaimsPrincipal → ICurrentUser 提取路径。
/// </summary>
public sealed class TestHarness : IDisposable
{
    public const long DefaultUserId = 2;

    public FakeMerchantApi Merchant { get; }
    public ToolCatalog Catalog { get; }
    public HttpToolInvoker Invoker { get; }
    public InMemoryPendingActionStore Pending { get; } = new();
    public PendingSummaryRegistry Summaries { get; }
    public HumanApprovalService Approvals { get; }
    public AgentTraceService Trace { get; }
    public DefaultHttpContext HttpContext { get; } = new();
    public HttpContextAccessor Accessor { get; }
    public ICurrentUser User { get; private set; } = null!;
    public AgentLoopOptions LoopOptions { get; }
    public Kernel Kernel { get; private set; }

    public TestHarness(
        AgentLoopOptions? loopOptions = null,
        string? toolsPath = null,
        IReadOnlyDictionary<string, OpenApiOperation>? openApi = null,
        bool useRealToolsJson = true)
    {
        Merchant = new FakeMerchantApi();
        LoopOptions = loopOptions ?? new AgentLoopOptions
        {
            MaxToolCallsPerTurn = 200,
            MaxApprovalsPerTurn = 200,
            TurnTimeoutSeconds = 30
        };

        HttpContext.Items["SessionId"] = "test-session";
        Accessor = new HttpContextAccessor { HttpContext = HttpContext };
        SetUser(DefaultUserId, "tester");

        Catalog = useRealToolsJson
            ? ToolCatalog.Load(toolsPath ?? LocateRealToolsJson(), Merchant.BaseUrl, null, openApi ?? SwaggerFixture.Operations)
            : ToolCatalog.Load(toolsPath!, Merchant.BaseUrl, null, openApi);

        Invoker = new HttpToolInvoker(new SingleBaseAddressClientFactory(new Uri(Merchant.BaseUrl)), Catalog, NullLogger<HttpToolInvoker>.Instance);
        // 和 Program.cs 用同一处发现逻辑，避免「线上注册了、测试没注册」两边对不上
        Summaries = new PendingSummaryRegistry(PendingSummaryRegistry.DiscoverFormatterTypes()
            .Select(t => (IPendingSummaryFormatter)Activator.CreateInstance(t)!));
        Approvals = new HumanApprovalService(
            Pending,
            Catalog,
            Invoker,
            Summaries,
            Trace = new AgentTraceService(
                Path.Combine(Path.GetTempPath(), "merchant-ai-tests", Guid.NewGuid().ToString("N")),
                NullLogger<AgentTraceService>.Instance),
            NullLogger<HumanApprovalService>.Instance);

        Kernel = BuildKernel();
    }

    public long UserId => User.UserId;

    /// <summary>设置本次请求的 AI 权限级别（和前端下拉框走同一条路径）。</summary>
    public void SetMode(AiPermissionMode mode) => AgentPermission.Set(HttpContext, mode);

    /// <summary>切换当前登录者（含角色），用于验证隔离与角色限制。</summary>
    public void SetUser(long userId, string userName, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, userName)
        };
        foreach (var role in roles.Length == 0 ? new[] { "Admin" } : roles)
            claims.Add(new Claim(ClaimTypes.Role, role));

        HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        User = new HttpContextCurrentUser(Accessor);
    }

    private Kernel BuildKernel()
    {
        var guard = new StoreGuardFilter(
            Catalog,
            Approvals,
            Trace,
            Accessor,
            User,
            LoopOptions,
            NullLogger<StoreGuardFilter>.Instance);

        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IFunctionInvocationFilter>(guard);

        var kernel = builder.Build();
        kernel.Plugins.AddFromFunctions("Store", ToolFunctionFactory.CreateAll(Catalog, Invoker));
        return kernel;
    }

    public KernelArguments Args(string json)
        => new(JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!);

    public async Task<string> CallAsync(string function, string json)
        => (await Kernel.InvokeAsync("Store", function, Args(json))).ToString();

    public IReadOnlyList<string> PendingIds
        => HttpContext.Items["PendingActionIds"] as List<string> ?? new List<string>();

    public void ResetTurn()
    {
        HttpContext.Items.Remove(AgentLoopState.ToolCallCountKey);
        HttpContext.Items.Remove(AgentLoopState.ApprovalCountKey);
        HttpContext.Items["PendingActionIds"] = new List<string>();
    }

    /// <summary>测试直接引用 API 项目里的真实 tools.json —— 配置写错了测试就会红，而不是等上线才发现。</summary>
    public static string LocateRealToolsJson()
    {
        var fromOutput = Path.Combine(AppContext.BaseDirectory, "tools.json");
        if (File.Exists(fromOutput)) return fromOutput;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "MerchantAdmin.AI.API", "tools.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException("找不到 tools.json");
    }

    public void Dispose() => Merchant.Dispose();

    private sealed class SingleBaseAddressClientFactory : IHttpClientFactory
    {
        private readonly Uri _baseAddress;
        public SingleBaseAddressClientFactory(Uri baseAddress) => _baseAddress = baseAddress;
        public HttpClient CreateClient(string name)
            => new() { BaseAddress = _baseAddress, Timeout = Timeout.InfiniteTimeSpan };
    }
}
