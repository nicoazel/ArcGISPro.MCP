using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Workflows;
using ArcGISProMCP.Core.Workspaces;

namespace ArcGISProMCP.Bridge.Protocol;

// Typed results of the bridge methods. The add-in serializes these records and the MCP gateway
// deserializes them, so both sides share one wire contract. Field names are camelCase; nulls are
// written; enums are numbers (as they always were on the wire). Loose payloads (operation data,
// arguments, schemas) stay JsonElement.

/// <summary>Serializer settings for every bridge result and every MCP tool result.</summary>
public static class BridgeJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            // Web defaults read numbers from strings, which would also make generated schemas accept
            // strings for integers. Results are always written by our own serializer.
            NumberHandling = JsonNumberHandling.Strict,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        options.MakeReadOnly();
        return options;
    }
}

/// <summary>system.get_state</summary>
public sealed record SystemStateResult(
    int BridgeProtocol,
    int ProcessId,
    WorkspaceSnapshot Workspace,
    int OperationCount,
    int RunningOperationCount,
    bool Connected);

/// <summary>Compact registry entry used by search, browse and validate.</summary>
public sealed record OperationSummary(
    string Id,
    string Version,
    string Title,
    string Summary,
    [property: Description("Numeric OperationRisk: 0 ReadOnly, 1 SafeWrite, 2 Destructive, 3 ExternalSideEffect.")] OperationRisk Risk,
    [property: Description("Numeric ExecutionTarget: 0 ArcGISMainCimThread, 1 ArcGISUiThread, 2 Background, 3 ExternalWorker.")] ExecutionTarget ExecutionTarget,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> RequiredCapabilities,
    bool RequiresConfirmation,
    bool ExecutesUserCode,
    string? TypicalDuration)
{
    public static OperationSummary From(OperationDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return new(
            descriptor.Id,
            descriptor.Version,
            descriptor.Title,
            descriptor.Summary,
            descriptor.Risk,
            descriptor.ExecutionTarget,
            [.. descriptor.Tags],
            [.. descriptor.RequiredCapabilities],
            descriptor.RequiresConfirmation,
            descriptor.ExecutesUserCode,
            descriptor.TypicalDuration);
    }
}

/// <summary>One registry.search hit (the method returns an array of these).</summary>
public sealed record RegistrySearchHit(OperationSummary Operation, double Score, IReadOnlyList<string> MatchedTerms);

public sealed record RegistryDomainCount(string Domain, int Count);

/// <summary>
/// registry.browse. Without a domain, <see cref="Total"/> and <see cref="Domains"/> are set; with a
/// domain, <see cref="Domain"/> and <see cref="Operations"/> are set. The other pair is null.
/// </summary>
public sealed record RegistryBrowseResult(
    int? Total,
    IReadOnlyList<RegistryDomainCount>? Domains,
    string? Domain,
    IReadOnlyList<OperationSummary>? Operations);

/// <summary>
/// registry.describe: the full descriptor plus <see cref="ResultSchema"/>, the schema of the
/// registry_invoke result envelope for this operation. <see cref="OutputSchema"/> is the schema of
/// the envelope's <c>data</c> member and is null while the operation does not declare one.
/// </summary>
public sealed record OperationDescription(
    string Id,
    string Version,
    string Title,
    string Summary,
    JsonElement InputSchema,
    JsonElement? OutputSchema,
    [property: Description("Numeric OperationRisk: 0 ReadOnly, 1 SafeWrite, 2 Destructive, 3 ExternalSideEffect.")] OperationRisk Risk,
    [property: Description("Numeric ExecutionTarget: 0 ArcGISMainCimThread, 1 ArcGISUiThread, 2 Background, 3 ExternalWorker.")] ExecutionTarget ExecutionTarget,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> Examples,
    IReadOnlyList<string> RelatedOperations,
    bool RequiresConfirmation,
    bool Undoable,
    string? TypicalDuration,
    bool ExecutesUserCode,
    JsonElement ResultSchema)
{
    public static OperationDescription From(OperationDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return new(
            descriptor.Id,
            descriptor.Version,
            descriptor.Title,
            descriptor.Summary,
            descriptor.InputSchema,
            descriptor.OutputSchema,
            descriptor.Risk,
            descriptor.ExecutionTarget,
            [.. descriptor.RequiredCapabilities],
            [.. descriptor.Tags],
            [.. descriptor.Aliases],
            [.. descriptor.Examples],
            [.. descriptor.RelatedOperations],
            descriptor.RequiresConfirmation,
            descriptor.Undoable,
            descriptor.TypicalDuration,
            descriptor.ExecutesUserCode,
            JsonSchemas.ResultEnvelope(descriptor.OutputSchema));
    }
}

public sealed record ValidationIssue(string Code, string Message);

/// <summary>registry.validate</summary>
public sealed record RegistryValidationResult(
    bool Valid,
    OperationSummary Operation,
    string Revision,
    IReadOnlyList<ValidationIssue> Issues,
    bool RequiresConfirmation);

// registry.invoke returns ArcGISProMCP.Core.Operations.OperationResult unchanged.

/// <summary>
/// approval.request and approval.status. <see cref="WaitNotice"/> is set when an approval.status
/// wait was requested but the host was already holding its maximum number of waits, so the current
/// status was returned immediately and the client should poll; older hosts never send it.
/// </summary>
public sealed record ApprovalStatusResult(
    string RequestId,
    string OperationId,
    string OperationVersion,
    string WorkspaceRevision,
    string Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt,
    string? ConfirmationToken,
    string? Instructions,
    string? WaitNotice = null);

/// <summary>approval.cancel</summary>
public sealed record ApprovalCancelResult(string RequestId, bool Cancelled);

/// <summary>One workflow.list entry (the method returns an array of these).</summary>
public sealed record WorkflowSummary(
    string Id,
    string Version,
    string Title,
    string Summary,
    IReadOnlyList<string> Tags,
    WorkflowRanking? Ranking);

// workflow.get returns ArcGISProMCP.Core.Workflows.WorkflowDefinition unchanged.

/// <summary>workflow.save</summary>
public sealed record WorkflowSaveResult(bool Saved, string Id, string Version);

/// <summary>One step outcome inside <see cref="WorkflowRunResult"/>.</summary>
public sealed record WorkflowStepResult(
    string Step,
    string Operation,
    bool Success,
    string? ErrorCode,
    string? Message,
    string? WorkspaceRevision,
    JsonElement? Data,
    IReadOnlyList<ResourceHandle>? Resources,
    IReadOnlyList<OperationNotice>? Notices)
{
    public static WorkflowStepResult From(string step, string operation, OperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(step, operation, result.Success, result.ErrorCode, result.Message, result.WorkspaceRevision,
            result.Data, [.. result.Resources], [.. result.Notices]);
    }

    public static WorkflowStepResult Skipped(string step, string operation, string errorCode, string message) =>
        new(step, operation, false, errorCode, message, null, null, null, null);
}

/// <summary>
/// workflow.run. <see cref="ErrorCode"/>, <see cref="Message"/>, <see cref="StoppedAtStep"/>,
/// <see cref="StepIndex"/>, <see cref="ExpectedRevision"/> and <see cref="CurrentRevision"/> are
/// set only when the run stopped with <c>workspace_changed</c>.
/// </summary>
public sealed record WorkflowRunResult(
    bool Success,
    string? ErrorCode,
    string? Message,
    string Id,
    string Version,
    string? StoppedAtStep,
    int? StepIndex,
    string? ExpectedRevision,
    string? CurrentRevision,
    IReadOnlyList<WorkflowStepResult> Results,
    string? Revision,
    string? HistoryWarning);

/// <summary>resource.read: <see cref="Data"/> is the base64 payload.</summary>
public sealed record ResourceContent(
    string Uri,
    string MimeType,
    string? Name,
    string Encoding,
    string? Data,
    DateTimeOffset CreatedAt);
