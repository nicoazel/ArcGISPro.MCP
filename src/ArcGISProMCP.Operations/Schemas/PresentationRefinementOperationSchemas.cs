using System.Text.Json;
using S = ArcGISProMCP.Core.Operations.JsonSchemas;

namespace ArcGISProMCP.Operations;

internal static class PresentationRefinementOperationSchemas
{
    public static JsonElement SetFrameExtentInput { get; } = S.Object(
        [
            ("layout", S.String()),
            ("frame", S.String()),
            ("layer", S.String()),
            ("padding", S.Number(minimum: 1, maximum: 20)),
            ("heading", S.Number(minimum: -360, maximum: 360)),
            ("pitch", S.Number(minimum: -90, maximum: 90)),
        ],
        ["layout", "frame", "layer"]);

    public static JsonElement SetUniqueValuesInput { get; } = S.Object(
        [
            ("map", S.String()),
            ("layer", S.String()),
            ("field", S.String()),
            ("classes", S.Array(S.Object(
                [("value", S.String()), ("label", S.String()), ("color", S.String())],
                ["value", "color"]))),
            ("defaultColor", S.String()),
        ],
        ["layer", "field", "classes"]);
}
