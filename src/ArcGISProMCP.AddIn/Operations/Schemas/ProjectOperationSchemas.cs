using System.Text.Json;
using S = ArcGISProMCP.Core.Operations.JsonSchemas;

namespace ArcGISProMCP.AddIn.Operations;

internal static class ProjectOperationSchemas
{
    public static JsonElement OpenInput { get; } = S.Object([("path", S.String(minLength: 1))], ["path"]);

    // Describes OperationResult.data for project.get: the workspace snapshot's ProjectState,
    // revision and capture time with default JsonSerializer names (nulls written).
    public static JsonElement GetOutput { get; } = S.Object(
        [
            ("Project", S.Object(
                [
                    ("Name", S.Any("Project name; null when no project is open.")),
                    ("Uri", S.Any("Project path; null when no project is open.")),
                    ("IsDirty", S.Boolean()),
                    ("IsOpen", S.Boolean()),
                ],
                ["Name", "Uri", "IsDirty", "IsOpen"])),
            ("Revision", S.String()),
            ("CapturedAt", S.String(description: "ISO 8601 date-time.")),
        ],
        ["Project", "Revision", "CapturedAt"]);
}
