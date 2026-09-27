using ArcGISProMCP.Core.Geoprocessing;
using ArcGISProMCP.Operations.Services;

namespace ArcGISProMCP.Operations.Tests.Fakes;

internal sealed record FakeGeoprocessingCall(
    string Tool,
    IReadOnlyList<string> Parameters,
    IReadOnlyList<KeyValuePair<string, string>> Environments,
    GeoprocessingExecutionFlags Flags);

/// <summary>Records every tool execution and answers with <see cref="NextResult"/>.</summary>
internal sealed class FakeGeoprocessingService : IGeoprocessingService
{
    public List<FakeGeoprocessingCall> Calls { get; } = [];

    public GeoprocessingExecutionResult NextResult { get; set; } = Succeeded();

    public Task<GeoprocessingExecutionResult> ExecuteAsync(
        string tool,
        IReadOnlyList<string> parameters,
        IReadOnlyList<KeyValuePair<string, string>> environments,
        GeoprocessingExecutionFlags flags,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(new FakeGeoprocessingCall(tool, parameters.ToArray(), environments.ToArray(), flags));
        return Task.FromResult(NextResult);
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
