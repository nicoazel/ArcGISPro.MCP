using System.Diagnostics.CodeAnalysis;
using System.IO;
using ArcGISProMCP.AddIn.ArcGIS;
using ArcGISProMCP.AddIn.Bridge;
using ArcGISProMCP.AddIn.Operations;
using ArcGISProMCP.AddIn.Services;
using ArcGISProMCP.AddIn.UI;
using ArcGISProMCP.Bridge.Transport;
using ArcGISProMCP.Core.Infrastructure;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Registry;
using ArcGISProMCP.Core.Workflows;

namespace ArcGISProMCP.AddIn;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "ArcGIS Pro owns Module lifetime and calls Uninitialize for cleanup.")]
internal sealed class ArcGISProMcpModule : global::ArcGIS.Desktop.Framework.Contracts.Module
{
    private readonly CancellationTokenSource _applicationStopping = new();
    private NamedPipeBridgeServer? _bridge;
    private FileWorkflowLibrary? _workflows;
    private JsonLineAuditLog? _audit;

    internal static ArcGISProMcpModule? Instance { get; private set; }
    internal IOperationRegistry? Registry { get; private set; }

    protected override bool Initialize()
    {
        try
        {
            return InitializeCore();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("ArcGIS Pro MCP initialization failed: {0}", exception);
            try
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArcGISProMCP");
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "startup-error.log"), exception.ToString());
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return false;
        }
    }

    private bool InitializeCore()
    {
        Instance = this;
        var registry = new OperationRegistry();
        var dispatcher = new ProDispatcher();
        var workspace = new ProWorkspaceStateProvider(dispatcher);
        var appRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArcGISProMCP");
        var resources = new FileResourceStore(Path.Combine(appRoot, "resources"));
        _workflows = new FileWorkflowLibrary(Path.Combine(appRoot, "workflows"), registry);
        _audit = new JsonLineAuditLog(Path.Combine(appRoot, "audit", "operations.jsonl"));

        foreach (var operation in ProOperationCatalog.Create(resources)) registry.Register(operation);
        Registry = registry;
        var context = new OperationContext(
            dispatcher,
            workspace,
            new LocalWpfConfirmationValidator(dispatcher, workspace),
            _audit,
            "startup",
            _applicationStopping.Token);
        var access = new BridgeAccessState();
        _bridge = new NamedPipeBridgeServer(new ProBridgeRequestHandler(registry, context, _workflows, resources, access));
        _bridge.Start();
        PanelStateSourceProvider.Factory = () => new ProPanelStateSource(registry, context, _workflows, resources, access);
        return true;
    }

    protected override bool CanUnload() => true;

    protected override void Uninitialize()
    {
        _applicationStopping.Cancel();
        if (_bridge is not null)
        {
            try
            {
                _bridge.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }
        _workflows?.Dispose();
        _audit?.Dispose();
        Registry = null;
        PanelStateSourceProvider.Factory = static () => new UnavailablePanelStateSource();
        Instance = null;
        _applicationStopping.Dispose();
        base.Uninitialize();
    }
}
