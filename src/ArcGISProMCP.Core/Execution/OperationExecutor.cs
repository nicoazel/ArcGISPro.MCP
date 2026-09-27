using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.Core.Execution;

public sealed class OperationExecutor(IOperationRegistry registry, OperationContext context)
{
    public async Task<OperationResult> ExecuteAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        var start = DateTimeOffset.UtcNow;
        var startSnapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var correlationId = context.CorrelationId;
        var descriptor = default(OperationDescriptor);
        OperationResult result;

        try
        {
            if (!registry.TryGet(request.OperationId, out var operation))
            {
                return OperationResult.Fail("operation_not_found", $"Unknown operation '{request.OperationId}'.", startSnapshot.Revision);
            }

            descriptor = operation.Descriptor;
            var argumentIssues = OperationArgumentValidator.Validate(request.Arguments, descriptor.InputSchema);
            if (argumentIssues.Count > 0)
            {
                result = OperationResult.Fail(
                    "invalid_arguments",
                    string.Join(" ", argumentIssues.Select(issue => $"{issue.Path}: {issue.Message}")),
                    startSnapshot.Revision);
            }
            else if (!request.DryRun && descriptor.Risk != OperationRisk.ReadOnly &&
                string.IsNullOrWhiteSpace(request.ExpectedWorkspaceRevision))
            {
                result = OperationResult.Fail(
                    "workspace_revision_required",
                    $"Operation '{descriptor.Id}' changes the workspace and requires the revision from system_get_state.",
                    startSnapshot.Revision);
            }
            else if (!request.DryRun && descriptor.Risk != OperationRisk.ReadOnly &&
                !string.Equals(request.ExpectedWorkspaceRevision, startSnapshot.Revision, StringComparison.Ordinal))
            {
                result = OperationResult.Fail(
                    "workspace_revision_mismatch",
                    $"Workspace changed: expected '{request.ExpectedWorkspaceRevision}', current '{startSnapshot.Revision}'. Refresh state before writing.",
                    startSnapshot.Revision);
            }
            else if (!request.DryRun && (descriptor.RequiresConfirmation ||
                descriptor.Risk is OperationRisk.Destructive or OperationRisk.ExternalSideEffect))
            {
                if (context.Confirmation is IAutonomousExecutionPolicy { AllowsUnattendedRiskyOperations: true })
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result = await ExecuteOrDescribeAsync(operation, descriptor, request, startSnapshot.Revision, context, cancellationToken)
                        .ConfigureAwait(false);
                    result = result with
                    {
                        Notices = result.Notices.Add(new OperationNotice(
                            "autonomous_control",
                            "The host's explicit autonomous-control setting bypassed local review for this risky operation.",
                            "warning"))
                    };
                }
                else if (string.IsNullOrWhiteSpace(request.ConfirmationToken) ||
                    !await context.Confirmation.IsValidAsync(
                        request.ConfirmationToken,
                        descriptor,
                        request.Arguments,
                        startSnapshot,
                        cancellationToken).ConfigureAwait(false))
                {
                    result = OperationResult.Fail(
                        "confirmation_required",
                        $"Operation '{descriptor.Id}' requires confirmation bound to these arguments and workspace revision.",
                        startSnapshot.Revision);
                }
                else
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result = await ExecuteOrDescribeAsync(operation, descriptor, request, startSnapshot.Revision, context, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                result = await ExecuteOrDescribeAsync(operation, descriptor, request, startSnapshot.Revision, context, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            result = OperationResult.Fail("operation_failed", exception.Message, startSnapshot.Revision);
        }

        if (descriptor is not null)
        {
            try
            {
                await context.Audit.WriteAsync(new OperationAuditEvent(
                    correlationId,
                    descriptor.Id,
                    descriptor.Version,
                    start,
                    DateTimeOffset.UtcNow,
                    result.Success,
                    startSnapshot.Revision,
                    result.WorkspaceRevision,
                    result.ErrorCode,
                    Hash(request.Arguments)), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Observability failure must not hide the known outcome of an accepted mutation.
                System.Diagnostics.Trace.TraceError("Operation audit write failed: {0}", exception);
                result = result with
                {
                    Notices = result.Notices.Add(new OperationNotice(
                    "audit_write_failed", "Operation result is known, but the audit record could not be persisted. Inspect host diagnostics before further work.", "warning"))
                };
            }
        }

        return result;
    }

    private static async Task<OperationResult> ExecuteOrDescribeAsync(
        IOperation operation,
        OperationDescriptor descriptor,
        OperationRequest request,
        string revision,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        if (!request.DryRun)
        {
            return await operation.ExecuteAsync(request.Arguments, context, cancellationToken).ConfigureAwait(false);
        }

        using var dryRun = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            valid = true,
            operation = descriptor.Id,
            descriptor.Risk,
            descriptor.ExecutionTarget,
            workspaceRevision = revision
        }));
        return OperationResult.Ok(dryRun.RootElement, revision);
    }

    private static string Hash(JsonElement arguments)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(arguments.GetRawText()));
        return Convert.ToHexString(bytes);
    }
}
