using System.Text.Json;
using ArcGISProMCP.Core.Approvals;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Workspaces;

namespace ArcGISProMCP.AddIn.Services;

/// <summary>
/// Add-in lifetime adapter for the process-local approval service. It deliberately exposes no
/// remote decision path; only the dockpane can call <see cref="TryResolve"/>.
/// </summary>
internal sealed class LocalApprovalService : IApprovalService
{
    private readonly ApprovalService _inner = new(lifetime: TimeSpan.FromMinutes(2), capacity: 128);

    public event EventHandler? Changed
    {
        add => _inner.Changed += value;
        remove => _inner.Changed -= value;
    }

    public ApprovalRequestSnapshot Request(
        OperationDescriptor descriptor,
        JsonElement arguments,
        WorkspaceSnapshot workspace) => _inner.Request(descriptor, arguments, workspace);

    public ApprovalRequestSnapshot? GetStatus(string requestId) => _inner.GetStatus(requestId);

    public IReadOnlyList<ApprovalRequestSnapshot> GetPending() => _inner.GetPending();

    public bool TryResolve(string requestId, ApprovalResolution resolution) =>
        _inner.TryResolve(requestId, resolution);

    public bool TryCancel(string requestId) => _inner.TryCancel(requestId);

    public void RevokeAll() => _inner.RevokeAll();

    public ValueTask<bool> IsValidAsync(
        string token,
        OperationDescriptor descriptor,
        JsonElement arguments,
        WorkspaceSnapshot workspace,
        CancellationToken cancellationToken) =>
        _inner.IsValidAsync(token, descriptor, arguments, workspace, cancellationToken);

    public void Dispose() => _inner.Dispose();
}