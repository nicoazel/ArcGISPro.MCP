using System.Text.Json;
using S = ArcGISProMCP.Core.Operations.JsonSchemas;

namespace ArcGISProMCP.Operations;

internal static class LayoutPresentationOperationSchemas
{
    public static JsonElement SetTextInput { get; } = S.Object(
        [
            ("layout", S.String(minLength: 1)),
            ("name", S.String(minLength: 1)),
            ("text", S.String()),
            ("x", S.Number()),
            ("y", S.Number()),
            ("fontFamily", S.String(minLength: 1)),
            ("fontStyle", S.String(minLength: 1)),
            ("size", S.Number(exclusiveMinimum: 0)),
            ("color", S.String(pattern: "^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$")),
        ],
        ["layout", "name", "text", "x", "y"]);
}
