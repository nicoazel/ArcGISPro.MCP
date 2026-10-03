using System.Text.Json;
using ArcGISProMCP.Core.Workspaces;

namespace ArcGISProMCP.Core.Operations;

public interface IOperation
{
    OperationDescriptor Descriptor { get; }

    Task<OperationResult> ExecuteAsync(
        JsonElement arguments,
        OperationContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// An operation with a real dry run: static validation of the arguments with no side effects.
/// <see cref="Execution.OperationExecutor"/> calls <see cref="DryRunAsync"/> instead of its generic
/// description when <see cref="OperationRequest.DryRun"/> is set, and never calls
/// <see cref="IOperation.ExecuteAsync"/> for a dry run.
/// </summary>
public interface IDryRunnableOperation
{
    Task<OperationResult> DryRunAsync(
        JsonElement arguments,
        OperationContext context,
        CancellationToken cancellationToken);
}

/// <summary>Why an operation refused a request before executing it.</summary>
public sealed record OperationRefusal(string Code, string Message);

/// <summary>
/// An operation that must not run some requests without local human review. When autonomous
/// mode would skip review, the executor asks the gate first and fails with the refusal instead
/// of executing. A request that carries a confirmation token skips the gate and is validated like
/// an interactive request, so a person can still approve it locally. Interactive mode is unaffected:
/// a person already reviews every request.
/// </summary>
public interface IUnattendedExecutionGate
{
    ValueTask<OperationRefusal?> CheckUnattendedAsync(JsonElement arguments, CancellationToken cancellationToken);
}

/// <summary>
/// An operation that can refuse a request because of the current host state before anything is
/// approved or run. The executor calls <see cref="CheckPreconditionAsync"/> for every non-dry-run
/// request after the revision checks and before it validates (and so consumes) an approval token
/// or applies the autonomous bypass, so a refusal leaves the token approved for a retry once the
/// condition is resolved (as long as the workspace revision has not changed). The operation must
/// still re-check the condition when it runs: the state can change between the two.
/// </summary>
public interface IExecutionPrecondition
{
    ValueTask<OperationRefusal?> CheckPreconditionAsync(
        JsonElement arguments,
        OperationContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// An operation that describes argument-specific risks for the local approval card
/// (for example "modifies input data in place"). Null means no warning.
/// </summary>
public interface IApprovalWarningSource
{
    string? GetApprovalWarning(JsonElement arguments);
}

public interface IOperationRegistry
{
    IReadOnlyCollection<OperationDescriptor> Descriptors { get; }

    void Register(IOperation operation);

    bool TryGet(string id, out IOperation operation);

    IReadOnlyList<SearchHit> Search(OperationQuery query);
}

public interface IOperationDispatcher
{
    Task<T> OnMainCimThreadAsync<T>(Func<T> action, CancellationToken cancellationToken);

    Task OnMainCimThreadAsync(Action action, CancellationToken cancellationToken);

    Task<T> OnUiThreadAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken);
}

public interface IConfirmationValidator
{
    ValueTask<bool> IsValidAsync(
        string token,
        OperationDescriptor descriptor,
        JsonElement arguments,
        WorkspaceSnapshot workspace,
        CancellationToken cancellationToken);
}

public interface IAutonomousExecutionPolicy
{
    bool AllowsUnattendedRiskyOperations { get; }
}

public interface IOperationAuditLog
{
    ValueTask WriteAsync(OperationAuditEvent auditEvent, CancellationToken cancellationToken);
}

public sealed record OperationAuditEvent(
    string CorrelationId,
    string OperationId,
    string OperationVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    bool Success,
    string StartRevision,
    string EndRevision,
    string? ErrorCode,
    string ArgumentsHash,
    string Kind = OperationAuditKinds.Operation,
    bool AutonomousBypass = false,
    string? Decision = null,
    string? Actor = null);

/// <summary>Audit record kinds written to the operation audit log.</summary>
public static class OperationAuditKinds
{
    /// <summary>An operation invocation (including rejected and unknown operation ids).</summary>
    public const string Operation = "operation";

    /// <summary>A local approval decision; <see cref="OperationAuditEvent.Decision"/> is "approved" or "denied".</summary>
    public const string Approval = "approval";

    /// <summary>A dry run: validation only, nothing was executed.</summary>
    public const string DryRun = "dry-run";
}

public sealed record OperationContext(
    IOperationDispatcher Dispatcher,
    IWorkspaceStateProvider Workspace,
    IConfirmationValidator Confirmation,
    IOperationAuditLog Audit,
    string CorrelationId,
    CancellationToken ApplicationStopping);
