using System.Net.Http.Headers;
using MerchantAdmin.AI.API.Ai.Tools;
using MerchantAdmin.AI.API.Auth;
using MerchantAdmin.AI.API.Controllers;
using MerchantAdmin.AI.API.Harness;
using MerchantAdmin.AI.API.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

// CORS
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                     ?? new[] { "http://localhost:5174" };
services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod());
});

services.AddControllers();
services.AddEndpointsApiExplorer();
services.AddSwaggerGen();
services.AddHttpContextAccessor();

// ===== 工具目录：tools.json 是「有哪些工具、怎么分级、怎么落到 HTTP」的唯一事实来源 =====
var toolsPath = builder.Configuration["Ai:Tools:Path"];
toolsPath = string.IsNullOrWhiteSpace(toolsPath)
    ? Path.Combine(builder.Environment.ContentRootPath, "tools.json")
    : Path.GetFullPath(toolsPath, builder.Environment.ContentRootPath);

void LogTools(string message) => Console.WriteLine($"[tools.json] {message}");

var catalogBaseUrl = ToolCatalog.ResolveBaseUrl(toolsPath, builder.Configuration["MerchantApi:BaseUrl"]);

// swagger 只用来给 tools.json 里的 "from" 提供路径/参数骨架，绝不自动注册工具。
// 取不到不算致命：除非确实有工具引用了它（那样 Load 会明确报错）。
var swaggerUrl = ToolCatalog.ResolveSwaggerUrl(toolsPath, catalogBaseUrl);
var swaggerCachePath = Path.Combine(Path.GetDirectoryName(toolsPath)!, ".swagger-cache.json");
LogTools($"swagger 地址：{swaggerUrl}");
var openApi = OpenApiToolSource.TryLoad(swaggerUrl, swaggerCachePath, LogTools);

var catalog = ToolCatalog.Load(
    toolsPath,
    baseUrlOverride: catalogBaseUrl,
    timeoutOverride: builder.Configuration.GetValue<int?>("Ai:Tools:TimeoutSeconds"),
    openApi: openApi,
    log: LogTools,
    // 经网关访问时业务路径要挪到网关前缀下（本地直连留空即可）
    pathPrefixOverride: builder.Configuration["MerchantApi:GatewayPrefix"]);

// 注册具体类型，热加载需要拿到可变实例；IToolCatalog 仍指向同一个对象，
// 因此所有消费方（过滤器/审批/执行器/MCP）会自动看到热加载后的新工具。
services.AddSingleton(catalog);
services.AddSingleton<IToolCatalog>(sp => sp.GetRequiredService<ToolCatalog>());

// ===== 业务 API（HTTP，只依赖接口契约，不引用任何业务 DLL） =====
// token 不再是一枚写死在配置里的字符串：ServiceTokenProvider 会在将过期时
// 用服务账号自动换新的，并把新 token 通过 Handler 挂到每个请求上。
services.Configure<MerchantApiOptions>(builder.Configuration.GetSection(MerchantApiOptions.SectionName));
services.Configure<IdentityApiOptions>(builder.Configuration.GetSection(IdentityApiOptions.SectionName));
services.AddSingleton<IServiceTokenProvider, ServiceTokenProvider>();

services.AddTransient<ServiceTokenHandler>();
services.AddHttpClient(HttpToolInvoker.HttpClientName, client =>
{
    // 每次创建 client 时重新读，这样 tools.json 热加载改了 baseUrl 也能生效
    client.BaseAddress = new Uri(catalog.BaseUrl);
    client.Timeout = Timeout.InfiniteTimeSpan; // 超时由 HttpToolInvoker 按 tools.json 控制
}).AddHttpMessageHandler<ServiceTokenHandler>();

// ===== 会话历史 / 待确认操作：配了 Redis 且连得上就用 Redis，否则退回内存 =====
// 降级是刻意的：本地没起 Redis 时不该连服务都跑不起来，但启动日志必须说清楚当前用的哪种。
var redisConnection = new RedisConnection(builder.Configuration, NullLogger<RedisConnection>.Instance);
var redisReady = redisConnection.TryPing();
services.AddSingleton(redisConnection);

if (redisReady)
{
    services.AddSingleton<IConversationStore, RedisConversationStore>();
    services.AddSingleton<IPendingActionStore, RedisPendingActionStore>();
    // 「界面上不显示」的名单。和会话/轨迹一样永久保留 —— 它自己过期了，删掉的东西会自己冒出来。
    services.AddSingleton<IVisibilityStore, RedisVisibilityStore>();
    // 会话的标题/置顶/分组。同样是用户设置，不能被内容重写冲掉，所以单独存。
    services.AddSingleton<ISessionPrefsStore, RedisSessionPrefsStore>();
    LogTools($"会话与待确认操作使用 Redis：{redisConnection.ConnectionString}");

    // 会话默认永久保留（Ai:History:TtlMinutes=0），待确认操作仍是短 TTL
    var historyTtl = builder.Configuration.GetValue("Ai:History:TtlMinutes", 0);
    LogTools(historyTtl > 0
        ? $"会话历史保留 {historyTtl} 分钟（Ai:History:TtlMinutes）"
        : "会话历史永久保留（Ai:History:TtlMinutes=0，不设过期时间）");
}
else
{
    services.AddSingleton<IConversationStore, InMemoryConversationStore>();
    services.AddSingleton<IPendingActionStore, InMemoryPendingActionStore>();
    services.AddSingleton<IVisibilityStore, InMemoryVisibilityStore>();
    services.AddSingleton<ISessionPrefsStore, InMemorySessionPrefsStore>();
    LogTools(redisConnection.IsEnabled
        ? "Redis 配置了但连不上，会话与待确认操作退回内存存储（重启即丢、多实例不共享）"
        : "未配置 Ai:Redis:ConnectionString，会话与待确认操作使用内存存储（重启即丢、多实例不共享）");
}

// ===== 鉴权：与 Identity.API 同一套 issuer/key =====
services.AddAiJwtAuthentication(builder.Configuration);
services.AddSingleton<ICurrentUser, HttpContextCurrentUser>();

// 登录代理用的 HttpClient：浏览器直连 Identity 会被它的 CORS 挡掉，由 AI 后端服务端转发。
// BaseAddress 必须以 / 结尾，相对路径才不会把最后一段路径顶掉：
//   http://host/api/identity/ + auth/login  ->  /api/identity/auth/login（网关）
//   http://localhost:5001/api/ + auth/login ->  /api/auth/login（本地直连）
var identityBaseUrl = builder.Configuration["IdentityApi:BaseUrl"] ?? "http://localhost:5001/api/";
services.AddHttpClient(ServiceTokenHttpClients.Identity, client =>
{
    client.BaseAddress = new Uri(identityBaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(15);
});

// ===== Harness 服务 =====
services.AddSingleton(AgentLoopOptions.FromConfiguration(builder.Configuration));
services.AddSingleton<AgentTraceService>();

// 审计轨迹也落 Redis（永久保留）；Redis 不可用时只写本地 JSONL
var traceToRedis = builder.Configuration.GetValue("Ai:Trace:Redis", true);
var traceToFile = builder.Configuration.GetValue("Ai:Trace:File", true);
if (traceToRedis && redisReady)
{
    LogTools("审计轨迹写入 Redis（merchant-ai:trace:*，不设过期时间，永久保留）" +
             (traceToFile ? " + 本地 JSONL 文件" : string.Empty));
}
else if (traceToFile)
{
    LogTools(traceToRedis
        ? "审计轨迹只写本地 JSONL 文件（Redis 不可用）"
        : "审计轨迹只写本地 JSONL 文件（Ai:Trace:Redis=false）");
}
else
{
    LogTools("审计轨迹已关闭（Ai:Trace:Redis=false 且 Ai:Trace:File=false）—— 不会有任何落盘");
}

// 摘要格式化器自动注册：新增一个 IPendingSummaryFormatter 实现即可，不用记得回来改这里
foreach (var formatterType in PendingSummaryRegistry.DiscoverFormatterTypes())
    services.AddSingleton(typeof(IPendingSummaryFormatter), formatterType);

services.AddSingleton<PendingSummaryRegistry>();
services.AddSingleton<IToolInvoker, HttpToolInvoker>();
services.AddSingleton<HumanApprovalService>();
services.AddSingleton<StoreGuardFilter>();
services.AddSingleton<StoreAgent>();
// 用模型把对话内容概括成会话标题（替代「第一条用户消息」）
services.AddSingleton<SessionTitleService>();

// tools.json 热加载（改完保存即生效，不用重启）
services.AddHostedService<ToolCatalogReloader>();

// ===== 同一个工具目录再开一个 MCP 入口（Streamable HTTP，/mcp）=====
services.AddStoreMcpServer(builder.Configuration);

// ===== Semantic Kernel + DeepSeek：工具全部由 tools.json 动态生成，这里没有任何业务端点代码 =====
services.AddSingleton<Kernel>(sp =>
{
    var modelId = builder.Configuration["DeepSeek:ModelId"] ?? "deepseek-chat";
    var apiKey = builder.Configuration["DeepSeek:ApiKey"] ?? "";
    var endpoint = builder.Configuration["DeepSeek:Endpoint"] ?? "https://api.deepseek.com/v1";

    var kb = Kernel.CreateBuilder();
    kb.Services.AddSingleton<IFunctionInvocationFilter>(sp.GetRequiredService<StoreGuardFilter>());
    kb.AddOpenAIChatCompletion(modelId, new Uri(endpoint), apiKey);

    var kernel = kb.Build();
    var functions = ToolFunctionFactory.CreateAll(catalog, sp.GetRequiredService<IToolInvoker>());
    kernel.Plugins.AddFromFunctions("Store", functions);

    Console.WriteLine($"[tools.json] 已注册 {functions.Count} 个工具：" +
                      string.Join(", ", functions.Select(f => $"{f.Name}({catalog.Find(f.Name)?.Access})")));
    return kernel;
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors("AllowFrontend");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

if (app.Configuration.GetValue("Ai:Mcp:Enabled", true))
{
    // MCP 也要求鉴权：它和聊天入口共用同一套工具和审批，不能成为绕过鉴权的后门
    app.MapMcp(StoreMcpServer.EndpointPattern).RequireAuthorization();
    app.Logger.LogInformation("MCP 端点已开启（需要 Bearer Token）：{Pattern}", StoreMcpServer.EndpointPattern);
}

// 启动时把工具清单打出来，配置错了第一时间能看见
app.Logger.LogInformation("工具目录：{Path}，已注册 {Count} 个工具", catalog.SourcePath, catalog.Exposed.Count);

// 业务接口的 token 是这套系统最容易「静默失效」的地方：过期后表现是 AI 突然查不到数据。
// 所以启动时明确说清楚当前有没有自动续期，而不是等用户撞上 401 才排查。
var tokenProvider = app.Services.GetRequiredService<IServiceTokenProvider>();
if (tokenProvider.AutoRenewEnabled)
{
    app.Logger.LogInformation(
        "业务接口 token 已开启自动续期（服务账号 {User}）", tokenProvider.ServiceAccountName);
}
else
{
    app.Logger.LogWarning(
        "业务接口 token 未开启自动续期，过期后所有工具调用会集体返回 401。" +
        "请配置 MerchantApi:ServiceAccount 的 UserName/Password 与 IdentityApi:BaseUrl");
}

if (catalog.UnexposedOperations.Count > 0)
{
    app.Logger.LogInformation("以下 {Count} 个后端接口尚未暴露给模型（需要在 tools.json 里显式声明 access）：{Operations}",
        catalog.UnexposedOperations.Count,
        string.Join(" | ", catalog.UnexposedOperations.Select(o => o.Key)));
}

app.Run();
