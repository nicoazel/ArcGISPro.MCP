using System.Text.Json;
using ArcGISProMCP.Core.Execution;
using ArcGISProMCP.Core.Geoprocessing;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Registry;
using ArcGISProMCP.Core.Resources;
using ArcGISProMCP.Operations;
using ArcGISProMCP.Operations.Services;

namespace ArcGISProMCP.Testing;

/// <summary>
/// "Fake Pro": every host-neutral operation from src/ArcGISProMCP.Operations, composed with fake
/// ArcGIS services into a real <see cref="OperationRegistry"/> and <see cref="OperationExecutor"/>.
/// Descriptors are the production ones; only the ArcGIS SDK calls are replaced.
/// </summary>
internal sealed class FakePro : IDisposable
{
    private readonly string _resourceRoot = Path.Combine(Path.GetTempPath(), $"ArcGISProMCP-FakePro-{Guid.NewGuid():N}");
    private readonly bool _ownsResources;

    public FakePro(
        FakeProState? state = null,
        ArcPyExecutionSettings? arcPy = null,
        bool autonomous = false,
        TimeSpan? mapStructuralSettleDelay = null,
        ToolboxCatalog? toolboxes = null,
        IConfirmationValidator? confirmation = null,
        FileResourceStore? resources = null,
        CancellationToken applicationStopping = default)
    {
        State = state ?? new FakeProState();
        Dispatcher = new FakeDispatcher();
        Workspace = new FakeWorkspace(State);
        Confirmation = new FakeConfirmation(autonomous);
        Audit = new FakeAuditLog();
        _ownsResources = resources is null;
        Resources = resources ?? new FileResourceStore(_resourceRoot);
        Views = new FakeViewCaptureService(State);
        Features = new FakeFeatureService(State);
        Geoprocessing = new FakeGeoprocessingService();
        Toolboxes = toolboxes ?? FakeToolboxes.Catalog;
        Services = new ArcGisServices(
            new FakeProjectService(State),
            new FakeMapService(State),
            new FakeLayerService(),
            Views,
            Features,
            Geoprocessing);
        Context = new OperationContext(Dispatcher, Workspace, confirmation ?? Confirmation, Audit, "fake-pro", applicationStopping);
        Registry = FakeProCatalog.CreateRegistry(Services, Resources, Toolboxes, arcPy, mapStructuralSettleDelay ?? TimeSpan.Zero);
        Executor = new OperationExecutor(Registry, Context);
    }

    public FakeProState State { get; }

    public FakeDispatcher Dispatcher { get; }

    public FakeWorkspace Workspace { get; }

    /// <summary>The fixed-token validator; the context uses it unless another validator was supplied.</summary>
    public FakeConfirmation Confirmation { get; }

    public FakeAuditLog Audit { get; }

    public FileResourceStore Resources { get; }

    public FakeViewCaptureService Views { get; }

    public FakeFeatureService Features { get; }

    public FakeGeoprocessingService Geoprocessing { get; }

    public ToolboxCatalog Toolboxes { get; }

    public ArcGisServices Services { get; }

    public OperationContext Context { get; }

    public OperationRegistry Registry { get; }

    public OperationExecutor Executor { get; }

    public IOperation Operation(string id) =>
        Registry.TryGet(id, out var operation) ? operation : throw new KeyNotFoundException($"Operation '{id}' is not in the fake catalog.");

    /// <summary>Runs one operation directly, bypassing executor policy (revisions, confirmation).</summary>
    public Task<OperationResult> RunDirectAsync(string id, string argumentsJson, CancellationToken cancellationToken) =>
        Operation(id).ExecuteAsync(Arguments(argumentsJson), Context, cancellationToken);

    /// <summary>Runs one operation through the real executor with the current revision and a valid approval.</summary>
    public async Task<OperationResult> InvokeApprovedAsync(string id, string argumentsJson, CancellationToken cancellationToken)
    {
        var revision = (await Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false)).Revision;
        return await Executor.ExecuteAsync(
            new OperationRequest(id, Arguments(argumentsJson), revision, FakeConfirmation.ApprovedToken),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs one operation through the real executor with the current revision and no approval token.</summary>
    public async Task<OperationResult> InvokeUnattendedAsync(string id, string argumentsJson, CancellationToken cancellationToken)
    {
        var revision = (await Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false)).Revision;
        return await Executor.ExecuteAsync(new OperationRequest(id, Arguments(argumentsJson), revision), cancellationToken).ConfigureAwait(false);
    }

    public static JsonElement Arguments(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public void Dispose()
    {
        if (!_ownsResources) return;
        Resources.Dispose();
        try
        {
            if (Directory.Exists(_resourceRoot)) Directory.Delete(_resourceRoot, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal static class FakeProCatalog
{
    /// <summary>
    /// The host-neutral operations in the order ProOperationCatalog registers them. ArcPy
    /// operations need explicit settings, as in the add-in.
    /// </summary>
    public static IReadOnlyList<IOperation> CreateOperations(
        ArcGisServices services,
        FileResourceStore resources,
        ToolboxCatalog toolboxes,
        ArcPyExecutionSettings? arcPy = null,
        TimeSpan? mapStructuralSettleDelay = null)
    {
        var operations = new List<IOperation>
        {
            new ProjectGetOperation(),
            new ProjectOpenOperation(services.Project),
            new ProjectSaveOperation(services.Project),
            new MapListOperation(services.Maps),
            new MapEnsureOperation(services.Maps, mapStructuralSettleDelay),
            new MapActivateOperation(services.Maps),
            new MapClearSelectionOperation(services.Maps),
            new LayerListOperation(services.Maps, services.Layers),
            new FeatureLayerDescribeOperation(services.Features),
            new FeatureQueryOperation(services.Features),
            new FeatureSelectOperation(services.Features),
            new FeatureCreateOperation(services.Features),
            new FeatureUpdateOperation(services.Features),
            new FeatureDeleteOperation(services.Features),
            new GeoprocessingSearchOperation(toolboxes),
            new GeoprocessingDescribeOperation(toolboxes),
            new GeoprocessingQueryOperation(toolboxes, services.Geoprocessing),
            new GeoprocessingRunOperation(toolboxes, services.Geoprocessing),
            new ViewCaptureOperation(resources, services.Views),
        };

        if (arcPy is not null)
        {
            operations.Add(new ArcPyInspectScriptOperation(arcPy));
            operations.Add(new ArcPyRunScriptOperation(arcPy));
        }

        return operations;
    }

    public static OperationRegistry CreateRegistry(
        ArcGisServices services,
        FileResourceStore resources,
        ToolboxCatalog toolboxes,
        ArcPyExecutionSettings? arcPy = null,
        TimeSpan? mapStructuralSettleDelay = null)
    {
        var registry = new OperationRegistry();
        foreach (var operation in CreateOperations(services, resources, toolboxes, arcPy, mapStructuralSettleDelay)) registry.Register(operation);
        return registry;
    }
}
