using System.Text.Json;
using Xunit;

namespace ArcGISProMCP.Server.Tests.Harness;

/// <summary>
/// Minimal JSON Schema checker for the keywords the SDK emits in tool output schemas: boolean
/// schemas, type (string or array), properties, required, items and enum. Any other keyword that
/// would constrain values fails the assertion, so a schema change cannot silently pass unchecked.
/// </summary>
public static class SchemaAssert
{
    private static readonly HashSet<string> Annotations = new(StringComparer.Ordinal)
    {
        "$schema", "title", "description", "default", "format"
    };

    public static void Valid(JsonElement schema, JsonElement value)
    {
        var errors = new List<string>();
        Check(schema, value, "$", errors);
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors) + Environment.NewLine + value.GetRawText());
    }

    private static void Check(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        if (schema.ValueKind == JsonValueKind.True) return;
        if (schema.ValueKind == JsonValueKind.False)
        {
            errors.Add($"{path}: schema 'false' accepts nothing.");
            return;
        }

        foreach (var keyword in schema.EnumerateObject())
        {
            if (keyword.Name is not ("type" or "properties" or "required" or "items" or "enum") && !Annotations.Contains(keyword.Name))
                errors.Add($"{path}: unsupported schema keyword '{keyword.Name}'.");
        }

        if (schema.TryGetProperty("type", out var type))
        {
            var allowed = type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Select(item => item.GetString()!).ToArray()
                : [type.GetString()!];
            if (!allowed.Any(name => Matches(name, value)))
            {
                errors.Add($"{path}: expected {string.Join("|", allowed)} but found {value.ValueKind}.");
                return;
            }
        }

        if (schema.TryGetProperty("enum", out var values) &&
            !values.EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, value)))
            errors.Add($"{path}: value is not one of the enumerated values.");

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
            {
                foreach (var name in required.EnumerateArray().Select(item => item.GetString()!))
                {
                    if (!value.TryGetProperty(name, out _))
                        errors.Add($"{path}: missing required property '{name}'.");
                }
            }
            if (schema.TryGetProperty("properties", out var properties))
            {
                foreach (var property in properties.EnumerateObject())
                {
                    if (value.TryGetProperty(property.Name, out var member))
                        Check(property.Value, member, $"{path}.{property.Name}", errors);
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
                Check(items, item, $"{path}[{index++}]", errors);
        }
    }

    private static bool Matches(string type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        _ => false
    };
}
