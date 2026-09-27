using System.Text.Json;
using S = ArcGISProMCP.Core.Operations.JsonSchemas;

namespace ArcGISProMCP.AddIn.Operations;

internal static class LayoutSurroundOperationSchemas
{
    public static JsonElement EnsureSurroundInput { get; } = S.Object(
        [
            ("layout", S.String()),
            ("frame", S.String()),
            ("name", S.String()),
            ("kind", S.Enum("legend", "north-arrow", "scale-bar")),
            ("x", S.Number()),
            ("y", S.Number()),
            ("width", S.Number(exclusiveMinimum: 0)),
            ("height", S.Number(exclusiveMinimum: 0)),
        ],
        ["layout", "frame", "name", "kind", "x", "y", "width", "height"]);
}
