using System.Text.Json;
using S = ArcGISProMCP.Core.Operations.JsonSchemas;

namespace ArcGISProMCP.Operations;

internal static class ViewOperationSchemas
{
    private static readonly JsonElement Pixels = S.Integer(minimum: 64, maximum: 4096);

    public static JsonElement CaptureInput { get; } = S.Object(
        [
            ("view", S.Enum("active-map", "layout")),
            ("layout", S.String()),
            ("width", Pixels),
            ("height", Pixels),
        ]);

    // Describes OperationResult.data for view.capture (default JsonSerializer names).
    public static JsonElement CaptureOutput { get; } = S.Object(
        [
            ("resource", S.String(minLength: 1, description: "Resource URI of the stored PNG.")),
            ("width", S.Integer()),
            ("height", S.Integer()),
            ("sourceKind", S.Enum("map", "layout")),
            ("sourceName", S.String()),
            ("sourceUri", S.String()),
            ("capturedAt", S.String(description: "ISO 8601 date-time.")),
        ],
        ["resource", "width", "height", "sourceKind", "sourceName", "sourceUri", "capturedAt"]);
}
