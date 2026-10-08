using System.Text;
using System.Text.Json;

namespace XamlG.Automation;

/// <summary>Validation for the JSON-schema subset emitted by typed automation contracts.</summary>
public static class AutomationSchema
{
    public const int MaximumArgumentBytes = 8 * 1024 * 1024;
    public static void Validate(JsonElement schema, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) Fail("$", "Expected an argument object.");
        if (Encoding.UTF8.GetByteCount(arguments.GetRawText()) > MaximumArgumentBytes) Fail("$", "Arguments exceed the size limit.");
        Visit(schema, arguments, schema, "$", 0);
    }
    private static void Visit(JsonElement schema, JsonElement value, JsonElement root, string path, int depth)
    {
        if (depth > 64) Fail(path, "Arguments exceed the depth limit.");
        if (schema.ValueKind == JsonValueKind.True) return;
        if (schema.ValueKind == JsonValueKind.False) Fail(path, "Value is not permitted.");
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var pointer = reference.GetString()!;
            if (!pointer.StartsWith('#')) Fail(path, "Only local schema references are supported.");
            var target = root;
            foreach (var segment in pointer[1..].Split('/', StringSplitOptions.RemoveEmptyEntries))
                target = target.GetProperty(segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
            Visit(target, value, root, path, depth + 1); return;
        }
        if (schema.TryGetProperty("type", out var types))
        {
            var names = types.ValueKind == JsonValueKind.Array ? types.EnumerateArray().Select(t => t.GetString()) : [types.GetString()];
            if (!names.Any(name => Matches(name!, value))) Fail(path, "Value has the wrong JSON type.");
        }
        if (schema.TryGetProperty("enum", out var allowed) && !allowed.EnumerateArray().Any(v => JsonElement.DeepEquals(v, value)))
            Fail(path, "Value is not a permitted enum member.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (schema.TryGetProperty("required", out var required))
                foreach (var item in required.EnumerateArray())
                    if (!value.TryGetProperty(item.GetString()!, out _)) Fail(path, "Missing required argument: " + item.GetString());
            schema.TryGetProperty("properties", out var properties);
            foreach (var item in value.EnumerateObject())
            {
                if (!names.Add(item.Name)) Fail(path, "Duplicate property: " + item.Name);
                if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(item.Name, out var property))
                    Visit(property, item.Value, root, path + "." + item.Name, depth + 1);
                else if (schema.TryGetProperty("additionalProperties", out var extra))
                    Visit(extra, item.Value, root, path + "." + item.Name, depth + 1);
            }
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            Bound(schema, "minItems", "maxItems", value.GetArrayLength(), path);
            if (schema.TryGetProperty("items", out var items))
            { var i = 0; foreach (var item in value.EnumerateArray()) Visit(items, item, root, path + "[" + i++ + "]", depth + 1); }
        }
        if (value.ValueKind == JsonValueKind.String) Bound(schema, "minLength", "maxLength", value.GetString()!.Length, path);
        if (value.ValueKind == JsonValueKind.Number) Bound(schema, "minimum", "maximum", value.GetDouble(), path);
    }
    private static bool Matches(string type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object, "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String, "null" => value.ValueKind == JsonValueKind.Null,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var n) && decimal.Truncate(n) == n,
        _ => false
    };
    private static void Bound(JsonElement schema, string min, string max, double value, string path)
    {
        if (schema.TryGetProperty(min, out var minimum) && value < minimum.GetDouble() ||
            schema.TryGetProperty(max, out var maximum) && value > maximum.GetDouble()) Fail(path, "Value is outside the permitted range.");
    }
    private static void Fail(string path, string message) => throw new AutomationException("invalid_arguments", path + ": " + message);
}
