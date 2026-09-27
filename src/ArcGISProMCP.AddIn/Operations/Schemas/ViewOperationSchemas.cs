using System.Text.Json;
using S = ArcGISProMCP.Core.Operations.JsonSchemas;

namespace ArcGISProMCP.AddIn.Operations;

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
}
