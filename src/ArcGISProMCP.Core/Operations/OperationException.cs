namespace ArcGISProMCP.Core.Operations;

/// <summary>
/// A failure with a stable, caller-actionable error code. <see cref="Execution.OperationExecutor"/>
/// reports <see cref="Code"/> instead of the generic <c>operation_failed</c>.
/// </summary>
public sealed class OperationException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = string.IsNullOrWhiteSpace(code)
        ? throw new ArgumentException("An operation error code is required.", nameof(code))
        : code;

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
}
