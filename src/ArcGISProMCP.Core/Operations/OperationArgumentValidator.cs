using System.Globalization;
using System.Text.Json;

namespace ArcGISProMCP.Core.Operations;

/// <summary>
/// Validates the bounded JSON Schema subset used by operation descriptors.
/// Supported keywords are object properties/required/additionalProperties,
/// primitive types, enum, string minLength, numeric minimum/maximum and their
/// exclusive counterparts, plus array items. This is deliberately not a full
/// JSON Schema implementation.
/// </summary>
public static class OperationArgumentValidator
{
    public static IReadOnlyList<OperationArgumentIssue> Validate(JsonElement arguments, JsonElement schema)
    {
        var issues = new List<OperationArgumentIssue>();
        ValidateValue(arguments, schema, "$", issues);
        return issues;
    }

    private static void ValidateValue(JsonElement value, JsonElement schema, string path, List<OperationArgumentIssue> issues)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new(path, "Input schema must be a JSON object."));
            return;
        }

        if (schema.TryGetProperty("enum", out var enumValues) && enumValues.ValueKind == JsonValueKind.Array &&
            !enumValues.EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, value)))
        {
            issues.Add(new(path, "Value must match one of the declared enum values."));
        }

        if (schema.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String)
        {
            var type = typeElement.GetString();
            if (!MatchesType(value, type))
            {
                issues.Add(new(path, $"Expected type '{type}', got '{ValueType(value)}'."));
                return;
            }
        }

        if (value.ValueKind == JsonValueKind.Object)
            ValidateObject(value, schema, path, issues);
        else if (value.ValueKind == JsonValueKind.String)
            ValidateString(value, schema, path, issues);
        else if (value.ValueKind is JsonValueKind.Number)
            ValidateNumber(value, schema, path, issues);
        else if (value.ValueKind == JsonValueKind.Array)
            ValidateArray(value, schema, path, issues);
    }

    private static void ValidateObject(JsonElement value, JsonElement schema, string path, List<OperationArgumentIssue> issues)
    {
        var properties = schema.TryGetProperty("properties", out var propertiesElement) && propertiesElement.ValueKind == JsonValueKind.Object
            ? propertiesElement
            : default;

        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in required.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var name = item.GetString()!;
                if (!value.TryGetProperty(name, out _))
                    issues.Add(new(PropertyPath(path, name), "Required property is missing."));
            }
        }

        var rejectAdditional = schema.TryGetProperty("additionalProperties", out var additional) &&
            additional.ValueKind == JsonValueKind.False;
        foreach (var property in value.EnumerateObject())
        {
            if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var propertySchema))
            {
                ValidateValue(property.Value, propertySchema, PropertyPath(path, property.Name), issues);
            }
            else if (rejectAdditional)
            {
                issues.Add(new(PropertyPath(path, property.Name), "Additional properties are not allowed."));
            }
        }
    }

    private static void ValidateString(JsonElement value, JsonElement schema, string path, List<OperationArgumentIssue> issues)
    {
        if (schema.TryGetProperty("minLength", out var minimum) && minimum.TryGetInt32(out var minLength) &&
            value.GetString()!.EnumerateRunes().Count() < minLength)
        {
            issues.Add(new(path, $"String length must be at least {minLength}."));
        }
    }

    private static void ValidateNumber(JsonElement value, JsonElement schema, string path, List<OperationArgumentIssue> issues)
    {
        if (!value.TryGetDecimal(out var number)) return;
        CheckBound("minimum", number, false, static (actual, bound) => actual < bound, schema, path, issues);
        CheckBound("maximum", number, false, static (actual, bound) => actual > bound, schema, path, issues);
        CheckBound("exclusiveMinimum", number, true, static (actual, bound) => actual <= bound, schema, path, issues);
        CheckBound("exclusiveMaximum", number, true, static (actual, bound) => actual >= bound, schema, path, issues);
    }

    private static void CheckBound(string keyword, decimal number, bool exclusive, Func<decimal, decimal, bool> invalid,
        JsonElement schema, string path, List<OperationArgumentIssue> issues)
    {
        if (!schema.TryGetProperty(keyword, out var boundElement) || !boundElement.TryGetDecimal(out var bound)) return;
        if (invalid(number, bound))
            issues.Add(new(path, $"Number must be {(exclusive ? "greater than" : keyword == "minimum" ? "at least" : "at most")} {bound.ToString(CultureInfo.InvariantCulture)}."));
    }

    private static void ValidateArray(JsonElement value, JsonElement schema, string path, List<OperationArgumentIssue> issues)
    {
        if (!schema.TryGetProperty("items", out var itemSchema)) return;
        var index = 0;
        foreach (var item in value.EnumerateArray())
            ValidateValue(item, itemSchema, $"{path}[{index++}]", issues);
    }

    private static bool MatchesType(JsonElement value, string? type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true
    };

    private static string ValueType(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Number => "number",
        JsonValueKind.String => "string",
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.Null => "null",
        _ => value.ValueKind.ToString().ToLowerInvariant()
    };

    private static string PropertyPath(string parent, string property) =>
        property.All(character => char.IsLetterOrDigit(character) || character == '_')
            ? $"{parent}.{property}"
            : $"{parent}[{JsonSerializer.Serialize(property)}]";
}

public sealed record OperationArgumentIssue(string Path, string Message);
