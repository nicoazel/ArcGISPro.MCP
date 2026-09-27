using System.Text.Json;
using S = ArcGISProMCP.Core.Operations.JsonSchemas;

namespace ArcGISProMCP.AddIn.Operations;

internal static class ProjectOperationSchemas
{
    public static JsonElement OpenInput { get; } = S.Object([("path", S.String(minLength: 1))], ["path"]);
}
