using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Workspaces;

namespace ArcGISProMCP.AddIn.ArcGIS;

internal sealed class ProWorkspaceStateProvider(IOperationDispatcher dispatcher) : IWorkspaceStateProvider
{
    private long _mutationSequence;
    internal void AdvanceRevision() => Interlocked.Increment(ref _mutationSequence);
    public Task<WorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        dispatcher.OnMainCimThreadAsync(CreateSnapshot, cancellationToken);

    private WorkspaceSnapshot CreateSnapshot()
    {
        var rhinoPeerAvailable = AppDomain.CurrentDomain.GetAssemblies()
            .Any(assembly => assembly.GetType("RhinoInside.ArcGISPro.RhinoMcpPeer", false, false) is not null);
        var project = Project.Current;
        if (project is null)
        {
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
                new CapabilityState("layouts", true),
                new CapabilityState("geoprocessing", true),
                new CapabilityState("visual-observations", true)
                ,new CapabilityState("rhino-peer", rhinoPeerAvailable, rhinoPeerAvailable ? "Rhino.Inside peer contract 1.0" : "Optional peer not loaded")
                ,new CapabilityState("rhino-sync", Services.RhinoPeerClient.CanInvoke)
            ]);
    }
}
