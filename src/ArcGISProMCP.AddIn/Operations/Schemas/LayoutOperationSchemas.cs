using System.Text.Json;
using S = ArcGISProMCP.Core.Operations.JsonSchemas;

namespace ArcGISProMCP.AddIn.Operations;

internal static class LayoutOperationSchemas
{
    private static readonly JsonElement Layout = S.String(minLength: 1);
    private static readonly JsonElement PositiveNumber = S.Number(exclusiveMinimum: 0);

    public static JsonElement InspectInput { get; } = S.Object([("layout", Layout)], ["layout"]);

    public static JsonElement EnsureInput { get; } = S.Object(
        [("name", S.String(minLength: 1)), ("width", PositiveNumber), ("height", PositiveNumber)],
        ["name"]);

    public static JsonElement AddMapFrameInput { get; } = S.Object(
        [
            ("layout", Layout),
            ("map", S.String(minLength: 1)),
            ("name", S.String()),
            ("x", S.Number()),
            ("y", S.Number()),
            ("width", PositiveNumber),
            ("height", PositiveNumber),
        ],
        ["layout", "map"]);

    public static JsonElement ActivateInput { get; } = S.Object([("layout", Layout)], ["layout"]);
}
