using System.Globalization;
using ArcGISProMCP.AddIn.Bridge;
using ArcGISProMCP.AddIn.Services;
using ArcGISProMCP.Core.Geoprocessing;
using ArcGISProMCP.Core.Workflows;
using ArcGISProMCP.Operations.Services;

namespace ArcGISProMCP.Testing;

internal sealed record FakeHostOptions(
    bool Autonomous = false,
    string? ToolboxRoot = null,
    string? DataRoot = null,
    TimeProvider? TimeProvider = null);

/// <summary>
/// An ArcGIS Pro host without ArcGIS Pro: the add-in's real <see cref="ProBridgeRequestHandler"/>,
/// the production operation descriptors in a real registry and executor (<see cref="FakePro"/>),
/// the real approval queue, workflow library and resource store, over an in-memory project seeded
/// from a <see cref="FakeProScenario"/>. The end-to-end tests call it in-process; FakeHost serves
/// it over the named-pipe bridge.
/// </summary>
internal sealed class FakeHostRuntime : IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly bool _ownsRoot;
    private int _disposed;

    private FakeHostRuntime(FakeProScenario scenario, FakeHostOptions options)
    {
        Scenario = scenario;
        _ownsRoot = options.DataRoot is null;
        Root = options.DataRoot ?? Path.Combine(Path.GetTempPath(), "ArcGISProMCP-FakeHost", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        State = scenario.CreateState();
        Approvals = new FakeHostApprovals(options.TimeProvider) { Autonomous = options.Autonomous };
        Resources = new ProResourceStore(Path.Combine(Root, "resources"));
        Pro = new FakePro(
            State,
            toolboxes: options.ToolboxRoot is null ? FakeToolboxes.Catalog : new ToolboxCatalog(options.ToolboxRoot),
            confirmation: Approvals,
            resources: Resources,
            applicationStopping: _stopping.Token);
        Pro.Geoprocessing.Responder = Respond;
        Workflows = new FileWorkflowLibrary(Path.Combine(Root, "workflows"), Pro.Registry);
        Access = new BridgeAccessState();
        Handler = new ProBridgeRequestHandler(Pro.Registry, Pro.Context, Workflows, Resources, Access, options.TimeProvider);
    }

    public FakeProScenario Scenario { get; }

    public string Root { get; }

    public FakeProState State { get; }

    public FakePro Pro { get; }

    public FakeHostApprovals Approvals { get; }

    public ProResourceStore Resources { get; }

    public FileWorkflowLibrary Workflows { get; }

    public BridgeAccessState Access { get; }

    public ProBridgeRequestHandler Handler { get; }

    /// <summary>Builds the host and saves the scenario's workflows into its library.</summary>
    public static async Task<FakeHostRuntime> StartAsync(FakeProScenario scenario, FakeHostOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var runtime = new FakeHostRuntime(scenario, options ?? new FakeHostOptions());
        try
        {
            foreach (var workflow in scenario.Workflows ?? [])
                await runtime.Workflows.SaveAsync(workflow, cancellationToken).ConfigureAwait(false);
            return runtime;
        }
        catch
        {
            runtime.Dispose();
            throw;
        }
    }

    public InProcessBridgeClient CreateClient() => new(Handler);

    /// <summary>Finds a feature layer by name in any map.</summary>
    public FakeFeatureTable Table(string layerName) =>
        FindLayer(layerName)?.Table ?? throw new KeyNotFoundException($"Feature layer '{layerName}' is not in the scenario.");

    public FakeLayer? FindLayer(string layerName) =>
        State.Maps.SelectMany(map => map.Layers)
            .FirstOrDefault(layer => string.Equals(layer.Name, layerName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Scenario-aware geoprocessing: management.GetCount counts the named layer's rows; any other
    /// tool succeeds and, when outputs go to the map, adds its output (parameter 2) to the active map
    /// as an empty polygon layer.
    /// </summary>
    private GeoprocessingExecutionResult? Respond(FakeGeoprocessingCall call)
    {
        if (string.Equals(call.Tool, "management.GetCount", StringComparison.OrdinalIgnoreCase))
        {
            var input = call.Parameters.Count > 0 ? call.Parameters[0] : string.Empty;
            if (FindLayer(input)?.Table is not { } table)
                return FakeGeoprocessingService.Failed($"ERROR 000732: Input Rows: Dataset {input} does not exist or is not supported");
            var count = table.Rows.Count.ToString(CultureInfo.InvariantCulture);
            return FakeGeoprocessingService.Succeeded() with { ReturnValue = count, Values = [count], ValueTypes = ["GPLong"] };
        }

        State.Calls.Add($"gp.run {call.Tool}");
        var output = call.Parameters.Count > 1 ? call.Parameters[1] : null;
        if (string.IsNullOrWhiteSpace(output) || output == "#") return FakeGeoprocessingService.Succeeded();
        if (call.Flags.AddOutputsToMap)
        {
            var map = State.Maps.FirstOrDefault(candidate => string.Equals(candidate.Name, State.ActiveMapName, StringComparison.Ordinal))
                      ?? State.Maps.FirstOrDefault();
            var name = output.Replace('/', '\\').Split('\\')[^1];
            if (map is not null && map.Layers.All(layer => !string.Equals(layer.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                var table = new FakeFeatureTable { GeometryType = "Polygon" };
                table.Fields.Add(new FeatureFieldInfo("OBJECTID", "Object ID", "OID", false, false, 4));
                table.Fields.Add(new FeatureFieldInfo("Shape", "Shape", "Geometry", true, true, 0));
                map.Layers.Insert(0, new FakeLayer(name, $"CIMPATH=layer/{name.ToLowerInvariant()}.xml") { Table = table });
            }
        }
        return FakeGeoprocessingService.Succeeded() with { ReturnValue = output, Values = [output], ValueTypes = ["DEFeatureClass"] };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _stopping.Cancel();
        Handler.Dispose();
        Workflows.Dispose();
        Approvals.Dispose();
        Resources.Dispose();
        Pro.Dispose();
        _stopping.Dispose();
        if (!_ownsRoot) return;
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
