using System.Text.Json;
using S = ArcGISProMCP.Core.Operations.JsonSchemas;

namespace ArcGISProMCP.Operations;

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

    // Output schemas describe OperationResult.data. Member names follow the anonymous
    // objects in FeatureOperations.cs as serialized by ProOperationBase.Json
    // (camelCase names, nulls written); nullable values are Any().
    private static readonly JsonElement Handle = S.String();

    private static readonly JsonElement FeatureRows = S.Array(
        S.Object([], additionalProperties: true, description: "objectId, globalId (null without a GlobalID field), and the requested field values."));

    public static JsonElement LayerDescribeOutput { get; } = S.Object(
        [
            ("map", Handle),
            ("layer", Handle),
            ("name", S.String()),
            ("editable", S.Boolean()),
            ("objectIdField", S.String()),
            ("globalIdField", S.Any("GlobalID field name; null when the layer has none.")),
            ("shapeField", S.Any("Shape field name; null for non-feature-class definitions.")),
            ("geometryType", S.Any("Shape type name; null for non-feature-class definitions.")),
            ("spatialReference", S.Any("{ wkid, name }; null for non-feature-class definitions.")),
            ("fields", S.Array(S.Object(
                [
                    ("name", S.String()),
                    ("aliasName", S.Any()),
                    ("type", S.String()),
                    ("isNullable", S.Boolean()),
                    ("isEditable", S.Boolean()),
                    ("length", S.Integer()),
                    ("isObjectId", S.Boolean()),
                    ("isGlobalId", S.Boolean()),
                    ("isGeometry", S.Boolean()),
                ],
                ["name", "aliasName", "type", "isNullable", "isEditable", "length", "isObjectId", "isGlobalId", "isGeometry"]))),
        ],
        ["map", "layer", "name", "editable", "objectIdField", "globalIdField", "shapeField", "geometryType", "spatialReference", "fields"]);

    public static JsonElement QueryOutput { get; } = S.Object(
        [
            ("map", Handle),
            ("layer", Handle),
            ("where", S.String()),
            ("spatialRelationship", S.Any("Spatial relationship name; null without a spatial filter.")),
            ("fields", S.Array(S.String())),
            ("returned", S.Integer(minimum: 0)),
            ("limit", S.Integer(minimum: 1, maximum: 500)),
            ("rows", FeatureRows),
        ],
        ["map", "layer", "where", "spatialRelationship", "fields", "returned", "limit", "rows"]);

    public static JsonElement SelectOutput { get; } = S.Object(
        [
            ("map", Handle),
            ("layer", Handle),
            ("mode", S.Enum("new", "add")),
            ("matched", S.Integer(minimum: 0)),
            ("selectionCount", S.Integer(minimum: 0)),
            ("limit", S.Integer(minimum: 1, maximum: 500)),
        ],
        ["map", "layer", "mode", "matched", "selectionCount", "limit"]);

    public static JsonElement CreateOutput { get; } = S.Object(
        [
            ("map", Handle),
            ("layer", Handle),
            ("objectId", S.Any("ObjectID from the edit row token; may be null.")),
            ("globalId", S.Any("GlobalID from the edit row token; may be null.")),
        ],
        ["map", "layer", "objectId", "globalId"]);

    public static JsonElement UpdateOutput { get; } = S.Object(
        [
            ("map", Handle),
            ("layer", Handle),
            ("objectId", S.Integer()),
            ("updatedAttributes", S.Array(S.String())),
            ("geometryUpdated", S.Boolean()),
        ],
        ["map", "layer", "objectId", "updatedAttributes", "geometryUpdated"]);

    public static JsonElement DeleteOutput { get; } = S.Object(
        [
            ("map", Handle),
            ("layer", Handle),
            ("objectId", S.Integer()),
            ("deleted", S.Boolean()),
        ],
        ["map", "layer", "objectId", "deleted"]);
}
