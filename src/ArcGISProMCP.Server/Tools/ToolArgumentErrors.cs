using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArcGISProMCP.Server.Tools;

/// <summary>
/// Turns argument binding failures into the standard error envelope. The SDK binds tools/call
/// arguments to method parameters before a tool runs; a missing required argument or a value of
/// the wrong JSON type throws there, and the SDK would answer with a generic isError text and no
/// structuredContent although every tool advertises an outputSchema. This filter answers instead
/// with <c>invalid_arguments</c>, naming the argument from the tool's inputSchema and never
/// echoing the exception. An unknown tool is not matched and stays a JSON-RPC error.
/// </summary>
internal static class ToolArgumentErrors
{
    public const string Code = "invalid_arguments";

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            try
            {
                return await next(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is ArgumentException or JsonException &&
                context.MatchedPrimitive is McpServerTool tool)
            {
                return ToolResults.Failure<object>(new ToolError(
                    Code,
                    Describe(tool.ProtocolTool, context.Params?.Arguments),
                    Retryable: false,
                    Revision: null));
            }
        };

    /// <summary>Names the first argument that does not match the tool's inputSchema.</summary>
    internal static string Describe(Tool tool, IDictionary<string, JsonElement>? arguments)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var schema = tool.InputSchema;
        var fallback = $"The arguments do not match the inputSchema of '{tool.Name}'.";
        if (schema.ValueKind != JsonValueKind.Object) return fallback;

        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in required.EnumerateArray().Select(item => item.GetString()).OfType<string>())
            {
                if (arguments is null || !arguments.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Undefined)
                    return $"Missing required argument '{name}'.";
            }
        }

        if (arguments is not null && schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (!arguments.TryGetValue(property.Name, out var value)) continue;
                var types = AllowedTypes(property.Value);
                if (types.Count > 0 && !types.Any(type => Matches(type, value)))
                    return $"Argument '{property.Name}' must be {string.Join(" or ", types)}, not {Describe(value)}.";
            }
        }

        return fallback;
    }

    private static List<string> AllowedTypes(JsonElement propertySchema)
    {
        if (propertySchema.ValueKind != JsonValueKind.Object || !propertySchema.TryGetProperty("type", out var type)) return [];
        return type.ValueKind switch
        {
            JsonValueKind.String => [type.GetString()!],
            JsonValueKind.Array => [.. type.EnumerateArray().Select(item => item.GetString()).OfType<string>()],
            _ => []
        };
    }

    private static bool Matches(string type, JsonElement value) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "null" => value.ValueKind == JsonValueKind.Null,
        // An unknown type keyword is not this filter's to judge.
        _ => true
    };

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.Null => "null",
        _ => "missing"
    };
}
