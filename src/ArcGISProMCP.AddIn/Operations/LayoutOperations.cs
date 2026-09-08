using System.Text.Json;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.AddIn.ArcGIS;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.AddIn.Operations;

internal sealed class LayoutListOperation() : ProOperationBase(OperationDescriptor.Create(
    "layout.list", "List layouts",
    "Lists project layouts and their map frames using stable handles.",
    JsonSchemas.EmptyObject,
    capabilities: ["layouts"], tags: ["layout", "map frame", "browse"], aliases: ["layouts", "print layouts"],
    related: ["layout.ensure", "layout.add-map-frame", "layout.activate"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
            (Project.Current?.GetItems<LayoutProjectItem>() ?? [])
                .Select(item => item.GetLayout())
                .Select(layout => new
                {
                    id = ProHandles.ForLayout(layout), layout.Name,
                    mapFrames = layout.GetElementsAsFlattenedList().OfType<MapFrame>()
                        .Select(frame => new { frame.Name, map = frame.Map is null ? null : ProHandles.ForMap(frame.Map) }).ToArray()
                })
                .OrderBy(layout => layout.Name, StringComparer.Ordinal)
                .ToArray(), cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }
}

internal sealed class LayoutEnsureOperation() : ProOperationBase(OperationDescriptor.Create(
    "layout.ensure", "Ensure layout exists",
    "Returns an existing named layout or creates a page in inches using the requested dimensions.",
    JsonSchemas.ObjectSchema(
        "\"name\": {\"type\": \"string\", \"minLength\": 1}, \"width\": {\"type\": \"number\", \"exclusiveMinimum\": 0}, \"height\": {\"type\": \"number\", \"exclusiveMinimum\": 0}",
        "name"),
    risk: OperationRisk.SafeWrite, capabilities: ["layouts"], tags: ["layout", "page", "create"],
    aliases: ["new layout", "create layout"], related: ["layout.add-map-frame", "layout.activate"], undoable: true))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var name = RequiredString(arguments, "name");
        var width = OptionalDouble(arguments, "width", 11);
        var height = OptionalDouble(arguments, "height", 8.5);
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(arguments), "Layout dimensions must be positive.");
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var existing = (Project.Current?.GetItems<LayoutProjectItem>() ?? [])
                .Select(item => item.GetLayout())
                .FirstOrDefault(layout => string.Equals(layout.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return new LayoutMutationResult(ProHandles.ForLayout(existing), existing.Name, false, width, height);
            var layout = LayoutFactory.Instance.CreateLayout(width, height, LinearUnit.Inches, false, 0);
            layout.SetName(name);
            return new LayoutMutationResult(ProHandles.ForLayout(layout), layout.Name, true, width, height);
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }

    private sealed record LayoutMutationResult(string Id, string Name, bool Created, double Width, double Height);
}

internal sealed class LayoutAddMapFrameOperation() : ProOperationBase(OperationDescriptor.Create(
    "layout.add-map-frame", "Add map frame",
    "Adds a map frame to a layout at page coordinates measured in inches.",
    JsonSchemas.ObjectSchema(
        "\"layout\": {\"type\": \"string\", \"minLength\": 1}, \"map\": {\"type\": \"string\", \"minLength\": 1}, \"name\": {\"type\": \"string\"}, \"x\": {\"type\": \"number\"}, \"y\": {\"type\": \"number\"}, \"width\": {\"type\": \"number\", \"exclusiveMinimum\": 0}, \"height\": {\"type\": \"number\", \"exclusiveMinimum\": 0}",
        "layout", "map"),
    risk: OperationRisk.SafeWrite, capabilities: ["layouts", "maps"], tags: ["layout", "map frame", "compose", "multi-map"],
    aliases: ["place map on layout", "multi map layout"], related: ["layout.ensure", "layout.list"], undoable: true))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var layoutReference = RequiredString(arguments, "layout");
        var mapReference = RequiredString(arguments, "map");
        var name = OptionalString(arguments, "name") ?? "Map Frame";
        var x = OptionalDouble(arguments, "x", 0.5);
        var y = OptionalDouble(arguments, "y", 0.5);
        var width = OptionalDouble(arguments, "width", 10);
        var height = OptionalDouble(arguments, "height", 7.5);
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(arguments), "Map-frame dimensions must be positive.");
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var layout = ProHandles.ResolveLayout(layoutReference);
            var map = ProHandles.ResolveMap(mapReference);
            var existing = layout.GetElementsAsFlattenedList().OfType<MapFrame>()
                .FirstOrDefault(frame => string.Equals(frame.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                ZoomToOperationalLayers(existing, map);
                return new FrameMutationResult(ProHandles.ForLayout(layout), ProHandles.ForMap(map), existing.Name, x, y, width, height, false);
            }
            var envelope = EnvelopeBuilderEx.CreateEnvelope(x, y, x + width, y + height);
            var frame = ElementFactory.Instance.CreateMapFrameElement(layout, envelope, map, name, false, null);
            ZoomToOperationalLayers(frame, map);
            return new FrameMutationResult(ProHandles.ForLayout(layout), ProHandles.ForMap(map), frame.Name, x, y, width, height, true);
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }

    private static void ZoomToOperationalLayers(MapFrame frame, Map map)
    {
        var layers = map.GetLayersAsFlattenedList().OfType<BasicFeatureLayer>().Cast<Layer>().ToArray();
        if (layers.Length > 0) frame.SetCamera(layers, false);
    }

    private sealed record FrameMutationResult(
        string Layout,
        string Map,
        string Name,
        double X,
        double Y,
        double Width,
        double Height,
        bool Created);
}

internal sealed class LayoutActivateOperation() : ProOperationBase(OperationDescriptor.Create(
    "layout.activate", "Activate layout",
    "Opens or activates a layout view.",
    JsonSchemas.ObjectSchema("\"layout\": {\"type\": \"string\", \"minLength\": 1}", "layout"),
    risk: OperationRisk.SafeWrite, executionTarget: ExecutionTarget.ArcGISUiThread, capabilities: ["layouts"],
    tags: ["layout", "view", "open", "switch"], aliases: ["show layout", "open layout"], related: ["layout.list", "view.capture"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var layout = await context.Dispatcher.OnMainCimThreadAsync(
            () => ProHandles.ResolveLayout(RequiredString(arguments, "layout")), cancellationToken).ConfigureAwait(false);
        await context.Dispatcher.OnUiThreadAsync(async () =>
        {
            var existing = global::ArcGIS.Desktop.Framework.FrameworkApplication.Panes
                .OfType<ILayoutPane>()
                .FirstOrDefault(pane => string.Equals(pane.LayoutView?.Layout?.URI, layout.URI, StringComparison.OrdinalIgnoreCase));
            if (existing is global::ArcGIS.Desktop.Framework.Contracts.Pane pane) pane.Activate();
            else await global::ArcGIS.Desktop.Framework.FrameworkApplication.Panes.CreateLayoutPaneAsync(layout).ConfigureAwait(true);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(new { id = ProHandles.ForLayout(layout), layout.Name }), snapshot.Revision);
    }
}
