using System.Text.Json;
using S = ArcGISProMCP.Core.Operations.JsonSchemas;

namespace ArcGISProMCP.Operations;

// Operation schemas depend only on Core; every host and test project shares these instances.
internal static class ArcPyOperationSchemas
{
    private static readonly JsonElement ScriptPath = S.String(minLength: 1, maxLength: 512);

    public static JsonElement InspectScriptInput { get; } = S.Object(
        [("scriptPath", ScriptPath)],
        ["scriptPath"]);

    public static JsonElement RunScriptInput { get; } = S.Object(
        [
            ("scriptPath", ScriptPath),
            ("scriptSha256", S.String(pattern: "^[A-Fa-f0-9]{64}$")),
            ("arguments", S.Array(S.String(maxLength: 8192), maxItems: 64)),
            ("timeoutSeconds", S.Integer(minimum: 1, maximum: 900)),
        ],
        ["scriptPath", "scriptSha256"]);
}
