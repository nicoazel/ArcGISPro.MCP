using System.Text.Json;
using S = ArcGISProMCP.Core.Operations.JsonSchemas;

namespace ArcGISProMCP.AddIn.Operations;

internal static class MapOperationSchemas
{
    public static JsonElement EnsureInput { get; } = S.Object(
        [
            ("name", S.String(minLength: 1)),
            ("type", S.Enum("map", "scene", "global-scene")),
            ("basemap", S.String()),
        ],
        ["name"]);

    public static JsonElement ActivateInput { get; } = S.Object([("map", S.String(minLength: 1))], ["map"]);
}
