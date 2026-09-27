using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.AddIn.Services;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Workspaces;

namespace ArcGISProMCP.AddIn.ArcGIS;

internal sealed class ProWorkspaceStateProvider(IOperationDispatcher dispatcher) : IWorkspaceStateProvider
{
    private long _mutationSequence;
    internal void AdvanceRevision() => Interlocked.Increment(ref _mutationSequence);
    public Task<WorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        dispatcher.OnMainCimThreadAsync(CreateSnapshot, cancellationToken);

    internal async Task<WorkspaceSnapshot> GetSettledSnapshotAsync(CancellationToken cancellationToken)
    {
        var previous = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var quietSamples = 0;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            var current = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (string.Equals(current.Revision, previous.Revision, StringComparison.Ordinal))
            {
                quietSamples++;
                if (quietSamples >= 2) return current;
            }
            else
            {
                quietSamples = 0;
            }
            previous = current;
        }
        return previous;
    }

    private WorkspaceSnapshot CreateSnapshot()
    {
        var project = Project.Current;
        if (project is null)
        {
            global::ArcGISProMCP.AddIn.ArcGISProMcpModule.Instance?.RefreshHostDiscovery(null, null);
            return new WorkspaceSnapshot(
                "closed",
                DateTimeOffset.UtcNow,
                new ProjectState(null, null, false, false),
                [],
                [],
                null,
                null,
                [new CapabilityState("arcgis-pro", true, typeof(Map).Assembly.GetName().Version?.ToString())]);
        }

        global::ArcGISProMCP.AddIn.ArcGISProMcpModule.Instance?.RefreshHostDiscovery(project.Name, project.URI);

        var activeMap = MapView.Active?.Map;
        var maps = project.GetItems<MapProjectItem>()
            .Select(item => item.GetMap())
            .Select(map => new MapState(
                ProHandles.ForMap(map),
                map.Name,
                map.MapType.ToString(),
                map.GetLayersAsFlattenedList().Count,
                string.Equals(map.URI, activeMap?.URI, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(map => map.Name, StringComparer.Ordinal)
            .ToImmutableArray();

        var activeLayout = LayoutView.Active?.Layout;
        var layouts = project.GetItems<LayoutProjectItem>()
            .Select(item => item.GetLayout())
            .Select(layout => new LayoutState(
                ProHandles.ForLayout(layout),
                layout.Name,
                layout.GetElementsAsFlattenedList().OfType<MapFrame>().Count(),
                string.Equals(layout.URI, activeLayout?.URI, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(layout => layout.Name, StringComparer.Ordinal)
            .ToImmutableArray();

        var projectState = new ProjectState(project.Name, project.URI, project.IsDirty, true);
        var revisionMaterial = string.Join('|',
            Interlocked.Read(ref _mutationSequence),
            project.URI,
            project.Name,
            project.IsDirty,
            string.Join(';', maps.Select(map => $"{map.Id}:{map.Name}:{map.LayerCount}:{map.IsActive}")),
            string.Join(';', layouts.Select(layout => $"{layout.Id}:{layout.Name}:{layout.MapFrameCount}:{layout.IsOpen}")));
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revisionMaterial)))[..16].ToLowerInvariant();

        return new WorkspaceSnapshot(
            revision,
            DateTimeOffset.UtcNow,
            projectState,
            maps,
            layouts,
            activeMap is null ? null : ProHandles.ForMap(activeMap),
            activeLayout is null ? activeMap is null ? null : ProHandles.ForMap(activeMap) : ProHandles.ForLayout(activeLayout),
            [
                new CapabilityState("arcgis-pro", true, typeof(Map).Assembly.GetName().Version?.ToString()),
                new CapabilityState("maps", true),
                new CapabilityState("feature-editing", true),
                new CapabilityState("metadata", true),
                new CapabilityState("layouts", true),
                new CapabilityState("geoprocessing", true),
                new CapabilityState(
                    "arcpy",
                    ArcPyCapabilityState.Enabled,
                    ArcPyCapabilityState.Enabled
                        ? "Explicitly enabled; the configured ArcPy runtime is validated before script inspection or execution."
                        : "Disabled by local configuration."),
                new CapabilityState(
                    "autonomous-control",
                    AutonomousControlState.Enabled,
                    AutonomousControlState.Enabled
                        ? "Explicitly enabled; risky operations bypass local review but still require current workspace revisions."
                        : "Disabled; risky operations require local review."),
                new CapabilityState("visual-observations", true)
            ]);
    }
}
