using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArcGISProMCP.Core.Workflows;

public static class WorkflowBinder
{
    public static IReadOnlyDictionary<string, JsonElement> BindParameters(
        WorkflowDefinition workflow,
        JsonElement supplied)
    {
        if (supplied.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Workflow parameters must be a JSON object.", nameof(supplied));

        var definitions = workflow.Parameters.ToDictionary(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var property in supplied.EnumerateObject())
            if (!definitions.ContainsKey(property.Name))
                throw new ArgumentException($"Unknown workflow parameter '{property.Name}'.", nameof(supplied));

        var bound = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in workflow.Parameters)
        {
            JsonElement value;
            if (supplied.TryGetProperty(parameter.Name, out var suppliedValue))
                value = suppliedValue.Clone();
            else if (parameter.DefaultValue is { } defaultValue)
                value = defaultValue.Clone();
            else if (parameter.Required)
                throw new ArgumentException($"Workflow parameter '{parameter.Name}' is required.", nameof(supplied));
            else
                continue;

            if (!MatchesType(value, parameter.Type))
                throw new ArgumentException($"Workflow parameter '{parameter.Name}' must be {parameter.Type}.", nameof(supplied));
            bound.Add(parameter.Name, value);
        }

        return bound;
    }

    public static JsonElement ResolveArguments(
        JsonElement template,
        IReadOnlyDictionary<string, JsonElement> parameters)
    {
        var node = JsonNode.Parse(template.GetRawText())
            ?? throw new ArgumentException("Workflow step arguments cannot be null.", nameof(template));
        return JsonSerializer.SerializeToElement(ResolveNode(node, parameters));
    }

    private static JsonNode? ResolveNode(JsonNode? node, IReadOnlyDictionary<string, JsonElement> parameters)
    {
        if (node is null) return null;

        if (node is JsonValue value && value.TryGetValue<string>(out var text) && TryPlaceholder(text, out var name))
        {
            if (!parameters.TryGetValue(name, out var parameter))
                throw new ArgumentException($"Workflow parameter '{name}' was not bound.", nameof(parameters));
            return JsonNode.Parse(parameter.GetRawText());
        }

        if (node is JsonObject jsonObject)
        {
            var resolved = new JsonObject();
            foreach (var property in jsonObject)
                resolved[property.Key] = ResolveNode(property.Value, parameters);
            return resolved;
        }

        if (node is JsonArray jsonArray)
        {
            var resolved = new JsonArray();
            foreach (var item in jsonArray)
                resolved.Add(ResolveNode(item, parameters));
            return resolved;
        }

        return node.DeepClone();
    }

    private static bool TryPlaceholder(string value, out string name)
    {
        const string prefix = "${parameters.";
        if (value.StartsWith(prefix, StringComparison.Ordinal) && value.EndsWith('}'))
        {
            name = value[prefix.Length..^1];
            return name.Length > 0;
        }

        name = string.Empty;
        return false;
    }

    private static bool MatchesType(JsonElement value, string declaredType) => declaredType.ToLowerInvariant() switch
    {
        "string" or "path" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "boolean" or "bool" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "any" => true,
        _ => false
    };
}
