using System.Text.Json;

namespace ArcGISProMCP.Server.Tests.Harness;

/// <summary>
/// Representative tools/call arguments for every tool: a minimal call that omits every optional
/// argument and, where the tool has optional arguments, a call that supplies all of them.
/// </summary>
public static class ToolCases
{
    public sealed record ToolCase(string Tool, string Name, string ArgumentsJson)
    {
        public Dictionary<string, object?> Arguments =>
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ArgumentsJson)!
                .ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal);

        public JsonElement ArgumentsElement => JsonDocument.Parse(ArgumentsJson).RootElement.Clone();
    }

    /// <summary>Tools that answer from bundled content and never call the bridge.</summary>
    public static readonly string[] LocalTools = ["skill_search", "skill_get"];

    public static readonly ToolCase[] All =
    [
        new("system_get_state", "minimal", "{}"),
        new("registry_search", "minimal", """{"query":"make parcels transparent"}"""),
        new("registry_search", "full", """{"query":"make parcels transparent","domain":"layer","limit":5,"capabilities":["maps"],"maxRisk":"SafeWrite"}"""),
        new("registry_browse", "minimal", "{}"),
        new("registry_browse", "full", """{"domain":"layer","limit":10}"""),
        new("registry_describe", "minimal", """{"operationId":"layer.set_transparency"}"""),
        new("registry_validate", "minimal", """{"operationId":"layer.set_transparency","arguments":{"layer":"Parcels","transparency":40}}"""),
        new("registry_validate", "full", """{"operationId":"layer.set_transparency","arguments":{"layer":"Parcels","transparency":40},"expectedRevision":"r1"}"""),
        new("registry_invoke", "minimal", """{"operationId":"layer.list","arguments":{}}"""),
        new("registry_invoke", "full", """{"operationId":"layer.remove","arguments":{"layer":"Parcels"},"expectedRevision":"r1","confirmationToken":"token-1","idempotencyKey":"key-1","dryRun":false}"""),
        new("registry_invoke", "dry-run", """{"operationId":"gp.run","arguments":{"tool":"analysis.Buffer"},"dryRun":true}"""),
        new("workflow_list", "minimal", "{}"),
        new("approval_request", "minimal", """{"operationId":"layer.remove","arguments":{"layer":"Parcels"},"expectedRevision":"r1"}"""),
        new("approval_status", "minimal", """{"requestId":"req-1"}"""),
        new("approval_status", "full", """{"requestId":"req-1","waitSeconds":30}"""),
        new("approval_cancel", "minimal", """{"requestId":"req-1"}"""),
        new("workflow_get", "minimal", """{"workflowId":"flow"}"""),
        new("workflow_get", "full", """{"workflowId":"flow","version":"1.0.0"}"""),
        new("workflow_save", "minimal", """{"workflow":{"id":"flow","version":"1.0.0","title":"Flow","steps":[]}}"""),
        new("workflow_run", "minimal", """{"workflowId":"flow","parameters":{"title":"T"}}"""),
        new("workflow_run", "full", """{"workflowId":"flow","parameters":{"title":"T"},"expectedRevision":"r1","version":"1.0.0","idempotencyKey":"key-1"}"""),
        new("resource_read", "minimal", """{"uri":"arcgis://resource/abc"}"""),
        new("skill_search", "minimal", "{}"),
        new("skill_search", "full", """{"query":"cartography"}"""),
        new("skill_get", "minimal", """{"skillId":"arcgis.cartography.master-plan"}"""),
    ];

    /// <summary>The bridge-backed tools, one entry per tool.</summary>
    public static IEnumerable<string> BridgeTools =>
        All.Select(item => item.Tool).Distinct(StringComparer.Ordinal).Where(tool => !LocalTools.Contains(tool));
}
