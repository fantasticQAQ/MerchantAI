using Microsoft.SemanticKernel;

namespace MerchantAdmin.AI.API.Ai.Tools;

/// <summary>
/// 把 tools.json 里的一条声明变成一个模型可调用的 KernelFunction。
/// 注意：参数描述必须写在 Schema 里面才会被 OpenAI 连接器发给模型（KernelParameterMetadata.Description 会被忽略），
/// 所以 ToolCatalog 在加载时已经把 description/default 内联进 Schema 了。
/// </summary>
public static class ToolFunctionFactory
{
    public static KernelFunction Create(ToolDescriptor descriptor, IToolInvoker invoker)
    {
        // 所有工具共用同一个委托形态（只接原始 KernelArguments），因此可以对任意参数集合生成函数。
        async Task<string> Invoke(KernelArguments arguments, CancellationToken ct)
        {
            var plan = invoker.BuildPlan(descriptor, arguments);
            if (!plan.Ok) return plan.Error!;
            var result = await invoker.SendAsync(descriptor, plan.Plan!, ct);
            return result.Text;
        }

        var parameters = descriptor.Parameters.Select(p => new KernelParameterMetadata(p.Name)
        {
            Description = p.Description,
            IsRequired = p.Required,
            ParameterType = typeof(object),
            Schema = KernelJsonSchema.Parse(p.Schema.ToJsonString())
        });

        return KernelFunctionFactory.CreateFromMethod(Invoke, new KernelFunctionFromMethodOptions
        {
            FunctionName = descriptor.Name,
            Description = descriptor.Description,
            Parameters = parameters,
            ReturnParameter = new KernelReturnParameterMetadata
            {
                Description = "业务接口返回的 JSON 数据，或一条可读的错误说明"
            }
        });
    }

    public static IReadOnlyList<KernelFunction> CreateAll(IToolCatalog catalog, IToolInvoker invoker)
        => catalog.Exposed.Select(d => Create(d, invoker)).ToList();
}
