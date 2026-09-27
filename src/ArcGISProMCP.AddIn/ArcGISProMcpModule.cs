using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using ArcGIS.Desktop.Core;
using ArcGISProMCP.AddIn.ArcGIS;
using ArcGISProMCP.AddIn.Bridge;
using ArcGISProMCP.AddIn.Operations;
using ArcGISProMCP.AddIn.Services;
using ArcGISProMCP.AddIn.UI;
using ArcGISProMCP.Bridge.Protocol;
using ArcGISProMCP.Bridge.Transport;
using ArcGISProMCP.Core.Approvals;
using ArcGISProMCP.Core.Infrastructure;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Registry;
using ArcGISProMCP.Core.Workflows;

namespace ArcGISProMCP.AddIn;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "ArcGIS Pro owns Module lifetime and calls Uninitialize for cleanup.")]
internal sealed class ArcGISProMcpModule : global::ArcGIS.Desktop.Framework.Contracts.Module
{
    private readonly CancellationTokenSource _applicationStopping = new();
    private readonly object _hostDiscoveryGate = new();
    private readonly DateTimeOffset _processStartedAtUtc = CurrentProcessStartedAtUtc();
    private NamedPipeBridgeServer? _bridge;
    private ProBridgeRequestHandler? _handler;
    private IApprovalService? _approvals;
    private ProWorkspaceEventMonitor? _workspaceEvents;
    private ProResourceStore? _resources;
    private FileWorkflowLibrary? _workflows;
    private JsonLineAuditLog? _audit;
    private Task? _cleanupTask;
    private bool _hasPublishedHostDiscovery;
    private string? _publishedProjectName;
    private string? _publishedProjectUri;

    internal static ArcGISProMcpModule? Instance { get; private set; }
    internal IOperationRegistry? Registry { get; private set; }
    internal string? PipeName { get; private set; }

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
            _applicationStopping.Cancel();
            BeginCleanup();
            Registry = null;
            PanelStateSourceProvider.Factory = static () => new UnavailablePanelStateSource();
            Instance = null;
            return false;
        }
    }

    private bool InitializeCore()
    {
        Instance = this;
        var registry = new OperationRegistry();
        var dispatcher = new ProDispatcher();
        var workspace = new ProWorkspaceStateProvider(dispatcher);
        _workspaceEvents = new ProWorkspaceEventMonitor(workspace);
        var appRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArcGISProMCP");
        var resources = _resources = new ProResourceStore(Path.Combine(appRoot, "resources"));
        var workflows = _workflows = new FileWorkflowLibrary(Path.Combine(appRoot, "workflows"), registry);
        var audit = _audit = new JsonLineAuditLog(Path.Combine(appRoot, "audit", "operations.jsonl"));
        var approvals = _approvals = new LocalApprovalService();

        foreach (var operation in ProOperationCatalog.Create(resources)) registry.Register(operation);
        SeedBundledWorkflows(workflows);
        Registry = registry;
        var context = new OperationContext(
            dispatcher,
            workspace,
            approvals,
            audit,
            "startup",
            _applicationStopping.Token);
        var access = new BridgeAccessState();
        var handler = _handler = new ProBridgeRequestHandler(registry, context, workflows, resources, access);
        var configuredPipe = Environment.GetEnvironmentVariable("ARCGIS_PRO_MCP_PIPE");
        var pipeName = string.IsNullOrWhiteSpace(configuredPipe)
            ? BridgeHostDiscovery.ProcessPipeName(Environment.ProcessId)
            : configuredPipe.Trim();
        PipeName = pipeName;
        _bridge = new NamedPipeBridgeServer(handler, pipeName);
        _bridge.Start();
        RefreshHostDiscovery(Project.Current?.Name, Project.Current?.URI);
        PanelStateSourceProvider.Factory = () => new ProPanelStateSource(context, handler, workflows, resources, access);
        return true;
    }

    // Runs after operation registration because workflow validation rejects unknown operation ids.
    // Every await in the library and seeder, including async disposal, uses ConfigureAwait(false),
    // so a synchronous wait cannot deadlock.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Workflow seeding must never block ArcGIS Pro startup.")]
    private static void SeedBundledWorkflows(IWorkflowLibrary workflows)
    {
        try
        {
            var report = WorkflowSeeder.SeedAsync(workflows, CancellationToken.None).GetAwaiter().GetResult();
            foreach (var item in report.Skipped)
            {
                Trace.TraceWarning(
                    "ArcGIS Pro MCP skipped bundled workflow {0} ({1}@{2}): {3} {4}",
                    item.ResourceName, item.WorkflowId, item.Version, item.Outcome, item.Message);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError("ArcGIS Pro MCP workflow seeding failed: {0}", exception);
        }
    }

    internal void RefreshHostDiscovery(string? projectName, string? projectUri)
    {
        lock (_hostDiscoveryGate)
        {
            if (PipeName is null ||
                _hasPublishedHostDiscovery &&
                string.Equals(projectName, _publishedProjectName, StringComparison.Ordinal) &&
                string.Equals(projectUri, _publishedProjectUri, StringComparison.Ordinal))
                return;

            BridgeHostDiscovery.Publish(new BridgeHostRecord(
                Environment.ProcessId,
                PipeName,
                _processStartedAtUtc,
                DateTimeOffset.UtcNow,
                projectName,
                projectUri));
            _hasPublishedHostDiscovery = true;
            _publishedProjectName = projectName;
            _publishedProjectUri = projectUri;
        }
    }

    protected override bool CanUnload() => _handler?.RunningOperationCount is not > 0;

    protected override void Uninitialize()
    {
        _applicationStopping.Cancel();
        BeginCleanup();
        Registry = null;
        PanelStateSourceProvider.Factory = static () => new UnavailablePanelStateSource();
        Instance = null;
        base.Uninitialize();
    }

    private void BeginCleanup()
    {
        if (_cleanupTask is not null) return;
        var bridge = _bridge;
        var handler = _handler;
        var approvals = _approvals;
        var workspaceEvents = _workspaceEvents;
        var resources = _resources;
        var workflows = _workflows;
        var audit = _audit;
        _bridge = null;
        _handler = null;
        _approvals = null;
        _workspaceEvents = null;
        _resources = null;
        _workflows = null;
        _audit = null;
        BridgeHostDiscovery.Remove(Environment.ProcessId);
        PipeName = null;
        _cleanupTask = DrainAndDisposeAsync(
            bridge,
            handler,
            approvals,
            workspaceEvents,
            resources,
            workflows,
            audit,
            _applicationStopping);
    }

    private static async Task DrainAndDisposeAsync(
        NamedPipeBridgeServer? bridge,
        ProBridgeRequestHandler? handler,
        IApprovalService? approvals,
        ProWorkspaceEventMonitor? workspaceEvents,
        ProResourceStore? resources,
        FileWorkflowLibrary? workflows,
        JsonLineAuditLog? audit,
        CancellationTokenSource applicationStopping)
    {
        try
        {
            if (bridge is not null) await bridge.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("ArcGIS Pro MCP shutdown drain failed: {0}", exception);
        }
        finally
        {
            handler?.Dispose();
            workspaceEvents?.Dispose();
            approvals?.Dispose();
            resources?.Dispose();
            workflows?.Dispose();
            audit?.Dispose();
            applicationStopping.Dispose();
        }
    }

    private static DateTimeOffset CurrentProcessStartedAtUtc()
    {
        using var process = Process.GetCurrentProcess();
        return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
    }
}
