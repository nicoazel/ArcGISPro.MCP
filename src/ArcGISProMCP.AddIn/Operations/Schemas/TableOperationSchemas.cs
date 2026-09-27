using System.Text.Json;
using S = ArcGISProMCP.Core.Operations.JsonSchemas;

namespace ArcGISProMCP.AddIn.Operations;

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
}
