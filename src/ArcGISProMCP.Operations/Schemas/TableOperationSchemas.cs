using System.Text.Json;
using S = ArcGISProMCP.Core.Operations.JsonSchemas;

namespace ArcGISProMCP.Operations;

internal static class TableOperationSchemas
{
    private static readonly JsonElement Layer = S.String(minLength: 1);
    private static readonly JsonElement Map = S.String();

    public static JsonElement QueryInput { get; } = S.Object(
        [
            ("layer", Layer),
            ("map", Map),
            ("where", S.String()),
            ("fields", S.Array(S.String())),
            ("limit", S.Integer(minimum: 1, maximum: 500)),
        ],
        ["layer"]);

    public static JsonElement StatisticsInput { get; } = S.Object(
        [
            ("layer", Layer),
            ("map", Map),
            ("field", S.String(minLength: 1)),
            ("where", S.String()),
            ("sampleLimit", S.Integer(minimum: 1, maximum: 1000000)),
        ],
        ["layer", "field"]);

    // Output schemas describe OperationResult.data (default JsonSerializer names; nulls written).
    public static JsonElement QueryOutput { get; } = S.Object(
        [
            ("map", S.String()),
            ("layer", S.String()),
            ("where", S.String()),
            ("fields", S.Array(S.String())),
            ("returned", S.Integer(minimum: 0)),
            ("limit", S.Integer(minimum: 1, maximum: 500)),
            ("rows", S.Array(S.Object([], additionalProperties: true, description: "Field name to plain JSON value."))),
        ],
        ["map", "layer", "where", "fields", "returned", "limit", "rows"]);

    public static JsonElement StatisticsOutput { get; } = S.Object(
        [
            ("map", S.String()),
            ("layer", S.String()),
            ("field", S.String()),
            ("fieldType", S.String()),
            ("where", S.String()),
            ("matched", S.Integer(minimum: 0)),
            ("inspected", S.Integer(minimum: 0)),
            ("sampled", S.Boolean()),
            ("nullCount", S.Integer(minimum: 0)),
            ("valueCount", S.Integer(minimum: 0)),
            ("minimum", S.Any("Number; null when no non-null value was inspected.")),
            ("maximum", S.Any("Number; null when no non-null value was inspected.")),
            ("sum", S.Number()),
            ("mean", S.Any("Number; null when no non-null value was inspected.")),
        ],
        ["map", "layer", "field", "fieldType", "where", "matched", "inspected", "sampled", "nullCount", "valueCount", "minimum", "maximum", "sum", "mean"]);
}
