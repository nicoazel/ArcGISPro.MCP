using System.Text.Json;
using ArcGISProMCP.Core.Execution;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Registry;
using ArcGISProMCP.Core.Resources;
using ArcGISProMCP.Operations.Services;

namespace ArcGISProMCP.Operations.Tests.Fakes;

/// <summary>
/// "Fake Pro": every host-neutral operation from src/ArcGISProMCP.Operations, composed with fake
/// ArcGIS services into a real <see cref="OperationRegistry"/> and <see cref="OperationExecutor"/>.
/// Descriptors are the production ones; only the ArcGIS SDK calls are replaced.
/// </summary>
internal sealed class FakePro : IDisposable
{
    private readonly string _resourceRoot = Path.Combine(Path.GetTempPath(), $"ArcGISProMCP-FakePro-{Guid.NewGuid():N}");

    public FakePro(
        FakeProState? state = null,
        ArcPyExecutionSettings? arcPy = null,
        bool autonomous = false,
        TimeSpan? mapStructuralSettleDelay = null)
    {
        State = state ?? new FakeProState();
        Dispatcher = new FakeDispatcher();
        Workspace = new FakeWorkspace(State);
        Confirmation = new FakeConfirmation(autonomous);
        Audit = new FakeAuditLog();
        Resources = new FileResourceStore(_resourceRoot);
        Views = new FakeViewCaptureService(State);
        Services = new ArcGisServices(
            new FakeProjectService(State),
            new FakeMapService(State),
            new FakeLayerService(),
            Views);
        Context = new OperationContext(Dispatcher, Workspace, Confirmation, Audit, "fake-pro", CancellationToken.None);
        Registry = FakeProCatalog.CreateRegistry(Services, Resources, arcPy, mapStructuralSettleDelay ?? TimeSpan.Zero);
        Executor = new OperationExecutor(Registry, Context);
    }

    public FakeProState State { get; }

    public FakeDispatcher Dispatcher { get; }

    public FakeWorkspace Workspace { get; }

    public FakeConfirmation Confirmation { get; }

    public FakeAuditLog Audit { get; }

    public FileResourceStore Resources { get; }

    public FakeViewCaptureService Views { get; }

    public ArcGisServices Services { get; }

    public OperationContext Context { get; }

    public OperationRegistry Registry { get; }

    public OperationExecutor Executor { get; }

    public IOperation Operation(string id) =>
        Registry.TryGet(id, out var operation) ? operation : throw new KeyNotFoundException($"Operation '{id}' is not in the fake catalog.");

    /// <summary>Runs one operation directly, bypassing executor policy (revisions, confirmation).</summary>
    public Task<OperationResult> RunAsync(string id, string argumentsJson = "{}") =>
        Operation(id).ExecuteAsync(Arguments(argumentsJson), Context, TestContext.Current.CancellationToken);

    /// <summary>Runs one operation through the real executor with the current revision and a valid approval.</summary>
    public async Task<OperationResult> InvokeAsync(string id, string argumentsJson = "{}")
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var revision = (await Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false)).Revision;
        return await Executor.ExecuteAsync(
            new OperationRequest(id, Arguments(argumentsJson), revision, FakeConfirmation.ApprovedToken),
            cancellationToken).ConfigureAwait(false);
    }

    public static JsonElement Arguments(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public void Dispose()
    {
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
        ArcPyExecutionSettings? arcPy = null,
        TimeSpan? mapStructuralSettleDelay = null)
    {
        var registry = new OperationRegistry();
        foreach (var operation in CreateOperations(services, resources, arcPy, mapStructuralSettleDelay)) registry.Register(operation);
        return registry;
    }
}
