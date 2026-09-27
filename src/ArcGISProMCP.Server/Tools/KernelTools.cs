using System.ComponentModel;
using System.Text.Json;
using ArcGISProMCP.Bridge.Transport;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArcGISProMCP.Server.Tools;

[McpServerToolType]
public sealed class KernelTools
{
    [McpServerTool(Name = "system_get_state", Title = "Get ArcGIS Pro state", ReadOnly = true, Destructive = false)]
    [Description("Returns a compact revisioned snapshot of the current ArcGIS Pro project, maps, layouts, active view, capabilities, and connection.")]
    public static Task<string> GetState(IBridgeClient bridge, CancellationToken cancellationToken) =>
        CallAsync(bridge, "system.get_state", null, cancellationToken);

    [McpServerTool(Name = "registry_search", Title = "Search ArcGIS operations", ReadOnly = true, Destructive = false)]
    [Description("Searches the server-side operation registry by intent, GIS terms and aliases, optionally filtered by domain, required capabilities and maximum risk, without adding the full catalog to model context.")]
    public static Task<string> Search(
        IBridgeClient bridge,
        [Description("Natural-language intent such as 'make parcels transparent' or 'compose three map frames'.")] string query,
        [Description("Optional domain: project, map, layer, feature, table, metadata, basemap, style, symbology, label, layout, gp, arcpy, view, workspace, workflow.")] string? domain = null,
        [Description("Maximum results from 1 to 100; values outside the range are clamped.")] int limit = 12,
        [Description("Optional capabilities every result must require, e.g. ['maps'], ['layouts'], ['geoprocessing'], ['arcpy'], ['metadata'], ['visual-observations'].")] string[]? capabilities = null,
        [Description("Optional highest risk to include: ReadOnly, SafeWrite, Destructive or ExternalSideEffect. Use ReadOnly to find only operations that never change the project.")] string? maxRisk = null,
        CancellationToken cancellationToken = default) =>
        CallAsync(bridge, "registry.search", new { query, domain, limit, capabilities, maxRisk }, cancellationToken);

    [McpServerTool(Name = "registry_browse", Title = "Browse ArcGIS operations", ReadOnly = true, Destructive = false)]
    [Description("Browses concise registry entries by domain. Use registry_describe only for the few operations relevant to the task.")]
    public static Task<string> Browse(
        IBridgeClient bridge,
        [Description("Optional registry domain; omit for domain counts and top-level navigation.")] string? domain = null,
        [Description("Maximum entries from 1 to 100.")] int limit = 30,
        CancellationToken cancellationToken = default) =>
        CallAsync(bridge, "registry.browse", new { domain, limit }, cancellationToken);

    [McpServerTool(Name = "registry_describe", Title = "Describe an ArcGIS operation", ReadOnly = true, Destructive = false)]
    [Description("Returns the complete descriptor for one registry id: JSON input schema, required capabilities, examples, risk, confirmation requirement and related operations.")]
    public static Task<string> Describe(
        IBridgeClient bridge,
        [Description("Stable operation id returned by registry_search or registry_browse.")] string operationId,
        CancellationToken cancellationToken = default) =>
        CallAsync(bridge, "registry.describe", new { operationId }, cancellationToken);

    [McpServerTool(Name = "registry_validate", Title = "Validate an ArcGIS operation", ReadOnly = true, Destructive = false)]
    [Description("Validates an operation's arguments against its registry input schema and, for writes, checks the expected workspace revision against the current one. Does not resolve layers, maps or paths, so a valid result does not guarantee the call will succeed. Never changes the project.")]
    public static Task<string> Validate(
        IBridgeClient bridge,
        [Description("Stable operation id.")] string operationId,
        [Description("Arguments matching the described operation schema.")] JsonElement arguments,
        [Description("Optional workspace revision for optimistic concurrency.")] string? expectedRevision = null,
        CancellationToken cancellationToken = default) =>
        CallAsync(bridge, "registry.validate", new { operationId, arguments, expectedRevision }, cancellationToken);

    [McpServerTool(Name = "registry_invoke", Title = "Invoke an ArcGIS operation")]
    [Description("Invokes one previously discovered operation. Writes enforce workspace revisions. Risky operations require an add-in-issued confirmation token unless the host was explicitly started in autonomous-control mode.")]
    public static Task<string> Invoke(
        IBridgeClient bridge,
        [Description("Stable operation id.")] string operationId,
        [Description("Arguments matching the operation schema.")] JsonElement arguments,
        [Description("Workspace revision from system_get_state; required for reliable writes.")] string? expectedRevision = null,
        [Description("Short-lived confirmation token supplied by the ArcGIS Pro panel for risky operations; omitted only when the host explicitly advertises autonomous-control.")] string? confirmationToken = null,
        [Description("Optional key that prevents duplicate mutations when a call is retried.")] string? idempotencyKey = null,
        CancellationToken cancellationToken = default) =>
        CallAsync(bridge, "registry.invoke", new { operationId, arguments, expectedRevision, confirmationToken, idempotencyKey }, cancellationToken);

    [McpServerTool(Name = "workflow_list", Title = "List ArcGIS skills and workflows", ReadOnly = true, Destructive = false)]
    [Description("Lists versioned reusable workflows and their evidence-based ranking. Definitions remain server-side until requested.")]
    public static Task<string> WorkflowList(IBridgeClient bridge, CancellationToken cancellationToken) =>
        CallAsync(bridge, "workflow.list", null, cancellationToken);

    [McpServerTool(Name = "approval_request", Title = "Request local review", Destructive = false)]
    [Description("Queues review of one exact risky operation in the ArcGIS Pro panel without blocking other requests. In normal mode, a person must approve there; this tool cannot grant approval. Poll approval_status, then invoke with the returned token and unchanged revision/arguments.")]
    public static Task<string> RequestApproval(
        IBridgeClient bridge,
        [Description("Stable operation id requiring approval.")] string operationId,
        [Description("Exact arguments to be reviewed.")] JsonElement arguments,
        [Description("Current workspace revision from system_get_state.")] string expectedRevision,
        CancellationToken cancellationToken = default) =>
        CallAsync(bridge, "approval.request", new { operationId, arguments, expectedRevision }, cancellationToken);

    [McpServerTool(Name = "approval_status", Title = "Check local review", ReadOnly = true, Destructive = false)]
    [Description("Returns pending, approved, denied, expired, cancelled or consumed. Only approved requests include a short-lived, single-use token. Unknown ids fail closed. This does not enable autonomous-control mode.")]
    public static Task<string> ApprovalStatus(
        IBridgeClient bridge,
        [Description("Opaque id returned by approval_request.")] string requestId,
        CancellationToken cancellationToken = default) =>
        CallAsync(bridge, "approval.status", new { requestId }, cancellationToken);

    [McpServerTool(Name = "approval_cancel", Title = "Cancel local review", Destructive = false)]
    [Description("Cancels a pending or approved review request and revokes its token. Does not stop an operation that already consumed the token.")]
    public static Task<string> CancelApproval(
        IBridgeClient bridge,
        [Description("Opaque approval request id.")] string requestId,
        CancellationToken cancellationToken = default) =>
        CallAsync(bridge, "approval.cancel", new { requestId }, cancellationToken);

    [McpServerTool(Name = "workflow_get", Title = "Get an ArcGIS workflow", ReadOnly = true, Destructive = false)]
    [Description("Gets one immutable workflow version, including parameters, steps, dependencies and observation hints. Read the linked skill for visual checks and recovery guidance.")]
    public static Task<string> WorkflowGet(
        IBridgeClient bridge,
        [Description("Workflow id.")] string workflowId,
        [Description("Optional immutable version; omit for latest.")] string? version = null,
        CancellationToken cancellationToken = default) =>
        CallAsync(bridge, "workflow.get", new { workflowId, version }, cancellationToken);

    [McpServerTool(Name = "workflow_save", Title = "Save an ArcGIS workflow")]
    [Description("Validates and saves a declarative workflow as a new immutable version. Validated against the registry; steps that execute user code (gp.run, arcpy.*) must be listed in the workflow's allowedOperations.")]
    public static Task<string> WorkflowSave(
        IBridgeClient bridge,
        [Description("Workflow definition JSON matching the workflow schema.")] JsonElement workflow,
        CancellationToken cancellationToken = default) =>
        CallAsync(bridge, "workflow.save", new { workflow }, cancellationToken);

    [McpServerTool(Name = "workflow_run", Title = "Run an ArcGIS workflow")]
    [Description("Runs a saved versioned workflow sequentially with explicit parameters and returns its final result and per-step observations.")]
    public static Task<string> WorkflowRun(
        IBridgeClient bridge,
        [Description("Workflow id.")] string workflowId,
        [Description("Workflow parameter values.")] JsonElement parameters,
        [Description("Workspace revision from system_get_state.")] string? expectedRevision = null,
        [Description("Immutable workflow version; required with idempotencyKey.")] string? version = null,
        [Description("Process-lifetime retry key. Retry only the exact version, parameters and initial revision; never replay completed steps blindly.")] string? idempotencyKey = null,
        CancellationToken cancellationToken = default) =>
        CallAsync(bridge, "workflow.run", new { workflowId, parameters, expectedRevision, version, idempotencyKey }, cancellationToken);

    [McpServerTool(Name = "resource_read", Title = "Read ArcGIS observation", ReadOnly = true, Destructive = false)]
    [Description("Reads a bounded semantic or image observation previously returned as an arcgis:// resource handle.")]
    public static async Task<CallToolResult> ResourceRead(
        IBridgeClient bridge,
        [Description("An arcgis:// resource URI returned by an operation.")] string uri,
        CancellationToken cancellationToken = default)
    {
        var resource = await bridge.CallAsync("resource.read", new { uri }, cancellationToken).ConfigureAwait(false);
        var mimeType = resource.GetProperty("mimeType").GetString() ?? "application/octet-stream";
        if (mimeType.StartsWith("image/", StringComparison.Ordinal))
        {
            var bytes = Convert.FromBase64String(resource.GetProperty("data").GetString()!);
            return new CallToolResult
            {
                Content = [ImageContentBlock.FromBytes(bytes, mimeType),
                    new TextContentBlock { Text = JsonSerializer.Serialize(new { uri, mimeType }) }]
            };
        }
        return new CallToolResult { Content = [new TextContentBlock { Text = resource.GetRawText() }] };
    }

    private static async Task<string> CallAsync(
        IBridgeClient bridge,
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        var result = await bridge.CallAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        return result.GetRawText();
    }
}
