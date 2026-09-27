using System.Text.Json;
using S = ArcGISProMCP.Core.Operations.JsonSchemas;

namespace ArcGISProMCP.AddIn.Operations;

internal static class SymbologyOperationSchemas
{
    private static readonly JsonElement Layer = S.String(minLength: 1);
    private static readonly JsonElement Map = S.String();
    private static readonly JsonElement PositiveNumber = S.Number(exclusiveMinimum: 0);

    public static JsonElement SetSimpleInput { get; } = S.Object(
        [
            ("layer", Layer),
            ("map", Map),
            ("color", S.String(pattern: "^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$")),
            ("size", PositiveNumber),
            ("outlineColor", S.String()),
            ("outlineWidth", S.Number(minimum: 0)),
        ],
        ["layer", "color"]);

    public static JsonElement LabelConfigureInput { get; } = S.Object(
        [
            ("layer", Layer),
            ("map", Map),
            ("enabled", S.Boolean()),
            ("expression", S.String()),
            ("fontFamily", S.String()),
            ("fontStyle", S.String()),
            ("size", PositiveNumber),
            ("color", S.String()),
        ],
        ["layer"]);
}
