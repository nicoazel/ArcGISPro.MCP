using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArcGISProMCP.Core.Operations;

/// <summary>
/// Validates the deliberately bounded JSON Schema subset used by operation
/// descriptors. Supported keywords are object properties/required/
/// additionalProperties/minProperties/maxProperties, primitive types, enum,
/// string minLength/maxLength/pattern, numeric minimum/maximum and numeric
/// exclusive bounds, and array items/minItems/maxItems/uniqueItems. This is not
/// a full JSON Schema implementation; unsupported or malformed keywords are
/// reported as schema issues and never silently accepted.
/// </summary>
public static class OperationArgumentValidator
{
    private const int MaxDepth = 64;
    private const int MaxIssues = 100;
    private const int MaxObjectProperties = 10_000;
    private const int MaxArrayItems = 10_000;
    private const int MaxEnumValues = 1_024;
    private const int MaxStringRunes = 1_000_000;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    private static readonly HashSet<string> SupportedKeywords = new(StringComparer.Ordinal)
    {
        "$schema", "title", "description", "default", "type", "enum", "properties", "required",
        "additionalProperties", "minProperties", "maxProperties", "minLength", "maxLength", "pattern",
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "items", "minItems", "maxItems", "uniqueItems"
    };

    public static IReadOnlyList<OperationArgumentIssue> Validate(JsonElement arguments, JsonElement schema)
    {
        var issues = new List<OperationArgumentIssue>();
        var schemaNodes = 0;
        ValidateSchemaShape(schema, "$", 0, ref schemaNodes, issues);
        if (issues.Count == 0)
            ValidateValue(arguments, schema, "$", 0, issues);
        return issues;
    }

    private static void ValidateSchemaShape(JsonElement schema, string path, int depth, ref int schemaNodes, List<OperationArgumentIssue> issues)
    {
        if (issues.Count >= MaxIssues) return;
        if (++schemaNodes > MaxObjectProperties)
        {
            AddIssue(issues, path, "Schema is too large to validate safely.");
            return;
        }
        if (depth > MaxDepth)
        {
            AddIssue(issues, path, "Schema nesting exceeds the supported depth.");
            return;
        }
        if (schema.ValueKind != JsonValueKind.Object)
        {
            AddIssue(issues, path, "Input schema must be a JSON object.");
            return;
        }

        var schemaKeywords = new HashSet<string>(StringComparer.Ordinal);
        foreach (var keyword in schema.EnumerateObject())
        {
            if (!schemaKeywords.Add(keyword.Name))
                AddIssue(issues, path, $"Schema contains duplicate keyword '{keyword.Name}'.");
            else if (!SupportedKeywords.Contains(keyword.Name))
                AddIssue(issues, path, $"Unsupported schema keyword '{keyword.Name}'.");
        }

        if (schema.TryGetProperty("type", out var type))
        {
            if (type.ValueKind != JsonValueKind.String || !IsKnownType(type.GetString()))
                AddIssue(issues, path, "Schema type must be one supported string type.");
        }
        ValidateEnumSchema(schema, path, issues);
        ValidateIntegerKeyword(schema, "minLength", path, issues);
        ValidateIntegerKeyword(schema, "maxLength", path, issues);
        ValidateIntegerKeyword(schema, "minItems", path, issues);
        ValidateIntegerKeyword(schema, "maxItems", path, issues);
        ValidateIntegerKeyword(schema, "minProperties", path, issues);
        ValidateIntegerKeyword(schema, "maxProperties", path, issues);
        ValidateNumberKeyword(schema, "minimum", path, issues);
        ValidateNumberKeyword(schema, "maximum", path, issues);
        ValidateNumberKeyword(schema, "exclusiveMinimum", path, issues);
        ValidateNumberKeyword(schema, "exclusiveMaximum", path, issues);

        if (schema.TryGetProperty("pattern", out var pattern))
        {
            if (pattern.ValueKind != JsonValueKind.String)
                AddIssue(issues, path, "Schema pattern must be a string.");
            else
            {
                try { _ = new Regex(pattern.GetString()!, RegexOptions.CultureInvariant, RegexTimeout); }
                catch (ArgumentException) { AddIssue(issues, path, "Schema pattern is not a valid regular expression."); }
            }
        }

        if (schema.TryGetProperty("properties", out var properties))
        {
            if (properties.ValueKind != JsonValueKind.Object)
                AddIssue(issues, Join(path, "properties"), "Schema properties must be an object.");
            else
            {
                var propertyNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in properties.EnumerateObject())
                {
                    if (!propertyNames.Add(property.Name))
                        AddIssue(issues, PropertyPath(path, property.Name), "Schema contains duplicate property definition.");
                    ValidateSchemaShape(property.Value, PropertyPath(path, property.Name), depth + 1, ref schemaNodes, issues);
                }
            }
        }
        if (schema.TryGetProperty("required", out var required))
        {
            if (required.ValueKind != JsonValueKind.Array)
                AddIssue(issues, Join(path, "required"), "Schema required must be an array of strings.");
            else
            {
                if (required.GetArrayLength() > MaxArrayItems)
                    AddIssue(issues, Join(path, "required"), "Schema required array is too large to validate safely.");
                var requiredCount = 0;
                foreach (var name in required.EnumerateArray())
                {
                    if (++requiredCount > MaxArrayItems)
                        break;
                    if (name.ValueKind != JsonValueKind.String)
                        AddIssue(issues, Join(path, "required"), "Schema required entries must be strings.");
                }
            }
        }
        if (schema.TryGetProperty("additionalProperties", out var additional) &&
            additional.ValueKind != JsonValueKind.True && additional.ValueKind != JsonValueKind.False)
            AddIssue(issues, Join(path, "additionalProperties"), "Only boolean additionalProperties is supported.");
        if (schema.TryGetProperty("uniqueItems", out var unique) && unique.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            AddIssue(issues, Join(path, "uniqueItems"), "Schema uniqueItems must be a boolean.");
        if (schema.TryGetProperty("items", out var items))
            ValidateSchemaShape(items, Join(path, "items"), depth + 1, ref schemaNodes, issues);
    }

    private static void ValidateValue(JsonElement value, JsonElement schema, string path, int depth, List<OperationArgumentIssue> issues)
    {
        if (issues.Count >= MaxIssues) return;
        if (depth > MaxDepth)
        {
            AddIssue(issues, path, "Argument nesting exceeds the supported depth.");
            return;
        }
        if (schema.TryGetProperty("enum", out var enumValues) &&
            !enumValues.EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, value)))
            AddIssue(issues, path, "Value must match one of the declared enum values.");

        if (schema.TryGetProperty("type", out var typeElement))
        {
            var type = typeElement.GetString();
            if (!MatchesType(value, type))
            {
                AddIssue(issues, path, $"Expected type '{type}', got '{ValueType(value)}'.");
                return;
            }
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object: ValidateObject(value, schema, path, depth, issues); break;
            case JsonValueKind.String: ValidateString(value, schema, path, issues); break;
            case JsonValueKind.Number: ValidateNumber(value, schema, path, issues); break;
            case JsonValueKind.Array: ValidateArray(value, schema, path, depth, issues); break;
        }
    }

    private static void ValidateObject(JsonElement value, JsonElement schema, string path, int depth, List<OperationArgumentIssue> issues)
    {
        var properties = schema.TryGetProperty("properties", out var propertySchema) && propertySchema.ValueKind == JsonValueKind.Object ? propertySchema : default;
        var count = value.EnumerateObject().Count();
        CheckCollectionBound(schema, "minProperties", count, path, issues, "properties");
        CheckCollectionBound(schema, "maxProperties", count, path, issues, "properties");
        if (count > MaxObjectProperties) { AddIssue(issues, path, "Object has too many properties to validate safely."); return; }

        if (schema.TryGetProperty("required", out var required))
            foreach (var item in required.EnumerateArray())
            {
                var name = item.GetString()!;
                if (!value.TryGetProperty(name, out _)) AddIssue(issues, PropertyPath(path, name), "Required property is missing.");
            }

        var rejectAdditional = schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name))
                AddIssue(issues, PropertyPath(path, property.Name), "Duplicate object property is not allowed.");
            if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var child))
                ValidateValue(property.Value, child, PropertyPath(path, property.Name), depth + 1, issues);
            else if (rejectAdditional)
                AddIssue(issues, PropertyPath(path, property.Name), "Additional properties are not allowed.");
            else
                ValidateUnconstrainedValue(property.Value, PropertyPath(path, property.Name), depth + 1, issues);
        }
    }

    private static void ValidateString(JsonElement value, JsonElement schema, string path, List<OperationArgumentIssue> issues)
    {
        var text = value.GetString()!;
        var length = CountRunesUpTo(text, MaxStringRunes + 1);
        if (length > MaxStringRunes) { AddIssue(issues, path, "String is too long to validate safely."); return; }
        CheckCollectionBound(schema, "minLength", length, path, issues, "length");
        CheckCollectionBound(schema, "maxLength", length, path, issues, "length");
        if (schema.TryGetProperty("pattern", out var pattern))
        {
            try
            {
                if (!Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant, RegexTimeout))
                    AddIssue(issues, path, "String does not match the required pattern.");
            }
            catch (RegexMatchTimeoutException) { AddIssue(issues, path, "Pattern validation timed out safely."); }
        }
    }

    private static void ValidateNumber(JsonElement value, JsonElement schema, string path, List<OperationArgumentIssue> issues)
    {
        if (!value.TryGetDouble(out var number)) return;
        CheckNumberBound(schema, "minimum", number, path, issues, inclusive: true, minimum: true);
        CheckNumberBound(schema, "maximum", number, path, issues, inclusive: true, minimum: false);
        CheckNumberBound(schema, "exclusiveMinimum", number, path, issues, inclusive: false, minimum: true);
        CheckNumberBound(schema, "exclusiveMaximum", number, path, issues, inclusive: false, minimum: false);
    }

    private static void ValidateArray(JsonElement value, JsonElement schema, string path, int depth, List<OperationArgumentIssue> issues)
    {
        var count = value.GetArrayLength();
        CheckCollectionBound(schema, "minItems", count, path, issues, "items");
        CheckCollectionBound(schema, "maxItems", count, path, issues, "items");
        var unique = schema.TryGetProperty("uniqueItems", out var uniqueElement) && uniqueElement.ValueKind == JsonValueKind.True;
        var seen = unique ? new HashSet<string>(StringComparer.Ordinal) : null;
        if (count > MaxArrayItems) { AddIssue(issues, path, "Array has too many items to validate safely."); return; }
        if (!schema.TryGetProperty("items", out var itemSchema))
        {
            var unconstrainedIndex = 0;
            foreach (var item in value.EnumerateArray())
                ValidateUnconstrainedValue(item, $"{path}[{unconstrainedIndex++}]", depth + 1, issues);
            return;
        }
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (seen is not null && !seen.Add(item.GetRawText())) AddIssue(issues, $"{path}[{index}]", "Array items must be unique.");
            ValidateValue(item, itemSchema, $"{path}[{index++}]", depth + 1, issues);
        }
    }

    private static void ValidateUnconstrainedValue(JsonElement value, string path, int depth, List<OperationArgumentIssue> issues)
    {
        if (issues.Count >= MaxIssues) return;
        if (depth > MaxDepth)
        {
            AddIssue(issues, path, "Argument nesting exceeds the supported depth.");
            return;
        }
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                var propertyCount = 0;
                foreach (var property in value.EnumerateObject())
                {
                    if (++propertyCount > MaxObjectProperties)
                    {
                        AddIssue(issues, path, "Object has too many properties to validate safely.");
                        return;
                    }
                    if (!names.Add(property.Name))
                        AddIssue(issues, PropertyPath(path, property.Name), "Duplicate object property is not allowed.");
                    ValidateUnconstrainedValue(property.Value, PropertyPath(path, property.Name), depth + 1, issues);
                }
                break;
            case JsonValueKind.Array:
                if (value.GetArrayLength() > MaxArrayItems)
                {
                    AddIssue(issues, path, "Array has too many items to validate safely.");
                    return;
                }
                var index = 0;
                foreach (var item in value.EnumerateArray())
                    ValidateUnconstrainedValue(item, $"{path}[{index++}]", depth + 1, issues);
                break;
            case JsonValueKind.String:
                if (CountRunesUpTo(value.GetString()!, MaxStringRunes + 1) > MaxStringRunes)
                    AddIssue(issues, path, "String is too long to validate safely.");
                break;
        }
    }
    private static void CheckCollectionBound(JsonElement schema, string keyword, int actual, string path, List<OperationArgumentIssue> issues, string label)
    {
        if (!schema.TryGetProperty(keyword, out var bound) || !bound.TryGetInt32(out var expected)) return;
        var invalid = keyword.StartsWith("min", StringComparison.Ordinal) ? actual < expected : actual > expected;
        if (invalid) AddIssue(issues, path, $"Number of {label} must be {(keyword.StartsWith("min", StringComparison.Ordinal) ? "at least" : "at most")} {expected}.");
    }

    private static void CheckNumberBound(JsonElement schema, string keyword, double number, string path, List<OperationArgumentIssue> issues, bool inclusive, bool minimum)
    {
        if (!schema.TryGetProperty(keyword, out var element) || !element.TryGetDouble(out var bound)) return;
        var invalid = minimum ? (inclusive ? number < bound : number <= bound) : (inclusive ? number > bound : number >= bound);
        if (invalid) AddIssue(issues, path, $"Number must be {(minimum ? (inclusive ? "at least" : "greater than") : (inclusive ? "at most" : "less than"))} {bound.ToString(CultureInfo.InvariantCulture)}.");
    }

    private static void ValidateEnumSchema(JsonElement schema, string path, List<OperationArgumentIssue> issues)
    {
        if (!schema.TryGetProperty("enum", out var values)) return;
        if (values.ValueKind != JsonValueKind.Array) { AddIssue(issues, Join(path, "enum"), "Schema enum must be an array."); return; }
        if (values.GetArrayLength() > MaxEnumValues) AddIssue(issues, Join(path, "enum"), "Schema enum has too many values.");
    }

    private static void ValidateIntegerKeyword(JsonElement schema, string keyword, string path, List<OperationArgumentIssue> issues)
    {
        if (schema.TryGetProperty(keyword, out var value) && (!value.TryGetInt32(out var number) || number < 0))
            AddIssue(issues, Join(path, keyword), $"Schema {keyword} must be a non-negative integer.");
    }

    private static void ValidateNumberKeyword(JsonElement schema, string keyword, string path, List<OperationArgumentIssue> issues)
    {
        if (schema.TryGetProperty(keyword, out var value) && !value.TryGetDouble(out _))
            AddIssue(issues, Join(path, keyword), $"Schema {keyword} must be a number.");
    }

    private static int CountRunesUpTo(string value, int limit)
    {
        var count = 0;
        foreach (var _ in value.EnumerateRunes()) if (++count >= limit) break;
        return count;
    }

    private static bool IsKnownType(string? type) => type is "object" or "array" or "string" or "number" or "integer" or "boolean" or "null";
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
        JsonValueKind.True or JsonValueKind.False => "boolean", JsonValueKind.Number => "number", JsonValueKind.String => "string",
        JsonValueKind.Object => "object", JsonValueKind.Array => "array", JsonValueKind.Null => "null", _ => value.ValueKind.ToString().ToLowerInvariant()
    };
    private static string Join(string parent, string child) => $"{parent}.{child}";
    private static string PropertyPath(string parent, string property) => property.All(c => char.IsLetterOrDigit(c) || c == '_') ? $"{parent}.{property}" : $"{parent}[{JsonSerializer.Serialize(property)}]";
    private static void AddIssue(List<OperationArgumentIssue> issues, string path, string message) { if (issues.Count < MaxIssues) issues.Add(new(path, message)); }
}

public sealed record OperationArgumentIssue(string Path, string Message);
