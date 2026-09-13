using MerchantAdmin.AI.API.Ai.Tools;
using Microsoft.AspNetCore.Mvc;

namespace MerchantAdmin.AI.API.Controllers;

/// <summary>
/// 开发期工具目录自省：看清模型当前能调哪些工具、参数长什么样，以及后端还有哪些接口没暴露。
/// 仅在 Development 环境可用。
/// </summary>
[ApiController]
[Route("api/ai/tools")]
public class ToolsController : ControllerBase
{
    private readonly IToolCatalog _catalog;
    private readonly IWebHostEnvironment _environment;

    public ToolsController(IToolCatalog catalog, IWebHostEnvironment environment)
    {
        _catalog = catalog;
        _environment = environment;
    }

    [HttpGet]
    public IActionResult Get()
    {
        if (!_environment.IsDevelopment()) return NotFound();

        return Ok(new
        {
            source = _catalog.SourcePath,
            baseUrl = _catalog.BaseUrl,
            timeoutSeconds = _catalog.TimeoutSeconds,
            tools = _catalog.Tools.Select(t => new
            {
                t.Name,
                access = t.Access.ToString().ToLowerInvariant(),
                exposedToModel = t.IsExposedToModel,
                requiresApproval = t.RequiresApproval,
                request = $"{t.Http.Method} {t.Http.Path}",
                t.Description,
                parameters = t.Parameters.Select(p => new
                {
                    p.Name,
                    p.Required,
                    p.Description,
                    schema = p.Schema.ToJsonString()
                })
            }),
            notExposedOperations = _catalog.UnexposedOperations.Select(o => new
            {
                operation = o.Key,
                o.Summary,
                o.Tag
            })
        });
    }
}
