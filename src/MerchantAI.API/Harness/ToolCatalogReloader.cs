using MerchantAdmin.AI.API.Ai.Tools;
using Microsoft.SemanticKernel;

namespace MerchantAdmin.AI.API.Harness;

/// <summary>
/// tools.json 热加载：改完保存即生效，不用重启进程。
/// 这是「加功能只改一处」体验的最后一块 —— 否则每加一条工具还是要重启一次。
///
/// 失败策略：新配置校验不过就保留旧快照并打印错误，改错一个字段不会把正在跑的服务打挂。
/// </summary>
public sealed class ToolCatalogReloader : IHostedService, IDisposable
{
    private const string PluginName = "Store";

    private readonly ToolCatalog _catalog;
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ToolCatalogReloader> _logger;

    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    public ToolCatalogReloader(
        ToolCatalog catalog,
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<ToolCatalogReloader> logger)
    {
        _catalog = catalog;
        _services = services;
        _configuration = configuration;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_configuration.GetValue("Ai:Tools:HotReload", true))
        {
            _logger.LogInformation("tools.json 热加载已关闭（Ai:Tools:HotReload=false）");
            return Task.CompletedTask;
        }

        var directory = Path.GetDirectoryName(_catalog.SourcePath)!;
        var fileName = Path.GetFileName(_catalog.SourcePath);

        _watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _watcher.Changed += Schedule;
        _watcher.Created += Schedule;
        _watcher.Renamed += Schedule;

        _logger.LogInformation("已监听 {File}，保存后自动热加载工具目录", _catalog.SourcePath);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    private void Schedule(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            // 编辑器保存往往触发多次事件，防抖一下
            _debounce?.Dispose();
            _debounce = new Timer(_ => Reload(), null, TimeSpan.FromMilliseconds(600), Timeout.InfiniteTimeSpan);
        }
    }

    private void Reload()
    {
        lock (_gate)
        {
            try
            {
                ReloadCore();
            }
            catch (Exception ex)
            {
                // 关键：校验失败不动旧快照，服务继续用上一份可用配置
                _logger.LogError("tools.json 热加载失败，继续沿用上一份配置：{Message}", ex.Message);
            }
        }
    }

    private void ReloadCore()
    {
        var baseUrl = ToolCatalog.ResolveBaseUrl(_catalog.SourcePath, _configuration["MerchantApi:BaseUrl"]);
        var swaggerUrl = ToolCatalog.ResolveSwaggerUrl(_catalog.SourcePath, baseUrl);
        var cachePath = Path.Combine(Path.GetDirectoryName(_catalog.SourcePath)!, ".swagger-cache.json");

        var openApi = OpenApiToolSource.TryLoad(swaggerUrl, cachePath, message => _logger.LogInformation("[tools.json] {Message}", message));

        _catalog.Reload(
            baseUrl,
            _configuration["MerchantApi:GatewayPrefix"],
            _configuration.GetValue<int?>("Ai:Tools:TimeoutSeconds"),
            openApi,
            message => _logger.LogInformation("[tools.json] {Message}", message));

        // 工具清单变了，Kernel 里的插件也要换掉，否则模型还看着旧工具
        var kernel = _services.GetRequiredService<Kernel>();
        var invoker = _services.GetRequiredService<IToolInvoker>();
        var functions = ToolFunctionFactory.CreateAll(_catalog, invoker);

        if (kernel.Plugins.TryGetPlugin(PluginName, out var existing))
            kernel.Plugins.Remove(existing);

        kernel.Plugins.Add(KernelPluginFactory.CreateFromFunctions(PluginName, functions));

        _logger.LogInformation(
            "工具目录已热加载：{Count} 个工具（{Tools}）",
            functions.Count,
            string.Join(", ", functions.Select(f => $"{f.Name}:{_catalog.Find(f.Name)?.Access}")));
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
        _watcher = null;
        _debounce = null;
    }
}
