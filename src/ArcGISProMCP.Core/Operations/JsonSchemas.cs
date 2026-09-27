using System.Text.Json;

namespace ArcGISProMCP.Core.Operations;

public static partial class JsonSchemas
{
    public static JsonElement EmptyObject { get; } = Parse("""
        { "type": "object", "additionalProperties": false }
        """);

    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
