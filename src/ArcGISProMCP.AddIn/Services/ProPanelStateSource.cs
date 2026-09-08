using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using ArcGISProMCP.AddIn.UI;
using ArcGISProMCP.Bridge.Protocol;
using ArcGISProMCP.Core.Execution;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Workflows;
using Microsoft.Win32;

namespace ArcGISProMCP.AddIn.Services;

internal sealed class ProPanelStateSource(
    IOperationRegistry registry,
    OperationContext context,
    IWorkflowLibrary workflows,
    FileResourceStore resources,
    BridgeAccessState access) : IPanelStateSource
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly List<ActivitySnapshot> _activity = [];
    private PanelStateSnapshot _current = PanelStateSnapshot.Unavailable;
    private bool _disposed;

    public event EventHandler<PanelStateSnapshot>? StateChanged;

    public PanelStateSnapshot Current
    {
        get
        {
            lock (_stateGate) return _current;
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        access.Enabled = true;
        AddActivity(ActivityLevel.Success, "Connected", "The local same-user MCP bridge is ready.");
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        access.Enabled = false;
        Publish(Current with
        {
            Connection = Connection(ConnectionStatus.Disconnected, "Disconnected", false)
        });
        return Task.CompletedTask;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var workspace = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var definitions = await workflows.ListAsync(cancellationToken).ConfigureAwait(false);
            var mapChoices = workspace.Maps.Select(map => new PanelChoice(map.Id, map.Name)).ToArray();
            var layoutChoices = workspace.Layouts.Select(layout => new PanelChoice(layout.Id, layout.Name)).ToArray();
            var workflowChoices = definitions.Select(definition => new WorkflowSnapshot(
                definition.Id,
                definition.Title,
                definition.Summary,
                definition.Parameters.All(parameter => !parameter.Required || parameter.DefaultValue is not null))).ToArray();
            var projectName = workspace.Project.IsOpen ? workspace.Project.Name ?? "Untitled project" : "No ArcGIS Pro project";
            var shortRevision = workspace.Revision.Length > 8 ? workspace.Revision[..8] : workspace.Revision;

            Publish(Current with
            {
                Connection = Connection(
                    access.Enabled ? ConnectionStatus.Ready : ConnectionStatus.Disconnected,
                    access.Enabled ? "Ready" : "Disconnected",
                    false,
                    $"PID {Environment.ProcessId} • revision {shortRevision}"),
                Workspace = new UI.WorkspaceSnapshot(
                    projectName,
                    mapChoices,
                    workspace.ActiveMapId,
                    layoutChoices,
                    workspace.Layouts.FirstOrDefault(layout => layout.IsOpen)?.Id,
                    $"{workspace.Maps.Length} maps • {workspace.Layouts.Length} layouts • revision {shortRevision}"),
                Workflows = workflowChoices,
                SkillCount = 1,
                Activity = _activity.ToArray(),
                FooterStatus = "Visual evidence is captured only on request",
                CanCancelOperation = false
            });
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task OpenProjectAsync(CancellationToken cancellationToken)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "ArcGIS Pro projects (*.aprx)|*.aprx",
            CheckFileExists = true,
            Multiselect = false,
            Title = "Open ArcGIS Pro project"
        };
        if (dialog.ShowDialog() != true) return;
        await RunOperationAsync("project.open", new { path = dialog.FileName }, cancellationToken).ConfigureAwait(false);
    }

    public Task ActivateMapAsync(string mapId, CancellationToken cancellationToken) =>
        RunOperationAsync("map.activate", new { map = mapId }, cancellationToken);

    public Task ActivateLayoutAsync(string layoutId, CancellationToken cancellationToken) =>
        RunOperationAsync("layout.activate", new { layout = layoutId }, cancellationToken);

    public Task ResolveApprovalAsync(string approvalId, ApprovalDecision decision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AddActivity(ActivityLevel.Information, "No pending approval", "No risky operation is waiting for a decision.");
        return Task.CompletedTask;
    }

    public async Task CaptureVisualAsync(CancellationToken cancellationToken)
    {
        var workspace = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var activeLayout = workspace.Layouts.FirstOrDefault(layout => layout.IsOpen);
        var arguments = activeLayout is null
            ? JsonSerializer.SerializeToElement(new { view = "active-map", width = 1280, height = 800 })
            : JsonSerializer.SerializeToElement(new { view = "layout", layout = activeLayout.Id, width = 1600, height = 1000 });
        var result = await ExecuteAsync("view.capture", arguments, workspace.Revision, cancellationToken).ConfigureAwait(false);
        var resource = result.Resources.FirstOrDefault(handle => handle.MimeType == "image/png");
        if (resource is null || !resources.TryGetLocalPath(resource.Uri, out var path))
            throw new InvalidOperationException("The visual capture did not return a readable PNG resource.");

        Publish(Current with
        {
            VisualEvidence = new VisualEvidenceSnapshot(
                path,
                activeLayout?.Name ?? workspace.Maps.FirstOrDefault(map => map.IsActive)?.Name ?? "Active map",
                DateTimeOffset.Now.ToString("t", CultureInfo.CurrentCulture),
                resource.Uri,
                false,
                false)
        });
    }

    public Task RevealVisualAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Current.VisualEvidence.ImagePath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        return Task.CompletedTask;
    }

    public Task ClearVisualCueAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Publish(Current with { VisualEvidence = Current.VisualEvidence with { HasActiveCue = false } });
        return Task.CompletedTask;
    }

    public Task SaveWorkflowAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AddActivity(ActivityLevel.Information, "Workflow library", "Save new immutable workflows through the workflow_save MCP tool.");
        return Task.CompletedTask;
    }

    public async Task ReplayWorkflowAsync(string workflowId, CancellationToken cancellationToken)
    {
        var workflow = await workflows.GetAsync(workflowId, null, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Workflow '{workflowId}' was not found.");
        var bound = WorkflowBinder.BindParameters(workflow, JsonSerializer.SerializeToElement(new { }));
        var workspace = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var revision = workspace.Revision;
        foreach (var step in workflow.Steps)
        {
            var result = await ExecuteAsync(
                step.Operation,
                WorkflowBinder.ResolveArguments(step.Arguments, bound),
                revision,
                cancellationToken).ConfigureAwait(false);
            revision = result.WorkspaceRevision;
            if (!result.Success && !step.ContinueOnError)
                throw new InvalidOperationException(result.Message ?? $"Workflow step '{step.Id}' failed.");
        }
        AddActivity(ActivityLevel.Success, "Workflow completed", workflow.Title);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task CancelCurrentOperationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task OpenSettingsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArcGISProMCP");
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    private async Task RunOperationAsync(string id, object arguments, CancellationToken cancellationToken)
    {
        var workspace = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var result = await ExecuteAsync(id, JsonSerializer.SerializeToElement(arguments), workspace.Revision, cancellationToken).ConfigureAwait(false);
        if (!result.Success) throw new InvalidOperationException(result.Message ?? result.ErrorCode ?? $"Operation '{id}' failed.");
        AddActivity(ActivityLevel.Success, id, result.Message ?? "Completed");
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<OperationResult> ExecuteAsync(
        string id,
        JsonElement arguments,
        string revision,
        CancellationToken cancellationToken) =>
        new OperationExecutor(registry, context with { CorrelationId = Guid.NewGuid().ToString("N") })
            .ExecuteAsync(new OperationRequest(id, arguments, revision), cancellationToken);

    private void AddActivity(ActivityLevel level, string message, string detail)
    {
        lock (_stateGate)
        {
            _activity.Insert(0, new ActivitySnapshot(
                Guid.NewGuid().ToString("N"),
                level,
                level == ActivityLevel.Success ? "✓" : "i",
                message,
                detail,
                DateTimeOffset.Now.ToString("t", CultureInfo.CurrentCulture),
                false));
            if (_activity.Count > 50) _activity.RemoveRange(50, _activity.Count - 50);
            _current = _current with { Activity = _activity.ToArray() };
        }
        StateChanged?.Invoke(this, Current);
    }

    private static ConnectionSnapshot Connection(
        ConnectionStatus status,
        string statusText,
        bool busy,
        string? sessionText = null) =>
        new(
            status,
            statusText,
            $"Named pipe: {BridgeProtocol.DefaultPipeName}",
            sessionText ?? $"PID {Environment.ProcessId}",
            busy);

    private void Publish(PanelStateSnapshot snapshot)
    {
        lock (_stateGate) _current = snapshot;
        StateChanged?.Invoke(this, snapshot);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshGate.Dispose();
    }
}
