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
    string ArgumentsHash);

public sealed record OperationContext(
    IOperationDispatcher Dispatcher,
    IWorkspaceStateProvider Workspace,
    IConfirmationValidator Confirmation,
    IOperationAuditLog Audit,
    string CorrelationId,
    CancellationToken ApplicationStopping);
