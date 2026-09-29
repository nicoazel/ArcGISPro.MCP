using System.Collections.Immutable;
using System.Text.Json;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Workspaces;

namespace ArcGISProMCP.Testing;

/// <summary>
/// Workspace snapshots over <see cref="FakeProState"/>. The revision is "rev-N" where N counts
/// <see cref="AdvanceRevision"/> calls, which is how the add-in's provider behaves for writes.
/// </summary>
internal sealed class FakeWorkspace(FakeProState state) : IRevisionPublishingWorkspace
{
    private long _sequence;

    public int SnapshotCount { get; private set; }

    public List<CancellationToken> SettledSnapshotTokens { get; } = [];

    public string Revision => $"rev-{Interlocked.Read(ref _sequence)}";

    public void AdvanceRevision() => Interlocked.Increment(ref _sequence);

    public Task<WorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SnapshotCount++;
        if (state.DirtySamples.Count > 0) state.IsDirty = state.DirtySamples.Dequeue();
        var maps = state.Maps
            .Select(map => new MapState(
                FakeProState.MapHandle(map),
                map.Name,
                map.Type,
                map.Layers.Count,
                string.Equals(map.Name, state.ActiveMapName, StringComparison.Ordinal)))
            .OrderBy(map => map.Name, StringComparer.Ordinal)
            .ToImmutableArray();
        var layouts = state.Layouts
            .Select(layout => new LayoutState(
                FakeProState.LayoutHandle(layout),
                layout.Name,
                0,
                string.Equals(layout.Name, state.ActiveLayoutName, StringComparison.Ordinal)))
            .OrderBy(layout => layout.Name, StringComparer.Ordinal)
            .ToImmutableArray();
        var activeMap = maps.FirstOrDefault(map => map.IsActive)?.Id;
        return Task.FromResult(new WorkspaceSnapshot(
            state.IsOpen ? Revision : "closed",
            DateTimeOffset.UtcNow,
            new ProjectState(state.ProjectName, state.ProjectUri, state.IsDirty, state.IsOpen),
            maps,
            layouts,
            activeMap,
            activeMap,
            [new CapabilityState("arcgis-pro", true, "fake")]));
    }

    public Task<WorkspaceSnapshot> GetSettledSnapshotAsync(CancellationToken cancellationToken)
    {
        SettledSnapshotTokens.Add(cancellationToken);
        return GetSnapshotAsync(cancellationToken);
    }
}

/// <summary>Accepts one fixed confirmation token, or every request when autonomous.</summary>
internal sealed class FakeConfirmation(bool autonomous = false) : IConfirmationValidator, IAutonomousExecutionPolicy
{
    public const string ApprovedToken = "fake-approval";

    public bool AllowsUnattendedRiskyOperations => autonomous;

    public ValueTask<bool> IsValidAsync(
        string token,
        OperationDescriptor descriptor,
        JsonElement arguments,
        WorkspaceSnapshot workspace,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(string.Equals(token, ApprovedToken, StringComparison.Ordinal));
}

internal sealed class FakeAuditLog : IOperationAuditLog
{
    public List<OperationAuditEvent> Events { get; } = [];

    public ValueTask WriteAsync(OperationAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        Events.Add(auditEvent);
        return ValueTask.CompletedTask;
    }
}
