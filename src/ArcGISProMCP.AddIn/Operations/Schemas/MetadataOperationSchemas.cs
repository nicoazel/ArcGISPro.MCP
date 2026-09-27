using System.Text.Json;
using S = ArcGISProMCP.Core.Operations.JsonSchemas;

namespace ArcGISProMCP.AddIn.Operations;

internal static class MetadataOperationSchemas
{
    private static readonly JsonElement Layer = S.String(minLength: 1);
    private static readonly JsonElement Map = S.String();
    private static readonly JsonElement Text = S.String(maxLength: 32768);

    public static JsonElement GetInput { get; } = S.Object(
        [("layer", Layer), ("map", Map), ("includeXml", S.Boolean())],
        ["layer"]);

    public static JsonElement UpdateInput { get; } = S.Object(
        [
            ("layer", Layer),
            ("map", Map),
            ("title", Text),
            ("summary", Text),
            ("description", Text),
            ("tags", S.Array(S.String(maxLength: 512), maxItems: 100, uniqueItems: true)),
            ("credits", Text),
            ("useLimitations", Text),
        ],
        ["layer"]);
}
