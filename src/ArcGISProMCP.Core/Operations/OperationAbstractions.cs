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
}

public sealed record OperationContext(
    IOperationDispatcher Dispatcher,
    IWorkspaceStateProvider Workspace,
    IConfirmationValidator Confirmation,
    IOperationAuditLog Audit,
    string CorrelationId,
    CancellationToken ApplicationStopping);
