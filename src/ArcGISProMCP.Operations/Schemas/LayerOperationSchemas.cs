using System.Text.Json;
using S = ArcGISProMCP.Core.Operations.JsonSchemas;

namespace ArcGISProMCP.Operations;

internal static class LayerOperationSchemas
{
    private static readonly JsonElement Layer = S.String(minLength: 1);
    private static readonly JsonElement Map = S.String();

    public static JsonElement ListInput { get; } = S.Object([("map", Map)]);

    public static JsonElement AddInput { get; } = S.Object(
        [("source", S.String(minLength: 1)), ("map", Map), ("name", S.String())],
        ["source"]);

    public static JsonElement SetAppearanceInput { get; } = S.Object(
        [
            ("layer", Layer),
            ("map", Map),
            ("visible", S.Boolean()),
            ("transparency", S.Number(minimum: 0, maximum: 100)),
        ],
        ["layer"]);

    public static JsonElement SetElevationInput { get; } = S.Object(
        [
            ("layer", Layer),
            ("map", Map),
            ("mode", S.Enum("on-ground", "relative-to-ground", "relative-to-scene", "absolute-height")),
            ("offset", S.Number(minimum: -1000000, maximum: 1000000)),
            ("verticalExaggeration", S.Number(minimum: 0.01, maximum: 100)),
        ],
        ["layer", "mode"]);

    public static JsonElement BasemapSetInput { get; } = S.Object(
        [("basemap", S.String(minLength: 1)), ("map", Map)],
        ["basemap"]);

    public static JsonElement StyleSearchInput { get; } = S.Object(
        [
            ("query", S.String()),
            ("type", S.Enum("point", "line", "polygon", "text")),
            ("limit", S.Integer(minimum: 1, maximum: 100)),
        ]);

    // Describes OperationResult.data for layer.list (camelCase names; nulls written).
    public static JsonElement ListOutput { get; } = S.Object(
        [
            ("map", S.String()),
            ("layers", S.Array(S.Object(
                [
                    ("id", S.String()),
                    ("name", S.String()),
                    ("type", S.String()),
                    ("isVisible", S.Boolean()),
                    ("transparency", S.Number()),
                    ("drawingOrder", S.Integer(minimum: 0)),
                    ("isFeatureLayer", S.Boolean()),
                    ("elevation", S.Any("{ mode, offset, verticalExaggeration } for feature layers; null otherwise.")),
                ],
                ["id", "name", "type", "isVisible", "transparency", "drawingOrder", "isFeatureLayer", "elevation"]))),
        ],
        ["map", "layers"]);
}
