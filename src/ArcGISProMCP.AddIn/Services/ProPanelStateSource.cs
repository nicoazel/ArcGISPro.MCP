using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using ArcGISProMCP.AddIn.UI;
using ArcGISProMCP.Bridge.Protocol;
using ArcGISProMCP.Core.Approvals;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Workflows;
using Microsoft.Win32;

namespace ArcGISProMCP.AddIn.Services;

internal sealed class ProPanelStateSource(
    OperationContext context,
    IBridgeRequestHandler handler,
    IWorkflowLibrary workflows,
    FileResourceStore resources,
    BridgeAccessState access) : IPanelStateSource
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions IndentedJson = new(JsonOptions) { WriteIndented = true };
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly IApprovalService? _approvals = context.Confirmation as IApprovalService;
    private readonly object _stateGate = new();
    private readonly List<ActivitySnapshot> _activity = [];
    private PanelStateSnapshot _current = PanelStateSnapshot.Unavailable;
    private int _approvalSubscribed;
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
        _approvals?.RevokeAll();
        Publish(Current with
        {
            Connection = Connection(ConnectionStatus.Disconnected, "Disconnected", false)
        });
        return Task.CompletedTask;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureApprovalSubscription();
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
            var approvalSnapshots = CreateApprovalSnapshots();
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
                Approvals = approvalSnapshots,
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
        var approvals = _approvals ?? throw new InvalidOperationException("The local approval service is unavailable.");
        var resolution = decision == ApprovalDecision.ApproveOnce
            ? ApprovalResolution.ApproveOnce
            : ApprovalResolution.Deny;
        if (!approvals.TryResolve(approvalId, resolution))
            throw new InvalidOperationException("This approval request is no longer pending. Refresh before deciding.");

        AddActivity(
            decision == ApprovalDecision.ApproveOnce ? ActivityLevel.Success : ActivityLevel.Warning,
            decision == ApprovalDecision.ApproveOnce ? "Approved once" : "Request denied",
            $"Approval request {approvalId} was resolved locally.");
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
        var workspace = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var result = await CallBridgeAsync(
            "workflow.run",
            new
            {
                workflowId,
                parameters = new { },
                expectedRevision = workspace.Revision
            },
            cancellationToken).ConfigureAwait(false);
        if (!result.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException($"Workflow '{workflowId}' did not complete successfully. Review its step results in the audit log.");

        AddActivity(ActivityLevel.Success, "Workflow completed", workflowId);
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
        var argumentElement = JsonSerializer.SerializeToElement(arguments);
        var approval = SelfApprovePanelRequest(id, argumentElement, workspace);
        OperationResult result;
        try
        {
            result = await ExecuteAsync(id, argumentElement, workspace.Revision, cancellationToken, approval?.ConfirmationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // Never leave an unconsumed panel token behind (for example after a revision race).
            if (approval is not null) _approvals?.TryCancel(approval.Id);
        }
        if (!result.Success) throw new InvalidOperationException(result.Message ?? result.ErrorCode ?? $"Operation '{id}' failed.");
        AddActivity(ActivityLevel.Success, id, result.Message ?? "Completed");
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A dockpane button click is itself the human approval. Route it through the same approval
    /// queue as MCP requests so the executor validates a real single-use token bound to these exact
    /// arguments and revision, and so the decision is audited like any other approval.
    /// </summary>
    private ApprovalRequestSnapshot? SelfApprovePanelRequest(
        string id,
        JsonElement arguments,
        Core.Workspaces.WorkspaceSnapshot workspace)
    {
        var registry = ArcGISProMcpModule.Instance?.Registry;
        if (registry is null || !registry.TryGet(id, out var operation)) return null;
        var descriptor = operation.Descriptor;
        if (!descriptor.RequiresConfirmation &&
            descriptor.Risk is not (OperationRisk.Destructive or OperationRisk.ExternalSideEffect))
            return null;

        var approvals = _approvals ?? throw new InvalidOperationException("The local approval service is unavailable.");
        var request = approvals.Request(descriptor, arguments, workspace);
        if (!approvals.TryResolve(request.Id, ApprovalResolution.ApproveOnce))
            throw new InvalidOperationException($"Could not approve the panel request for '{id}'.");
        return approvals.GetStatus(request.Id) is { ConfirmationToken: not null } approved
            ? approved
            : throw new InvalidOperationException($"The panel approval for '{id}' expired before it could be used.");
    }

    private async Task<OperationResult> ExecuteAsync(
        string id,
        JsonElement arguments,
        string revision,
        CancellationToken cancellationToken,
        string? confirmationToken = null)
    {
        var result = await CallBridgeAsync(
            "registry.invoke",
            new { operationId = id, arguments, expectedRevision = revision, confirmationToken },
            cancellationToken).ConfigureAwait(false);
        return result.Deserialize<OperationResult>(JsonOptions)
            ?? throw new InvalidOperationException($"Operation '{id}' returned an invalid result.");
    }

    private async Task<JsonElement> CallBridgeAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var response = await handler.HandleAsync(
            new BridgeRequest(
                BridgeProtocol.Version,
                requestId,
                method,
                JsonSerializer.SerializeToElement(parameters, JsonOptions),
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
        if (!response.Success)
            throw new InvalidOperationException(
                $"{response.Error?.Code ?? "bridge_request_failed"}: {response.Error?.Message ?? "The local bridge request failed."}");
        return response.Result?.Clone()
            ?? throw new InvalidOperationException($"Bridge method '{method}' returned no result.");
    }

    private ApprovalSnapshot[] CreateApprovalSnapshots()
    {
        if (_approvals is null) return [];
        return _approvals.GetPending()
            .Select(approval => new ApprovalSnapshot(
                approval.Id,
                approval.OperationId,
                approval.OperationVersion,
                approval.OperationTitle,
                approval.OperationSummary,
                approval.WorkspaceRevision,
                JsonSerializer.Serialize(approval.Arguments, IndentedJson),
                approval.Risk == OperationRisk.SafeWrite ? ApprovalRisk.Moderate : ApprovalRisk.High,
                $"Requested {approval.RequestedAt.ToLocalTime():g}",
                $"Expires {approval.ExpiresAt.ToLocalTime():g}",
                false))
            .ToArray();
    }

    private void EnsureApprovalSubscription()
    {
        if (_approvals is null || Interlocked.CompareExchange(ref _approvalSubscribed, 1, 0) != 0) return;
        _approvals.Changed += OnApprovalsChanged;
    }

    private void OnApprovalsChanged(object? sender, EventArgs eventArgs)
    {
        if (_disposed) return;
        try
        {
            Publish(Current with { Approvals = CreateApprovalSnapshots() });
        }
        catch (ObjectDisposedException)
        {
        }
    }
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
            $"Named pipe: {ArcGISProMcpModule.Instance?.PipeName ?? BridgeProtocol.DefaultPipeName}",
            sessionText ?? $"PID {Environment.ProcessId}{(AutonomousControlState.Enabled ? " · AUTONOMOUS CONTROL" : string.Empty)}",
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
        if (_approvals is not null && Interlocked.Exchange(ref _approvalSubscribed, 0) != 0)
            _approvals.Changed -= OnApprovalsChanged;
        _refreshGate.Dispose();
    }
}
