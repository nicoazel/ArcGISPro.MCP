using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGISProMCP.Operations.Services;

namespace ArcGISProMCP.AddIn.ArcGIS.Services;

internal sealed class ProGeoprocessingService : IGeoprocessingService
{
    public async Task<GeoprocessingExecutionResult> ExecuteAsync(
        string tool,
        IReadOnlyList<string> parameters,
        IReadOnlyList<KeyValuePair<string, string>> environments,
        GeoprocessingExecutionFlags flags,
        CancellationToken cancellationToken)
    {
        var executeFlags = GPExecuteToolFlags.GPThread;
        if (flags.AddToHistory) executeFlags |= GPExecuteToolFlags.AddToHistory;
        if (flags.RefreshProjectItems) executeFlags |= GPExecuteToolFlags.RefreshProjectItems;
        if (flags.AddOutputsToMap) executeFlags |= GPExecuteToolFlags.AddOutputsToMap;

        var result = await Geoprocessing.ExecuteToolAsync(
            tool,
            parameters,
            environments,
            cancellationToken,
            null,
            executeFlags).ConfigureAwait(false);
        return new GeoprocessingExecutionResult(
            result.IsFailed,
            result.IsCanceled,
            result.ErrorCode,
            result.ReturnValue,
            result.Values?.ToArray(),
            result.ValueTypes?.ToArray(),
            result.Messages.Select(message => new GeoprocessingMessage(message.Type.ToString(), message.Text, message.ErrorCode)).ToArray(),
            result.ErrorMessages.FirstOrDefault()?.Text);
    }
}
