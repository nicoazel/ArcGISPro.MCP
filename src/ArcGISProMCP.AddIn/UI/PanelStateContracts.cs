using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArcGISProMCP.AddIn.UI;

/// <summary>
/// Framework-neutral state source consumed by the dockpane. The ArcGIS/MCP backend adapts
/// its services to this interface; the view models never call ArcGIS APIs directly.
/// </summary>
internal interface IPanelStateSource : IDisposable
{
    event EventHandler<PanelStateSnapshot>? StateChanged;

    PanelStateSnapshot Current { get; }

    Task ConnectAsync(CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
    Task RefreshAsync(CancellationToken cancellationToken);
    Task OpenProjectAsync(CancellationToken cancellationToken);
    Task ActivateMapAsync(string mapId, CancellationToken cancellationToken);
    Task ActivateLayoutAsync(string layoutId, CancellationToken cancellationToken);
    Task ResolveApprovalAsync(string approvalId, ApprovalDecision decision, CancellationToken cancellationToken);
    Task CaptureVisualAsync(CancellationToken cancellationToken);
    Task RevealVisualAsync(CancellationToken cancellationToken);
    Task ClearVisualCueAsync(CancellationToken cancellationToken);
    Task SaveWorkflowAsync(CancellationToken cancellationToken);
    Task ReplayWorkflowAsync(string workflowId, CancellationToken cancellationToken);
    Task CancelCurrentOperationAsync(CancellationToken cancellationToken);
    Task OpenSettingsAsync(CancellationToken cancellationToken);
}

internal static class PanelStateSourceProvider
{
    private static Func<IPanelStateSource> _factory = static () => new UnavailablePanelStateSource();

    internal static Func<IPanelStateSource> Factory
    {
        get => _factory;
        set => _factory = value ?? throw new ArgumentNullException(nameof(value));
    }

    internal static IPanelStateSource Create() => Factory();
}

internal enum ConnectionStatus
{
    Disconnected,
    Connecting,
    Ready,
    Reconnecting,
    Degraded,
    Faulted
}

internal enum ApprovalRisk
{
    Low,
    Moderate,
    High
}

internal enum ApprovalDecision
{
    ApproveOnce,
    Reject
}

internal enum ActivityLevel
{
    Information,
    Success,
    Warning,
    Error
}

internal sealed record PanelChoice(string Id, string Name);

internal sealed record ConnectionSnapshot(
    ConnectionStatus Status,
    string StatusText,
    string Endpoint,
    string SessionText,
    bool IsBusy);

internal sealed record WorkspaceSnapshot(
    string ProjectName,
    IReadOnlyList<PanelChoice> Maps,
    string? ActiveMapId,
    IReadOnlyList<PanelChoice> Layouts,
    string? ActiveLayoutId,
    string ContextSummary);

internal sealed record ApprovalSnapshot(
    string Id,
    string ToolName,
    string Summary,
    string Target,
    string ArgumentsPreview,
    ApprovalRisk Risk,
    string RequestedAtText,
    bool IsDeciding);

internal sealed record VisualEvidenceSnapshot(
    string? ImagePath,
    string Caption,
    string CapturedAtText,
    string ContextText,
    bool IsCapturing,
    bool HasActiveCue);

internal sealed record ActivitySnapshot(
    string Id,
    ActivityLevel Level,
    string Glyph,
    string Message,
    string Detail,
    string TimestampText,
    bool IsRunning);

internal sealed record WorkflowSnapshot(
    string Id,
    string Name,
    string Summary,
    bool CanReplay);

internal sealed record PanelStateSnapshot(
    ConnectionSnapshot Connection,
    WorkspaceSnapshot Workspace,
    IReadOnlyList<ApprovalSnapshot> Approvals,
    VisualEvidenceSnapshot VisualEvidence,
    IReadOnlyList<ActivitySnapshot> Activity,
    IReadOnlyList<WorkflowSnapshot> Workflows,
    int SkillCount,
    string FooterStatus,
    bool CanCancelOperation)
{
    internal static PanelStateSnapshot Unavailable { get; } = new(
        new ConnectionSnapshot(
            ConnectionStatus.Disconnected,
            "Disconnected",
            "Local MCP bridge",
            "No active session",
            false),
        new WorkspaceSnapshot(
            "No ArcGIS Pro project",
            Array.Empty<PanelChoice>(),
            null,
            Array.Empty<PanelChoice>(),
            null,
            "Open a project to expose map and layout context."),
        Array.Empty<ApprovalSnapshot>(),
        new VisualEvidenceSnapshot(
            null,
            "No visual evidence captured",
            "Not captured",
            "The latest image shared with the LLM appears here.",
            false,
            false),
        new[]
        {
            new ActivitySnapshot(
                "panel-source-unavailable",
                ActivityLevel.Information,
                "i",
                "Waiting for the MCP backend",
                "The dockpane is ready; a panel state adapter has not been registered yet.",
                string.Empty,
                false)
        },
        Array.Empty<WorkflowSnapshot>(),
        0,
        "Visual evidence on request",
        false);
}

internal sealed class UnavailablePanelStateSource : IPanelStateSource
{
    public event EventHandler<PanelStateSnapshot>? StateChanged
    {
        add { }
        remove { }
    }

    public PanelStateSnapshot Current => PanelStateSnapshot.Unavailable;

    public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task OpenProjectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ActivateMapAsync(string mapId, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ActivateLayoutAsync(string layoutId, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ResolveApprovalAsync(string approvalId, ApprovalDecision decision, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task CaptureVisualAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task RevealVisualAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ClearVisualCueAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task SaveWorkflowAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ReplayWorkflowAsync(string workflowId, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task CancelCurrentOperationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task OpenSettingsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public void Dispose() { }
}
