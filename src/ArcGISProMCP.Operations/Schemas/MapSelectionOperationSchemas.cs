using System.Text.Json;
using S = ArcGISProMCP.Core.Operations.JsonSchemas;

namespace ArcGISProMCP.Operations;

internal static class MapSelectionOperationSchemas
{
    public static JsonElement ClearSelectionInput { get; } = S.Object([("map", S.String())]);
}
