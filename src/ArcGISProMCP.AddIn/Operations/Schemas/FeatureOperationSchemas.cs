using System.Text.Json;
using S = ArcGISProMCP.Core.Operations.JsonSchemas;

namespace ArcGISProMCP.AddIn.Operations;

internal static class FeatureOperationSchemas
{
    private static readonly JsonElement Layer = S.String(minLength: 1);
    private static readonly JsonElement Map = S.String();
    private static readonly JsonElement Where = S.String(maxLength: 4096);
    private static readonly JsonElement Limit = S.Integer(minimum: 1, maximum: 500);

    private static readonly JsonElement Envelope = S.Object(
        [
            ("xmin", S.Number()),
            ("ymin", S.Number()),
            ("xmax", S.Number()),
            ("ymax", S.Number()),
        ],
        ["xmin", "ymin", "xmax", "ymax"]);

    private static readonly JsonElement SpatialRelationship =
        S.Enum("intersects", "envelopeIntersects", "contains", "within", "touches", "crosses", "overlaps");

    private static readonly JsonElement Attributes = S.Object([], additionalProperties: true);

    private static readonly JsonElement Geometry = S.Object(
        [
            ("type", S.Enum("point", "polyline", "polygon")),
            ("x", S.Number()),
            ("y", S.Number()),
            ("z", S.Number()),
            ("coordinates", S.Array(S.Array(S.Number(), minItems: 2, maxItems: 3), minItems: 2)),
        ],
        ["type"]);

    private static readonly JsonElement Target = S.Object(
        [
            ("objectId", S.Integer(minimum: 0)),
            ("globalId", S.String(minLength: 32, maxLength: 38)),
        ],
        minProperties: 1,
        maxProperties: 1);

    public static JsonElement LayerDescribeInput { get; } = S.Object(
        [("layer", Layer), ("map", Map)],
        ["layer"]);

    public static JsonElement QueryInput { get; } = S.Object(
        [
            ("layer", Layer),
            ("map", Map),
            ("where", Where),
            ("fields", S.Array(S.String(), maxItems: 64)),
            ("envelope", Envelope),
            ("spatialRelationship", SpatialRelationship),
            ("limit", Limit),
        ],
        ["layer"]);

    public static JsonElement SelectInput { get; } = S.Object(
        [
            ("layer", Layer),
            ("map", Map),
            ("where", Where),
            ("envelope", Envelope),
            ("spatialRelationship", SpatialRelationship),
            ("mode", S.Enum("new", "add")),
            ("limit", Limit),
        ],
        ["layer"]);

    public static JsonElement CreateInput { get; } = S.Object(
        [("layer", Layer), ("map", Map), ("attributes", Attributes), ("geometry", Geometry)],
        ["layer", "geometry"]);

    public static JsonElement UpdateInput { get; } = S.Object(
        [("layer", Layer), ("map", Map), ("target", Target), ("attributes", Attributes), ("geometry", Geometry)],
        ["layer", "target"]);

    public static JsonElement DeleteInput { get; } = S.Object(
        [("layer", Layer), ("map", Map), ("target", Target)],
        ["layer", "target"]);
}
