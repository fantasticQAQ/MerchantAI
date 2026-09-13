using System.Text.Json;
using System.Text.Json.Nodes;

namespace MerchantAdmin.AI.API.Ai.Tools;

/// <summary>
/// 极简 JSON Schema 校验器（只覆盖 tools.json 会用到的关键字）。
/// 存在的意义：模型传参跑偏时（空数组、缺字段、枚举值乱写）**必须在生成待确认卡片之前就拦下来**，
/// 而不是登记一张空卡片、等用户点了确认才报错。
/// 校验失败的信息会原样回给模型，让它自己纠正重试。
/// </summary>
internal static class JsonSchemaLite
{
    public static List<string> Validate(JsonNode? schema, JsonNode? value, string path)
    {
        var errors = new List<string>();
        Walk(schema as JsonObject, value, path, errors);
        return errors;
    }

    private static void Walk(JsonObject? schema, JsonNode? value, string path, List<string> errors)
    {
        if (schema is null) return;

        var type = ReadString(schema, "type");

        if (value is null)
        {
            if (type is not null && type != "null")
                errors.Add($"{path} 不能为空（应为 {type}）");
            return;
        }

        if (type is not null && !MatchesType(type, value))
        {
            errors.Add($"{path} 类型应为 {type}，实际收到{Describe(value)}（值：{Snippet(value)}）");
            return;
        }

        switch (value)
        {
            case JsonObject obj:
                if (ReadInt(schema, "minProperties") is { } minProps && obj.Count < minProps)
                    errors.Add($"{path} 至少需要指定 {minProps} 个字段，实际只给了 {obj.Count} 个");
                if (schema["required"] is JsonArray required)
                {
                    foreach (var r in required)
                    {
                        var name = r is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                        if (name is null) continue;
                        if (!obj.TryGetPropertyValue(name, out var child) || child is null)
                            errors.Add($"{path}.{name} 是必填项，缺失");
                    }
                }
                if (schema["properties"] is JsonObject props)
                {
                    foreach (var (name, childSchema) in props)
                        if (obj.TryGetPropertyValue(name, out var child))
                            Walk(childSchema as JsonObject, child, $"{path}.{name}", errors);
                }
                break;

            case JsonArray arr:
                if (ReadInt(schema, "minItems") is { } min && arr.Count < min)
                    errors.Add($"{path} 至少需要 {min} 项，实际只有 {arr.Count} 项");
                if (ReadInt(schema, "maxItems") is { } max && arr.Count > max)
                    errors.Add($"{path} 最多允许 {max} 项，实际有 {arr.Count} 项");
                if (schema["items"] is JsonObject itemSchema)
                    for (var i = 0; i < arr.Count; i++)
                        Walk(itemSchema, arr[i], $"{path}[{i}]", errors);
                break;

            case JsonValue val when schema["enum"] is JsonArray en:
                var text = val.ToJsonString();
                if (!en.Any(e => e?.ToJsonString() == text))
                    errors.Add($"{path} 的取值 {text} 不在允许范围内（允许：{string.Join(", ", en.Select(e => e?.ToJsonString()))}）");
                break;

            case JsonValue num when TryReadDecimal(num, out var number):
                if (ReadDecimal(schema, "minimum") is { } minimum && number < minimum)
                    errors.Add($"{path} 不能小于 {minimum}，实际是 {number}");
                if (ReadDecimal(schema, "maximum") is { } maximum && number > maximum)
                    errors.Add($"{path} 不能大于 {maximum}，实际是 {number}");
                if (ReadDecimal(schema, "exclusiveMinimum") is { } exclusiveMin && number <= exclusiveMin)
                    errors.Add($"{path} 必须大于 {exclusiveMin}，实际是 {number}");
                break;
        }
    }

    private static bool MatchesType(string type, JsonNode value) => type switch
    {
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        "string" => value is JsonValue sv && sv.TryGetValue<string>(out _),
        "boolean" => value is JsonValue bv && bv.TryGetValue<bool>(out _),
        "integer" => value is JsonValue iv && IsInteger(iv),
        "number" => value is JsonValue nv && IsNumber(nv),
        "null" => false,
        _ => true
    };

    private static bool IsInteger(JsonValue v)
    {
        if (v.TryGetValue<long>(out _)) return true;
        if (v.TryGetValue<int>(out _)) return true;
        if (v.TryGetValue<double>(out var d)) return Math.Abs(d % 1) < double.Epsilon;
        return false;
    }

    private static bool IsNumber(JsonValue v)
        => v.TryGetValue<decimal>(out _) || v.TryGetValue<double>(out _) || v.TryGetValue<long>(out _);

    private static string Describe(JsonNode v) => v switch
    {
        JsonObject => "对象",
        JsonArray => "数组",
        JsonValue jv when jv.TryGetValue<string>(out _) => "字符串",
        JsonValue jv when jv.TryGetValue<bool>(out _) => "布尔值",
        JsonValue => "数字",
        _ => v.GetType().Name
    };

    /// <summary>把实际收到的值截断后附在错误里，模型能据此自我纠正，人也一眼看出问题。</summary>
    private static string Snippet(JsonNode v)
    {
        var text = v.ToJsonString();
        return text.Length <= 160 ? text : text[..160] + "…";
    }

    private static string? ReadString(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? ReadInt(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return (int)l;
        if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var p)) return p;
        return null;
    }

    private static decimal? ReadDecimal(JsonObject o, string key)
        => o[key] is JsonValue v && TryReadDecimal(v, out var value) ? value : null;

    private static bool TryReadDecimal(JsonValue value, out decimal result)
    {
        if (value.TryGetValue<decimal>(out result)) return true;
        if (value.TryGetValue<double>(out var d)) { result = (decimal)d; return true; }
        if (value.TryGetValue<long>(out var l)) { result = l; return true; }
        if (value.TryGetValue<int>(out var i)) { result = i; return true; }
        if (value.TryGetValue<string>(out var s)
            && decimal.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out result))
            return true;

        result = 0;
        return false;
    }
}
