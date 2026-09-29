using System.Text.Json;
using ArcGISProMCP.Core.Approvals;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Workspaces;

namespace ArcGISProMCP.Testing;

/// <summary>
/// The add-in's approval model without the dockpane: the real process-local
/// <see cref="ApprovalService"/>, plus the autonomous-control switch the add-in keeps in
/// <c>AutonomousControlState</c>. Only code holding this object (a test acting as the person, or
/// the FakeHost console) can resolve a request; the bridge still has no remote approval path.
/// </summary>
internal sealed class FakeHostApprovals(TimeProvider? timeProvider = null) : IApprovalService, IAutonomousExecutionPolicy
{
    private readonly ApprovalService _inner = new(timeProvider, lifetime: TimeSpan.FromMinutes(2), capacity: 128);
    private int _autonomous;

    /// <summary>Mirrors the add-in's explicit autonomous-control setting.</summary>
    public bool Autonomous
    {
        get => Volatile.Read(ref _autonomous) == 1;
        set => Volatile.Write(ref _autonomous, value ? 1 : 0);
    }

    public bool AllowsUnattendedRiskyOperations => Autonomous;

    public event EventHandler? Changed
    {
        add => _inner.Changed += value;
        remove => _inner.Changed -= value;
    }

    public ApprovalRequestSnapshot Request(
        OperationDescriptor descriptor,
        JsonElement arguments,
        WorkspaceSnapshot workspace,
        bool reuseExisting = true) => _inner.Request(descriptor, arguments, workspace, reuseExisting);

    public ApprovalRequestSnapshot? GetStatus(string requestId) => _inner.GetStatus(requestId);

    public IReadOnlyList<ApprovalRequestSnapshot> GetPending() => _inner.GetPending();

    public bool TryResolve(string requestId, ApprovalResolution resolution) => _inner.TryResolve(requestId, resolution);

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
