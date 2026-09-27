using System.ComponentModel;
using System.Text.Json;
using ArcGISProMCP.Bridge.Protocol;
using ArcGISProMCP.Bridge.Transport;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Workflows;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArcGISProMCP.Server.Tools;

// Every tool returns a ToolEnvelope<T> ({ ok, result, error }) as structuredContent and as the text
// block, with isError set when ok is false. Annotations are explicit on every tool; nothing here
// reaches outside the local ArcGIS Pro session, so OpenWorld is always false.
[McpServerToolType]
public sealed class KernelTools
{
    [McpServerTool(Name = "system_get_state", Title = "Get ArcGIS Pro state",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<SystemStateResult>))]
    [Description("Returns a compact revisioned snapshot of the current ArcGIS Pro project, maps, layouts, active view, capabilities, and connection.")]
    public static Task<CallToolResult> GetState(IBridgeClient bridge, CancellationToken cancellationToken) =>
        ToolResults.CallAsync<SystemStateResult>(bridge, "system.get_state", null, cancellationToken);

    [McpServerTool(Name = "registry_search", Title = "Search ArcGIS operations",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<RegistrySearchHit[]>))]
    [Description("Searches the server-side operation registry by intent, GIS terms and aliases, optionally filtered by domain, required capabilities and maximum risk, without adding the full catalog to model context.")]
    public static Task<CallToolResult> Search(
        IBridgeClient bridge,
        [Description("Natural-language intent such as 'make parcels transparent' or 'compose three map frames'.")] string query,
        [Description("Optional domain: project, map, layer, feature, table, metadata, basemap, style, symbology, label, layout, gp, arcpy, view, workspace, workflow.")] string? domain = null,
        [Description("Maximum results from 1 to 100; values outside the range are clamped.")] int limit = 12,
        [Description("Optional capabilities every result must require, e.g. ['maps'], ['layouts'], ['geoprocessing'], ['arcpy'], ['metadata'], ['visual-observations'].")] string[]? capabilities = null,
        [Description("Optional highest risk to include: ReadOnly, SafeWrite, Destructive or ExternalSideEffect. Use ReadOnly to find only operations that never change the project.")] string? maxRisk = null,
        CancellationToken cancellationToken = default) =>
        ToolResults.CallAsync<RegistrySearchHit[]>(bridge, "registry.search", new { query, domain, limit, capabilities, maxRisk }, cancellationToken);

    [McpServerTool(Name = "registry_browse", Title = "Browse ArcGIS operations",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<RegistryBrowseResult>))]
    [Description("Browses concise registry entries by domain. Without a domain it returns total and per-domain counts; with a domain it returns that domain's operations. Use registry_describe only for the few operations relevant to the task.")]
    public static Task<CallToolResult> Browse(
        IBridgeClient bridge,
        [Description("Optional registry domain; omit for domain counts and top-level navigation.")] string? domain = null,
        [Description("Maximum entries from 1 to 100.")] int limit = 30,
        CancellationToken cancellationToken = default) =>
        ToolResults.CallAsync<RegistryBrowseResult>(bridge, "registry.browse", new { domain, limit }, cancellationToken);

    [McpServerTool(Name = "registry_describe", Title = "Describe an ArcGIS operation",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<OperationDescription>))]
    [Description("Returns the complete descriptor for one registry id: JSON input schema, required capabilities, examples, risk, confirmation requirement, related operations, and resultSchema (the shape of the registry_invoke result for this operation; its data member is unconstrained unless the operation declares an outputSchema).")]
    public static Task<CallToolResult> Describe(
        IBridgeClient bridge,
        [Description("Stable operation id returned by registry_search or registry_browse.")] string operationId,
        CancellationToken cancellationToken = default) =>
        ToolResults.CallAsync<OperationDescription>(bridge, "registry.describe", new { operationId }, cancellationToken);

    [McpServerTool(Name = "registry_validate", Title = "Validate an ArcGIS operation",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<RegistryValidationResult>))]
    [Description("Validates an operation's arguments against its registry input schema and, for writes, checks the expected workspace revision against the current one. Does not resolve layers, maps or paths, so a valid result does not guarantee the call will succeed. Never changes the project.")]
    public static Task<CallToolResult> Validate(
        IBridgeClient bridge,
        [Description("Stable operation id.")] string operationId,
        [Description("Arguments matching the described operation schema.")] JsonElement arguments,
        [Description("Optional workspace revision for optimistic concurrency.")] string? expectedRevision = null,
        CancellationToken cancellationToken = default) =>
        ToolResults.CallAsync<RegistryValidationResult>(bridge, "registry.validate", new { operationId, arguments, expectedRevision }, cancellationToken);

    [McpServerTool(Name = "registry_invoke", Title = "Invoke an ArcGIS operation",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<OperationResult>))]
    [Description("Invokes one previously discovered operation. Writes enforce workspace revisions. Risky operations require an add-in-issued confirmation token unless the host was explicitly started in autonomous-control mode. When the operation reports success: false the call is an error (isError) and result still carries the operation's result.")]
    public static Task<CallToolResult> Invoke(
        IBridgeClient bridge,
        [Description("Stable operation id.")] string operationId,
        [Description("Arguments matching the operation schema.")] JsonElement arguments,
        [Description("Workspace revision from system_get_state; required for reliable writes.")] string? expectedRevision = null,
        [Description("Short-lived confirmation token supplied by the ArcGIS Pro panel for risky operations; omitted only when the host explicitly advertises autonomous-control.")] string? confirmationToken = null,
        [Description("Optional key that prevents duplicate mutations when a call is retried.")] string? idempotencyKey = null,
        CancellationToken cancellationToken = default) =>
        ToolResults.CallAsync<OperationResult>(
            bridge, "registry.invoke", new { operationId, arguments, expectedRevision, confirmationToken, idempotencyKey }, cancellationToken,
            result => result.Success ? null : new ToolError(
                result.ErrorCode ?? "operation_failed",
                result.Message ?? "The operation reported a failure.",
                false,
                result.WorkspaceRevision));

    [McpServerTool(Name = "workflow_list", Title = "List ArcGIS skills and workflows",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<WorkflowSummary[]>))]
    [Description("Lists versioned reusable workflows and their evidence-based ranking. Definitions remain server-side until requested.")]
    public static Task<CallToolResult> WorkflowList(IBridgeClient bridge, CancellationToken cancellationToken) =>
        ToolResults.CallAsync<WorkflowSummary[]>(bridge, "workflow.list", null, cancellationToken);

    [McpServerTool(Name = "approval_request", Title = "Request local review",
        ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<ApprovalStatusResult>))]
    [Description("Queues review of one exact risky operation in the ArcGIS Pro panel without blocking other requests. In normal mode, a person must approve there; this tool cannot grant approval. Then call approval_status (optionally with waitSeconds to wait for the decision) and invoke with the returned token and unchanged revision/arguments.")]
    public static Task<CallToolResult> RequestApproval(
        IBridgeClient bridge,
        [Description("Stable operation id requiring approval.")] string operationId,
        [Description("Exact arguments to be reviewed.")] JsonElement arguments,
        [Description("Current workspace revision from system_get_state.")] string expectedRevision,
        CancellationToken cancellationToken = default) =>
        ToolResults.CallAsync<ApprovalStatusResult>(bridge, "approval.request", new { operationId, arguments, expectedRevision }, cancellationToken);

    [McpServerTool(Name = "approval_status", Title = "Check local review",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<ApprovalStatusResult>))]
    [Description("Returns pending, approved, denied, expired, cancelled or consumed. Only approved requests include a short-lived, single-use token. With waitSeconds, a pending request is held until a person decides or the wait elapses; the status is returned either way and waiting never approves anything. Unknown ids fail closed. This does not enable autonomous-control mode.")]
    public static Task<CallToolResult> ApprovalStatus(
        IBridgeClient bridge,
        [Description("Opaque id returned by approval_request.")] string requestId,
        [Description("Seconds to wait for a pending request to be decided, 0 to 120 (values outside are clamped). 0 returns immediately. Keep it below your client's tool-call timeout.")] int waitSeconds = 0,
        CancellationToken cancellationToken = default) =>
        ToolResults.CallAsync<ApprovalStatusResult>(bridge, "approval.status", new { requestId, waitSeconds }, cancellationToken);

    [McpServerTool(Name = "approval_cancel", Title = "Cancel local review",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<ApprovalCancelResult>))]
    [Description("Cancels a pending or approved review request and revokes its token. Does not stop an operation that already consumed the token.")]
    public static Task<CallToolResult> CancelApproval(
        IBridgeClient bridge,
        [Description("Opaque approval request id.")] string requestId,
        CancellationToken cancellationToken = default) =>
        ToolResults.CallAsync<ApprovalCancelResult>(bridge, "approval.cancel", new { requestId }, cancellationToken);

    [McpServerTool(Name = "workflow_get", Title = "Get an ArcGIS workflow",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<WorkflowDefinition>))]
    [Description("Gets one immutable workflow version, including parameters, steps, dependencies and observation hints. Read the linked skill for visual checks and recovery guidance.")]
    public static Task<CallToolResult> WorkflowGet(
        IBridgeClient bridge,
        [Description("Workflow id.")] string workflowId,
        [Description("Optional immutable version; omit for latest.")] string? version = null,
        CancellationToken cancellationToken = default) =>
        ToolResults.CallAsync<WorkflowDefinition>(bridge, "workflow.get", new { workflowId, version }, cancellationToken);

    [McpServerTool(Name = "workflow_save", Title = "Save an ArcGIS workflow",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<WorkflowSaveResult>))]
    [Description("Validates and saves a declarative workflow as a new immutable version. Validated against the registry; steps that execute user code (gp.run, arcpy.*) must be listed in the workflow's allowedOperations.")]
    public static Task<CallToolResult> WorkflowSave(
        IBridgeClient bridge,
        [Description("Workflow definition JSON matching the workflow schema.")] JsonElement workflow,
        CancellationToken cancellationToken = default) =>
        ToolResults.CallAsync<WorkflowSaveResult>(bridge, "workflow.save", new { workflow }, cancellationToken);

    [McpServerTool(Name = "workflow_run", Title = "Run an ArcGIS workflow",
        ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<WorkflowRunResult>))]
    [Description("Runs a saved versioned workflow sequentially with explicit parameters and returns its final result and per-step observations. When the run reports success: false the call is an error (isError) and result still carries every step outcome.")]
    public static Task<CallToolResult> WorkflowRun(
        IBridgeClient bridge,
        [Description("Workflow id.")] string workflowId,
        [Description("Workflow parameter values.")] JsonElement parameters,
        [Description("Workspace revision from system_get_state.")] string? expectedRevision = null,
        [Description("Immutable workflow version; required with idempotencyKey.")] string? version = null,
        [Description("Process-lifetime retry key. Retry only the exact version, parameters and initial revision; never replay completed steps blindly.")] string? idempotencyKey = null,
        CancellationToken cancellationToken = default) =>
        ToolResults.CallAsync<WorkflowRunResult>(
            bridge, "workflow.run", new { workflowId, parameters, expectedRevision, version, idempotencyKey }, cancellationToken,
            result => result.Success ? null : new ToolError(
                result.ErrorCode ?? "workflow_step_failed",
                result.Message ?? "One or more workflow steps failed; see result.results for each step.",
                false,
                result.Revision ?? result.CurrentRevision));

    [McpServerTool(Name = "resource_read", Title = "Read ArcGIS observation",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<ResourceContent>))]
    [Description("Reads a bounded semantic or image observation previously returned as an arcgis:// resource handle. Images are returned as an image content block, and result.data is then null.")]
    public static async Task<CallToolResult> ResourceRead(
        IBridgeClient bridge,
        [Description("An arcgis:// resource URI returned by an operation.")] string uri,
        CancellationToken cancellationToken = default)
    {
        var (resource, error) = await ToolResults.ReadAsync<ResourceContent>(bridge, "resource.read", new { uri }, cancellationToken).ConfigureAwait(false);
        if (resource is null)
            return ToolResults.Failure<ResourceContent>(error!);
        if (!resource.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || resource.Data is null)
            return ToolResults.Success(resource);

        // The image travels once, as a native image block; the structured result describes it.
        var bytes = Convert.FromBase64String(resource.Data);
        var result = ToolResults.Success(resource with { Data = null });
        result.Content.Insert(0, ImageContentBlock.FromBytes(bytes, resource.MimeType));
        return result;
    }
}
