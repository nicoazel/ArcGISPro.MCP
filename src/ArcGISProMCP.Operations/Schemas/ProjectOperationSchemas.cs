using System.Text.Json;
using S = ArcGISProMCP.Core.Operations.JsonSchemas;

namespace ArcGISProMCP.Operations;

internal static class ProjectOperationSchemas
{
    public static JsonElement OpenInput { get; } = S.Object([("path", S.String(minLength: 1))], ["path"]);

    // Describes OperationResult.data for project.get: the workspace snapshot's ProjectState,
    // revision and capture time with camelCase names (nulls written).
    public static JsonElement GetOutput { get; } = S.Object(
        [
            ("project", S.Object(
                [
                    ("name", S.Any("Project name; null when no project is open.")),
                    ("uri", S.Any("Project path; null when no project is open.")),
                    ("isDirty", S.Boolean()),
                    ("isOpen", S.Boolean()),
                ],
                ["name", "uri", "isDirty", "isOpen"])),
            ("revision", S.String()),
            ("capturedAt", S.String(description: "ISO 8601 date-time.")),
        ],
        ["project", "revision", "capturedAt"]);
}
