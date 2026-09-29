using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.Operations;
using ArcGISProMCP.Operations.Services;

namespace ArcGISProMCP.AddIn.ArcGIS.Services;

internal sealed class ProMapService : IMapService
{
    public IReadOnlyList<MapSummary> List()
    {
        var activeUri = MapView.Active?.Map?.URI;
        return (Project.Current?.GetItems<MapProjectItem>() ?? [])
            .Select(item => item.GetMap())
            .Select(map => new MapSummary(
                ProHandles.ForMap(map),
                map.Name,
                map.MapType.ToString(),
                map.GetLayersAsFlattenedList().Count,
                string.Equals(map.URI, activeUri, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    public ResolvedMap Resolve(string? handleOrName) => ToResolved(ProHandles.ResolveMap(handleOrName));

    public ResolvedMap? FindByName(string name)
    {
        var existing = (Project.Current?.GetItems<MapProjectItem>() ?? [])
            .Select(item => item.GetMap())
            .FirstOrDefault(map => string.Equals(map.Name, name, StringComparison.OrdinalIgnoreCase));
        return existing is null ? null : ToResolved(existing);
    }

    public bool TryGetBasemap(string normalizedName, out string canonicalName)
    {
        if (Enum.TryParse<Basemap>(normalizedName, true, out var basemap))
        {
            canonicalName = basemap.ToString();
            return true;
        }
        canonicalName = string.Empty;
        return false;
    }

    public ResolvedMap Create(string name, MapViewKind kind, string basemap)
    {
        var (mapType, viewingMode) = kind switch
        {
            MapViewKind.GlobalScene => (MapType.Scene, MapViewingMode.SceneGlobal),
            MapViewKind.LocalScene => (MapType.Scene, MapViewingMode.SceneLocal),
            _ => (MapType.Map, MapViewingMode.Map)
        };
        var created = MapFactory.Instance.CreateMap(name, mapType, viewingMode, Enum.Parse<Basemap>(basemap, true));
        return ToResolved(created);
    }

    public async Task ActivateViewAsync(ResolvedMap map)
    {
        var hostMap = HostMap(map);
        var existing = global::ArcGIS.Desktop.Framework.FrameworkApplication.Panes
            .OfType<IMapPane>()
            .FirstOrDefault(pane => string.Equals(pane.MapView.Map.URI, hostMap.URI, StringComparison.OrdinalIgnoreCase));
        if (existing is global::ArcGIS.Desktop.Framework.Contracts.Pane pane)
            pane.Activate();
        else
            await hostMap.OpenViewAsync().ConfigureAwait(true);
    }

    public void ClearSelection(ResolvedMap map) => HostMap(map).ClearSelection();

    /// <summary>basemap.set parsing: the same normalization, host lookup and error as map.ensure.</summary>
    internal static Basemap ParseBasemap(string value) =>
        Enum.TryParse<Basemap>(Basemaps.Normalize(value), true, out var basemap)
            ? basemap
            : throw new ArgumentException(Basemaps.UnknownMessage(value));

    internal static Map HostMap(ResolvedMap map) =>
        map.HostMap as Map ?? throw new ArgumentException("The resolved map does not belong to ArcGIS Pro.", nameof(map));

    private static ResolvedMap ToResolved(Map map) => new(ProHandles.ForMap(map), map.Name, map);
}
