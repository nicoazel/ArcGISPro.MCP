using System.Text.Json;

namespace ArcGISProMCP.Core.Operations;

public static partial class JsonSchemas
{
    public static JsonElement EmptyObject { get; } = Parse("""
        { "type": "object", "additionalProperties": false }
        """);

    public static JsonElement ObjectSchema(string propertiesJson, params string[] required)
    {
        var requiredJson = required.Length == 0
            ? "[]"
            : JsonSerializer.Serialize(required);
        return Parse($$"""
            {
              "type": "object",
              "properties": { {{propertiesJson}} },
              "required": {{requiredJson}},
              "additionalProperties": false
            }
            """);
    }

    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
