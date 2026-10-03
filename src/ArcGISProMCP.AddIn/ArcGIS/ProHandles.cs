using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.AddIn.ArcGIS;

internal static class ProHandles
{
    public static string ForMap(Map map) => $"pro://map/{Uri.EscapeDataString(map.URI)}";

    public static string ForLayer(Layer layer) => $"pro://layer/{Uri.EscapeDataString(layer.URI)}";

    public static string ForLayout(Layout layout) => $"pro://layout/{Uri.EscapeDataString(layout.URI)}";

    public static Map ResolveMap(string? handleOrName)
    {
        var maps = Project.Current?.GetItems<MapProjectItem>() ?? [];
        if (string.IsNullOrWhiteSpace(handleOrName))
        {
            return MapView.Active?.Map ?? maps.FirstOrDefault()?.GetMap()
                ?? throw OperationException.MapNotFound(null);
        }

        var value = Decode(handleOrName, "pro://map/");
        return maps.Select(item => item.GetMap()).FirstOrDefault(map =>
                   string.Equals(map.URI, value, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(map.Name, value, StringComparison.OrdinalIgnoreCase))
               ?? throw OperationException.MapNotFound(handleOrName);
    }

    public static Layer ResolveLayer(Map map, string handleOrName)
    {
        var value = Decode(handleOrName, "pro://layer/");
        return map.GetLayersAsFlattenedList().FirstOrDefault(layer =>
                   string.Equals(layer.URI, value, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layer.Name, value, StringComparison.OrdinalIgnoreCase))
               ?? throw OperationException.LayerNotFound(handleOrName, map.Name);
    }

    public static Layout ResolveLayout(string handleOrName)
    {
        var value = Decode(handleOrName, "pro://layout/");
        return (Project.Current?.GetItems<LayoutProjectItem>() ?? [])
                   .Select(item => item.GetLayout())
                   .FirstOrDefault(layout =>
                       string.Equals(layout.URI, value, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layout.Name, value, StringComparison.OrdinalIgnoreCase))
               ?? throw OperationException.LayoutNotFound(handleOrName);
    }

    /// <summary>The map frame with this name (case-insensitive) on the layout.</summary>
    public static MapFrame ResolveMapFrame(Layout layout, string name) =>
        layout.GetElementsAsFlattenedList().OfType<MapFrame>()
            .FirstOrDefault(frame => string.Equals(frame.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw OperationException.FrameNotFound(name, layout.Name);

    private static string Decode(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? Uri.UnescapeDataString(value[prefix.Length..])
            : value;
}
