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

    // Output schemas describe OperationResult.data (default JsonSerializer names; nulls written).
    public static JsonElement ListOutput { get; } = S.Array(S.Object(
        [
            ("id", S.String()),
            ("Name", S.String()),
            ("mapFrames", S.Array(S.Object(
                [("Name", S.String()), ("map", S.Any("Map handle; null when the frame has no map."))],
                ["Name", "map"]))),
        ],
        ["id", "Name", "mapFrames"]));

    public static JsonElement InspectOutput { get; } = S.Object(
        [
            ("id", S.String()),
            ("Name", S.String()),
            ("page", S.Object(
                [("width", S.Number()), ("height", S.Number()), ("units", S.String())],
                ["width", "height", "units"])),
            ("elements", S.Array(S.Object(
                [
                    ("Name", S.String()),
                    ("type", S.String()),
                    ("drawingOrder", S.Integer(minimum: 0)),
                    ("bounds", S.Object(
                        [
                            ("x", S.Number()),
                            ("y", S.Number()),
                            ("width", S.Number()),
                            ("height", S.Number()),
                            ("xMax", S.Number()),
                            ("yMax", S.Number()),
                        ],
                        ["x", "y", "width", "height", "xMax", "yMax"])),
                    ("mapFrame", S.Any("{ map, mapName, camera } for map frames; null otherwise.")),
                ],
                ["Name", "type", "drawingOrder", "bounds", "mapFrame"]))),
        ],
        ["id", "Name", "page", "elements"]);
}
