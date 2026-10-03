namespace ArcGISProMCP.Core.Operations;

/// <summary>
/// A failure with a stable, caller-actionable error code. <see cref="Execution.OperationExecutor"/>
/// reports <see cref="Code"/> instead of the generic <c>operation_failed</c>. Codes the executor,
/// bridge or gateway report themselves (<see cref="OperationErrorCodes.Reserved"/>) are rejected, so
/// an operation cannot impersonate a revision, confirmation or argument failure. The one exception
/// is <see cref="InvalidArgument"/>: an operation may report <c>invalid_arguments</c> for an argument
/// value that passed the schema but names nothing the project or ArcGIS Pro accepts.
/// </summary>
public sealed class OperationException : Exception
{
    public OperationException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = ValidateCode(code);
    }

    private OperationException(string message, Exception? innerException, bool argumentFailure)
        : base(message, innerException)
    {
        // Only InvalidArgument reaches this constructor; it reports the executor's own argument code.
        _ = argumentFailure;
        Code = OperationErrorCodes.InvalidArguments;
    }

    public string Code { get; }

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

    /// <summary>No map in the project matches the requested handle or name (or the project has no map).</summary>
    public static OperationException MapNotFound(string? reference) =>
        new(
            OperationErrorCodes.MapNotFound,
            string.IsNullOrWhiteSpace(reference) ? "No map is available." : $"Map '{reference}' was not found.");

    /// <summary>No layer in the map matches the requested handle or name.</summary>
    public static OperationException LayerNotFound(string reference, string mapName) =>
        new(OperationErrorCodes.LayerNotFound, $"Layer '{reference}' was not found in map '{mapName}'.");

    /// <summary>No layout in the project matches the requested handle or name.</summary>
    public static OperationException LayoutNotFound(string reference) =>
        new(OperationErrorCodes.LayoutNotFound, $"Layout '{reference}' was not found.");

    /// <summary>No map frame on the layout has the requested name.</summary>
    public static OperationException FrameNotFound(string frameName, string layoutName) =>
        new(OperationErrorCodes.FrameNotFound, $"Map frame '{frameName}' was not found on layout '{layoutName}'.");

    /// <summary>
    /// An argument value that passed the input schema but that the project or ArcGIS Pro does not
    /// accept (an unknown basemap name, a layer of the wrong geometry type). Raise it only before
    /// anything was changed: clients treat <c>invalid_arguments</c> as "nothing ran, fix the call".
    /// The message must name the argument value and never carry a local path or exception type; a
    /// parse failure behind it can be kept as <paramref name="innerException"/> for the audit trail.
    /// </summary>
    public static OperationException InvalidArgument(string message, Exception? innerException = null) =>
        new(message, innerException, argumentFailure: true);
}

/// <summary>Stable error codes raised through <see cref="OperationException"/>.</summary>
public static class OperationErrorCodes
{
    /// <summary>A layer's data source is broken, missing, or cannot be opened.</summary>
    public const string LayerDataSourceUnavailable = "layer_data_source_unavailable";

    /// <summary>The requested map does not exist in the open project.</summary>
    public const string MapNotFound = "map_not_found";

    /// <summary>The requested layer does not exist in the resolved map.</summary>
    public const string LayerNotFound = "layer_not_found";

    /// <summary>The requested layout does not exist in the open project.</summary>
    public const string LayoutNotFound = "layout_not_found";

    /// <summary>The requested map frame does not exist on the resolved layout.</summary>
    public const string FrameNotFound = "frame_not_found";

    /// <summary>A requested layout element would lie (partly) outside the layout page.</summary>
    public const string ElementOutsidePage = "element_outside_page";

    /// <summary>
    /// Arguments that do not match the schema, or (through <see cref="OperationException.InvalidArgument"/>)
    /// an argument value nothing in the project accepts. Reserved: only that factory may raise it.
    /// </summary>
    public const string InvalidArguments = "invalid_arguments";

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
            InvalidArguments,
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
