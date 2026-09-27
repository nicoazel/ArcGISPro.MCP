using System.Text.Json;

namespace ArcGISProMCP.Core.Operations;

public static partial class JsonSchemas
{
    public static JsonElement EmptyObject { get; } = Parse("""
        { "type": "object", "additionalProperties": false }
        """);

    /// <summary>
    /// Legacy string-based object schema. Add-in operations now declare schemas with the typed
    /// builder (<see cref="Object"/> and friends, see AddIn/Operations/Schemas); the remaining
    /// production caller is gp.run in GeoprocessingOperations.cs, owned by the geoprocessing
    /// work unit. Remove this method (and its JsonSchemasTests coverage) once gp.run migrates.
    /// It is deliberately not [Obsolete] because warnings are errors.
    /// </summary>
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
