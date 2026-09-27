using System.Text.Json;
using ArcGISProMCP.AddIn.ArcGIS;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.AddIn.Operations;

internal sealed class MapClearSelectionOperation() : ProOperationBase(OperationDescriptor.Create(
    "map.clear-selection", "Clear feature selection",
    "Clears selected features in a map without editing feature data. Useful before a clean layout export.",
    MapSelectionOperationSchemas.ClearSelectionInput,
    risk: OperationRisk.SafeWrite, capabilities: ["maps"], tags: ["map", "selection", "highlight", "export"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var map = ProHandles.ResolveMap(OptionalString(arguments, "map"));
            map.ClearSelection();
            return new { map = ProHandles.ForMap(map), cleared = true };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }
}
