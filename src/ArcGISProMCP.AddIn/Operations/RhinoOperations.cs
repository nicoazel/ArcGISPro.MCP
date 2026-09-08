using System.Text.Json;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.AddIn.ArcGIS;
using ArcGISProMCP.AddIn.Services;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.AddIn.Operations;

internal sealed class RhinoPeerStateOperation() : ProOperationBase(OperationDescriptor.Create(
    "rhino.peer-state", "Get Rhino peer state",
    "Returns the optional Rhino.Inside child-session and McNeel MCP peer state without taking a RhinoCommon dependency.",
    JsonSchemas.EmptyObject,
    executionTarget: ExecutionTarget.Background, tags: ["rhino", "peer", "rhino.inside", "mcp"],
    aliases: ["rhino status", "rhino mcp status"], related: ["rhino.handoff"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var state = RhinoPeerClient.TryGetState();
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(new
        {
            available = state is not null,
            state,
            guidance = state is null
                ? "Load Rhino.Inside-ArcGIS and start Rhino to enable the peer handoff."
                : "Send Rhino modeling requests to McNeel's Rhino MCP peer; use this ArcGIS registry for maps, data, and layouts."
        }), snapshot.Revision);
    }
}

internal sealed class RhinoHandoffOperation() : ProOperationBase(OperationDescriptor.Create(
    "rhino.handoff", "Prepare Rhino peer handoff",
    "Builds a compact handoff packet that associates an intent with the active ArcGIS project, map, spatial reference, extent, and Rhino peer session.",
    JsonSchemas.ObjectSchema("\"intent\": {\"type\": \"string\"}"),
    tags: ["rhino", "peer", "handoff", "spatial context"], aliases: ["send to rhino", "rhino modeling handoff"],
    related: ["rhino.peer-state", "map.list", "view.capture"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var intent = OptionalString(arguments, "intent");
        var rhino = RhinoPeerClient.TryGetState();
        var arcgis = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var project = Project.Current;
            var view = MapView.Active;
            var map = view?.Map;
            var spatialReference = map?.SpatialReference;
            var extent = view?.Extent;
            return new
            {
                project = project is null ? null : new { project.Name, project.URI },
                activeMap = map is null ? null : new
                {
                    id = ProHandles.ForMap(map), map.Name,
                    spatialReference = spatialReference is null ? null : new { spatialReference.Name, spatialReference.Wkid },
                    extent = extent is null ? null : new { extent.XMin, extent.YMin, extent.XMax, extent.YMax },
                    layerCount = map.GetLayersAsFlattenedList().Count
                }
            };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(new
        {
            available = rhino is not null,
            target = "McNeel Rhino-MCP-Platform peer",
            intent,
            arcgis,
            rhino,
            guidance = rhino is null
                ? "Rhino.Inside peer is unavailable. Load the existing Rhino.Inside-ArcGIS add-in and start Rhino, then retry."
                : "Pass this packet as context to the Rhino MCP peer. Geometry remains owned by Rhino; GIS data and cartography remain owned by this ArcGIS MCP."
        }), snapshot.Revision);
    }
}
