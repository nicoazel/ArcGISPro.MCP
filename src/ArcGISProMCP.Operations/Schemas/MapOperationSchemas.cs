using System.Text.Json;
using S = ArcGISProMCP.Core.Operations.JsonSchemas;

namespace ArcGISProMCP.Operations;

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

    // Describes OperationResult.data for map.list (camelCase names).
    public static JsonElement ListOutput { get; } = S.Array(S.Object(
        [
            ("id", S.String()),
            ("name", S.String()),
            ("type", S.String()),
            ("layerCount", S.Integer(minimum: 0)),
            ("isActive", S.Boolean()),
        ],
        ["id", "name", "type", "layerCount", "isActive"]));
}
