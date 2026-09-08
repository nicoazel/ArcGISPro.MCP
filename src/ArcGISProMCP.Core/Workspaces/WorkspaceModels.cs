using System.Collections.Immutable;

namespace ArcGISProMCP.Core.Workspaces;

public sealed record WorkspaceSnapshot(
    string Revision,
    DateTimeOffset CapturedAt,
    ProjectState Project,
    ImmutableArray<MapState> Maps,
    ImmutableArray<LayoutState> Layouts,
    string? ActiveMapId,
    string? ActiveViewId,
    ImmutableArray<CapabilityState> Capabilities);

public sealed record ProjectState(string? Name, string? Uri, bool IsDirty, bool IsOpen);

public sealed record MapState(string Id, string Name, string MapType, int LayerCount, bool IsActive);

public sealed record LayoutState(string Id, string Name, int MapFrameCount, bool IsOpen);

public sealed record CapabilityState(string Id, bool Available, string? Detail = null);

public interface IWorkspaceStateProvider
{
    Task<WorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}
