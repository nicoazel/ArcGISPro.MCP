using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.Operations.Services;

namespace ArcGISProMCP.AddIn.ArcGIS.Services;

internal sealed class ProLayerService : ILayerService
{
    public IReadOnlyList<LayerSummary> List(ResolvedMap map) =>
        ProMapService.HostMap(map).GetLayersAsFlattenedList()
            .Select(layer => new LayerSummary(
                ProHandles.ForLayer(layer),
                layer.Name,
                layer.GetType().Name,
                layer.IsVisible,
                layer.Transparency,
                layer is FeatureLayer,
                layer is FeatureLayer featureLayer ? DescribeElevation(featureLayer) : null))
            .ToArray();

    internal static string ToMode(LayerElevationType type) => type switch
    {
        LayerElevationType.OnGround => "on-ground",
        LayerElevationType.RelativeToGround => "relative-to-ground",
        LayerElevationType.RelativeToScene => "relative-to-scene",
        LayerElevationType.AtAbsoluteHeight => "absolute-height",
        LayerElevationType.OnCustomSurface => "on-custom-surface",
        LayerElevationType.RelativeToCustomSurface => "relative-to-custom-surface",
        _ => "none"
    };

    private static LayerElevation DescribeElevation(FeatureLayer layer)
    {
        var definition = layer.GetElevationTypeDefinition();
        return new LayerElevation(ToMode(definition.ElevationType), definition.CartographicOffset, definition.VerticalExaggeration);
    }
}
