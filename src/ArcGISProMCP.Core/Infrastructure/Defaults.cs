using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.Core.Infrastructure;

public sealed class RejectAllConfirmations : IConfirmationValidator
{
    public ValueTask<bool> IsValidAsync(
        string token,
        OperationDescriptor descriptor,
        System.Text.Json.JsonElement arguments,
        Workspaces.WorkspaceSnapshot workspace,
        CancellationToken cancellationToken) => ValueTask.FromResult(false);
}

public sealed class NullAuditLog : IOperationAuditLog
{
    public ValueTask WriteAsync(OperationAuditEvent auditEvent, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
