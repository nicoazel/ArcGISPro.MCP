using System.Text.Json;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Operations.Services;

namespace ArcGISProMCP.Operations;

internal sealed class LayerListOperation(IMapService maps, ILayerService layers) : ProOperationBase(OperationDescriptor.Create(
    "layer.list", "List layers",
    "Lists the flattened layer tree for a map with stable handles and appearance state.",
    LayerOperationSchemas.ListInput,
    outputSchema: LayerOperationSchemas.ListOutput,
    capabilities: ["maps"], tags: ["layer", "map", "browse"], aliases: ["table of contents", "toc"],
    related: ["layer.add", "layer.set-appearance", "layer.set-elevation", "symbology.set-simple"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var mapReference = OptionalString(arguments, "map");
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var map = maps.Resolve(mapReference);
            return new
            {
                map = map.Id,
                layers = layers.List(map).Select((layer, index) => new
                {
                    id = layer.Id,
                    layer.Name,
                    type = layer.Type,
                    layer.IsVisible,
                    layer.Transparency,
                    drawingOrder = index,
                    isFeatureLayer = layer.IsFeatureLayer,
                    elevation = layer.IsFeatureLayer ? DescribeElevation(layer.Elevation) : null
                }).ToArray()
            };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }

    private static object? DescribeElevation(LayerElevation? elevation) =>
        elevation is null
            ? null
            : new
            {
                mode = elevation.Mode,
                offset = elevation.Offset,
                verticalExaggeration = elevation.VerticalExaggeration
            };
}
