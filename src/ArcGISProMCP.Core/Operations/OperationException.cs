namespace ArcGISProMCP.Core.Operations;

/// <summary>
/// A failure with a stable, caller-actionable error code. <see cref="Execution.OperationExecutor"/>
/// reports <see cref="Code"/> instead of the generic <c>operation_failed</c>. Codes the executor,
/// bridge or gateway report themselves (<see cref="OperationErrorCodes.Reserved"/>) are rejected, so
/// an operation cannot impersonate a revision, confirmation or argument failure.
/// </summary>
public sealed class OperationException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = ValidateCode(code);

    private static string ValidateCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("An operation error code is required.", nameof(code));
        if (OperationErrorCodes.Reserved.Contains(code))
            throw new ArgumentException($"'{code}' is reserved for the executor and transport; operations must use their own code.", nameof(code));
        return code;
    }

    /// <summary>The standard failure for a layer whose data source is broken, missing, or cannot be opened.</summary>
    public static OperationException LayerDataSourceUnavailable(string layerName, Exception? innerException = null) =>
        new(
            OperationErrorCodes.LayerDataSourceUnavailable,
            $"The data source of layer '{layerName}' is unavailable (broken or missing). " +
            "Repair its data source in ArcGIS Pro, or re-add it with layer.add using the same name and a valid source.",
            innerException);
}

/// <summary>Stable error codes raised through <see cref="OperationException"/>.</summary>
public static class OperationErrorCodes
{
    /// <summary>A layer's data source is broken, missing, or cannot be opened.</summary>
    public const string LayerDataSourceUnavailable = "layer_data_source_unavailable";

    /// <summary>
    /// Codes reported by the executor, workflows, the bridge or the gateway themselves. Clients act on
    /// them (refresh the revision, request approval, fix arguments), so an operation may not raise them
    /// through <see cref="OperationException"/>.
    /// </summary>
    public static IReadOnlySet<string> Reserved { get; } = new HashSet<string>(
        [
            "audit_write_failed",
            "bridge_request_failed",
            "confirmation_required",
            "dry_run_idempotency_conflict",
            "host_stopping",
            "idempotency_capacity",
            "idempotency_conflict",
            "invalid_arguments",
            "invalid_idempotency_key",
            "invalid_workflow",
            "operation_failed",
            "operation_not_found",
            "outcome_unknown",
            "request_cancelled",
            "workflow_step_failed",
            "workspace_changed",
            "workspace_revision_mismatch",
            "workspace_revision_required",
        ],
        StringComparer.Ordinal);
}
