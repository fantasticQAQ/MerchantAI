using System.Text.Json.Nodes;

namespace MerchantAdmin.AI.API.Ai.Tools;

/// <summary>从 swagger 里解析出来的一个操作（路径 / 参数骨架）。</summary>
public sealed class OpenApiOperation
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public string? Summary { get; init; }
    public string? Description { get; init; }
    public string? Tag { get; init; }
    public List<ToolParameter> Parameters { get; init; } = new();
    public List<string> QueryNames { get; init; } = new();
    /// <summary>请求体模板，例如 { "orderItems": "$orderItems" }；null 表示该操作没有请求体。</summary>
    public JsonNode? BodyTemplate { get; init; }

    /// <summary>tools.json 里 "from" 引用的键，形如 "GET /api/Products"。</summary>
    public string Key => $"{Method} {Path}";
}

/// <summary>
/// 从业务服务的 swagger.json 提取「路径 + 参数骨架」，供 tools.json 用 "from" 引用。
/// 注意：这里**只生成骨架，绝不自动注册工具**。哪些接口允许模型调用、叫什么名字、是读还是写，仍然必须由人写进 tools.json。
/// 否则 DELETE /api/Products/{productId} 这类接口会自动变成模型可调用的工具。
/// </summary>
public static class OpenApiToolSource
{
    private static readonly string[] HttpMethods = { "GET", "POST", "PUT", "PATCH", "DELETE" };

    /// <summary>
    /// 拉取 swagger 文档。成功则顺带缓存到磁盘；失败则退回上次的缓存。
    /// 缓存的意义：让「AI 后端先于业务服务启动」这种情况不会导致 AI 服务起不来。
    /// </summary>
    public static IReadOnlyDictionary<string, OpenApiOperation>? TryLoad(
        string swaggerUrl,
        string? cachePath = null,
        Action<string>? log = null)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var json = http.GetStringAsync(swaggerUrl).GetAwaiter().GetResult();
            var operations = Parse(json);
            log?.Invoke($"已从 {swaggerUrl} 读取到 {operations.Count} 个接口。");

            if (cachePath is not null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    File.WriteAllText(cachePath, json);
                }
                catch (Exception ex)
                {
                    log?.Invoke($"swagger 缓存写入失败（不影响运行）：{ex.Message}");
                }
            }

            return operations;
        }
        catch (Exception ex)
        {
            log?.Invoke($"读取 swagger 失败（{swaggerUrl}）：{ex.Message}");

            if (cachePath is not null && File.Exists(cachePath))
            {
                try
                {
                    var operations = Parse(File.ReadAllText(cachePath));
                    log?.Invoke($"已退回使用 swagger 缓存（{cachePath}，{operations.Count} 个接口），可能与当前后端版本不一致。");
                    return operations;
                }
                catch (Exception cacheEx)
                {
                    log?.Invoke($"swagger 缓存也不可用：{cacheEx.Message}");
                }
            }

            return null;
        }
    }

    public static IReadOnlyDictionary<string, OpenApiOperation> Parse(string swaggerJson)
    {
        var document = JsonNode.Parse(swaggerJson) as JsonObject
                       ?? throw new InvalidOperationException("swagger 文档不是合法的 JSON 对象");

        var components = document["components"]?["schemas"] as JsonObject;
        var paths = document["paths"] as JsonObject ?? new JsonObject();
        var result = new Dictionary<string, OpenApiOperation>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, pathItemNode) in paths)
        {
            if (pathItemNode is not JsonObject pathItem) continue;

            foreach (var (method, operationNode) in pathItem)
            {
                var upper = method.ToUpperInvariant();
                if (!HttpMethods.Contains(upper)) continue;
                if (operationNode is not JsonObject operation) continue;

                var descriptor = Build(upper, path, operation, components);
                result[descriptor.Key] = descriptor;
            }
        }

        return result;
    }

    private static OpenApiOperation Build(string method, string path, JsonObject operation, JsonObject? components)
    {
        var parameters = new List<ToolParameter>();
        var queryNames = new List<string>();

        if (operation["parameters"] is JsonArray declared)
        {
            foreach (var node in declared.OfType<JsonObject>())
            {
                var location = ReadString(node, "in");
                if (location is not ("query" or "path")) continue;

                var name = ReadString(node, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;

                var schema = Resolve(node["schema"], components) as JsonObject
                             ?? new JsonObject { ["type"] = "string" };

                parameters.Add(new ToolParameter
                {
                    Name = name,
                    Description = ReadString(node, "description") ?? ReadString(schema, "description"),
                    Required = location == "path" || ReadBool(node, "required") == true,
                    Default = schema["default"]?.DeepClone(),
                    Schema = schema
                });

                if (location == "query") queryNames.Add(name);
            }
        }

        var body = BuildRequestBody(operation, components);
        if (body is not null) parameters.Add(body.Value.Parameter);

        return new OpenApiOperation
        {
            Method = method,
            Path = path,
            Summary = ReadString(operation, "summary"),
            Description = ReadString(operation, "description"),
            Tag = (operation["tags"] as JsonArray)?.FirstOrDefault()?.GetValue<string>(),
            Parameters = parameters,
            QueryNames = queryNames,
            BodyTemplate = body?.Template
        };
    }

    /// <summary>
    /// 请求体的处理约定（确定性规则，避免"魔法"）：
    /// - 恰好只有一个顶层属性时，把它摊平成同名参数（command 风格接口最常见），如 { orderItems: [...] } -> 参数 orderItems
    /// - 否则暴露一个名为 body 的对象参数
    /// </summary>
    private static (ToolParameter Parameter, JsonNode Template)? BuildRequestBody(JsonObject operation, JsonObject? components)
    {
        if (operation["requestBody"] is not JsonObject requestBody) return null;

        var content = requestBody["content"] as JsonObject;
        var media = content?["application/json"] as JsonObject
                    ?? content?["text/json"] as JsonObject
                    ?? content?.FirstOrDefault().Value as JsonObject;

        if (Resolve(media?["schema"], components) is not JsonObject schema) return null;

        var bodyRequired = ReadBool(requestBody, "required") ?? false;

        if (schema["properties"] is JsonObject { Count: 1 } properties)
        {
            var (name, propertySchema) = properties.First();
            var child = propertySchema as JsonObject ?? new JsonObject();
            return (
                new ToolParameter
                {
                    Name = name,
                    Description = ReadString(child, "description"),
                    Required = bodyRequired,
                    Default = child["default"]?.DeepClone(),
                    Schema = child
                },
                new JsonObject { [name] = "$" + name });
        }

        return (
            new ToolParameter
            {
                Name = "body",
                Description = "请求体对象",
                Required = bodyRequired,
                Schema = schema
            },
            JsonValue.Create("$body")!);
    }

    /// <summary>把 OpenAPI 的 $ref / nullable 等专有写法解析成纯 JSON Schema，并防住循环引用。</summary>
    private static JsonNode? Resolve(JsonNode? schema, JsonObject? components, HashSet<string>? visiting = null)
    {
        if (schema is not JsonObject obj) return schema?.DeepClone();

        if (ReadString(obj, "$ref") is { } reference)
        {
            var name = reference.Split('/').Last();
            visiting ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!visiting.Add(name)) return new JsonObject { ["type"] = "object" }; // 循环引用兜底

            var resolved = Resolve(components?[name], components, visiting);
            visiting.Remove(name);
            return resolved;
        }

        var result = new JsonObject();
        foreach (var (key, value) in obj)
        {
            switch (key)
            {
                case "properties" when value is JsonObject properties:
                    var resolvedProperties = new JsonObject();
                    foreach (var (propertyName, propertySchema) in properties)
                        resolvedProperties[propertyName] = Resolve(propertySchema, components, visiting);
                    result["properties"] = resolvedProperties;
                    break;

                case "items":
                    result["items"] = Resolve(value, components, visiting);
                    break;

                case "allOf" when value is JsonArray allOf:
                    MergeAllOf(result, allOf, components, visiting);
                    break;

                // OpenAPI 专有、对模型无意义的关键字，丢掉以保持 schema 干净
                case "nullable" or "example" or "xml" or "externalDocs" or "discriminator" or "deprecated":
                    break;

                default:
                    result[key] = value?.DeepClone();
                    break;
            }
        }

        return result;
    }

    private static void MergeAllOf(JsonObject target, JsonArray allOf, JsonObject? components, HashSet<string>? visiting)
    {
        var properties = target["properties"] as JsonObject ?? new JsonObject();
        var required = new List<string>();

        foreach (var part in allOf)
        {
            if (Resolve(part, components, visiting) is not JsonObject resolved) continue;

            foreach (var (key, value) in resolved)
            {
                if (key == "properties" && value is JsonObject childProperties)
                {
                    foreach (var (propertyName, propertySchema) in childProperties)
                        properties[propertyName] = propertySchema?.DeepClone();
                }
                else if (key == "required" && value is JsonArray childRequired)
                {
                    required.AddRange(childRequired.Select(x => x?.GetValue<string>()).OfType<string>());
                }
                else if (!target.ContainsKey(key))
                {
                    target[key] = value?.DeepClone();
                }
            }
        }

        if (properties.Count > 0) target["properties"] = properties;
        if (required.Count > 0) target["required"] = new JsonArray(required.Distinct().Select(r => (JsonNode)r).ToArray());
    }

    private static string? ReadString(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool? ReadBool(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
}
