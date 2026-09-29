using ArcGISProMCP.Core.Geoprocessing;
using ArcGISProMCP.Operations.Services;

namespace ArcGISProMCP.Testing;

internal sealed record FakeGeoprocessingCall(
    string Tool,
    IReadOnlyList<string> Parameters,
    IReadOnlyList<KeyValuePair<string, string>> Environments,
    GeoprocessingExecutionFlags Flags);

/// <summary>
/// Records every tool execution and answers with <see cref="Responder"/>'s result, or
/// <see cref="NextResult"/> when there is no responder or it returns null.
/// </summary>
internal sealed class FakeGeoprocessingService : IGeoprocessingService
{
    public List<FakeGeoprocessingCall> Calls { get; } = [];

    public GeoprocessingExecutionResult NextResult { get; set; } = Succeeded();

    public Func<FakeGeoprocessingCall, GeoprocessingExecutionResult?>? Responder { get; set; }

    public Task<GeoprocessingExecutionResult> ExecuteAsync(
        string tool,
        IReadOnlyList<string> parameters,
        IReadOnlyList<KeyValuePair<string, string>> environments,
        GeoprocessingExecutionFlags flags,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var call = new FakeGeoprocessingCall(tool, parameters.ToArray(), environments.ToArray(), flags);
        Calls.Add(call);
        return Task.FromResult(Responder?.Invoke(call) ?? NextResult);
    }

    public static GeoprocessingExecutionResult Succeeded(params GeoprocessingMessage[] messages) =>
        new(false, false, 0, "memory/result", ["memory/result"], ["DEFeatureClass"],
            messages.Length == 0 ? [new GeoprocessingMessage("Informative", "Succeeded.", 0)] : messages, null);

    public static GeoprocessingExecutionResult Failed(string error) =>
        new(true, false, 1, null, null, null, [new GeoprocessingMessage("Error", error, 999999)], error);
}

internal static class FakeToolboxes
{
    /// <summary>The synthetic system toolboxes shared with Core.Tests (fixture.* and management.*).</summary>
    public static ToolboxCatalog Catalog { get; } = new(Path.Combine(AppContext.BaseDirectory, "Fixtures", "toolboxes"));
}
