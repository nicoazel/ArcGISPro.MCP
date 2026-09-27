using System.Text.Json;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Operations.Services;

namespace ArcGISProMCP.Operations;

internal sealed class MapClearSelectionOperation(IMapService maps) : ProOperationBase(OperationDescriptor.Create(
    "map.clear-selection", "Clear feature selection",
    "Clears selected features in a map without editing feature data. Useful before a clean layout export.",
    MapSelectionOperationSchemas.ClearSelectionInput,
    risk: OperationRisk.SafeWrite, capabilities: ["maps"], tags: ["map", "selection", "highlight", "export"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var map = maps.Resolve(OptionalString(arguments, "map"));
            maps.ClearSelection(map);
            return new { map = map.Id, cleared = true };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }
}
