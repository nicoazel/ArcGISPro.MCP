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
    related: ["layout.inspect", "layout.ensure", "layout.add-map-frame", "layout.activate"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
            (Project.Current?.GetItems<LayoutProjectItem>() ?? [])
                .Select(item => item.GetLayout())
                .Select(layout => new
                {
                    id = ProHandles.ForLayout(layout),
                    layout.Name,
                    mapFrames = layout.GetElementsAsFlattenedList().OfType<MapFrame>()
                        .Select(frame => new { frame.Name, map = frame.Map is null ? null : ProHandles.ForMap(frame.Map) }).ToArray()
                })
                .OrderBy(layout => layout.Name, StringComparer.Ordinal)
                .ToArray(), cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }
}

internal sealed class LayoutInspectOperation() : ProOperationBase(OperationDescriptor.Create(
    "layout.inspect", "Inspect layout",
    "Returns a layout's page dimensions and flattened element geometry, including map-frame bindings and cameras.",
    LayoutOperationSchemas.InspectInput,
    capabilities: ["layouts"], tags: ["layout", "inspect", "map frame", "camera", "verification"],
    aliases: ["inspect layout elements", "verify layout geometry", "read map frames"],
    related: ["layout.list", "layout.add-map-frame", "layout.set-frame-extent", "view.capture"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(
        JsonElement arguments,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var layout = ProHandles.ResolveLayout(RequiredString(arguments, "layout"));
            var page = layout.GetPage();
            var elements = layout.GetElementsAsFlattenedList()
                .Select((element, drawingOrder) =>
                {
                    var bounds = element.GetBounds(false);
                    object? mapFrame = null;
                    if (element is MapFrame frame)
                    {
                        var camera = frame.Camera;
                        mapFrame = new
                        {
                            map = frame.Map is null ? null : ProHandles.ForMap(frame.Map),
                            mapName = frame.Map?.Name,
                            camera = new
                            {
                                x = Finite(camera.X),
                                y = Finite(camera.Y),
                                z = Finite(camera.Z),
                                scale = Finite(camera.Scale),
                                heading = Finite(camera.Heading),
                                pitch = Finite(camera.Pitch),
                                roll = Finite(camera.Roll),
                                viewpoint = camera.Viewpoint.ToString(),
                                viewportWidth = Finite(camera.ViewportWidth),
                                viewportHeight = Finite(camera.ViewportHeight),
                                spatialReferenceWkid = camera.SpatialReference?.Wkid
                            }
                        };
                    }

                    return new
                    {
                        element.Name,
                        type = ElementType(element),
                        drawingOrder,
                        bounds = new
                        {
                            x = bounds.XMin,
                            y = bounds.YMin,
                            width = bounds.Width,
                            height = bounds.Height,
                            xMax = bounds.XMax,
                            yMax = bounds.YMax
                        },
                        mapFrame
                    };
                })
                .ToArray();

            return new
            {
                id = ProHandles.ForLayout(layout),
                layout.Name,
                page = new { width = page.Width, height = page.Height, units = page.Units.Name },
                elements
            };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }

    private static double? Finite(double value) => double.IsFinite(value) ? value : null;

    private static string ElementType(Element element) => element switch
    {
        MapFrame => "map-frame",
        Legend => "legend",
        NorthArrow => "north-arrow",
        ScaleBar => "scale-bar",
        TextElement => "text",
        GroupElement => "group",
        GraphicElement => "graphic",
        _ => element.GetType().Name
    };
}

internal sealed class LayoutEnsureOperation() : ProOperationBase(OperationDescriptor.Create(
    "layout.ensure", "Ensure layout exists",
    "Returns an existing named layout or creates a page in inches using the requested dimensions.",
    LayoutOperationSchemas.EnsureInput,
    risk: OperationRisk.SafeWrite, capabilities: ["layouts"], tags: ["layout", "page", "create"],
    aliases: ["new layout", "create layout"], related: ["layout.inspect", "layout.add-map-frame", "layout.activate"], undoable: true))
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
            if (existing is not null)
            {
                var page = existing.GetPage();
                var updated = page.Units.FactoryCode != LinearUnit.Inches.FactoryCode ||
                              !NearlyEqual(page.Width, width) ||
                              !NearlyEqual(page.Height, height);
                if (updated)
                {
                    page.Units = LinearUnit.Inches;
                    page.Width = width;
                    page.Height = height;
                    existing.SetPage(page, false);
                    page = existing.GetPage();
                }

                if (page.Units.FactoryCode != LinearUnit.Inches.FactoryCode ||
                    !NearlyEqual(page.Width, width) ||
                    !NearlyEqual(page.Height, height))
                {
                    throw new InvalidOperationException($"Layout '{existing.Name}' did not accept the requested {width} by {height} inch page size.");
                }

                return new LayoutMutationResult(
                    ProHandles.ForLayout(existing), existing.Name, false, updated,
                    page.Width, page.Height, page.Units.Name);
            }
            var layout = LayoutFactory.Instance.CreateLayout(width, height, LinearUnit.Inches, false, 0);
            layout.SetName(name);
            var createdPage = layout.GetPage();
            return new LayoutMutationResult(
                ProHandles.ForLayout(layout), layout.Name, true, false,
                createdPage.Width, createdPage.Height, createdPage.Units.Name);
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }

    private static bool NearlyEqual(double left, double right) => Math.Abs(left - right) <= 1e-6;

    private sealed record LayoutMutationResult(
        string Id,
        string Name,
        bool Created,
        bool Updated,
        double Width,
        double Height,
        string Units);
}

internal sealed class LayoutAddMapFrameOperation() : ProOperationBase(OperationDescriptor.Create(
    "layout.add-map-frame", "Add map frame",
    "Adds a map frame to a layout at page coordinates measured in inches.",
    LayoutOperationSchemas.AddMapFrameInput,
    risk: OperationRisk.SafeWrite, capabilities: ["layouts", "maps"], tags: ["layout", "map frame", "compose", "multi-map"],
    aliases: ["place map on layout", "multi map layout"], related: ["layout.ensure", "layout.inspect", "layout.list"], undoable: true))
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
            var envelope = EnvelopeBuilderEx.CreateEnvelope(x, y, x + width, y + height);
            var existing = layout.GetElementsAsFlattenedList().OfType<MapFrame>()
                .FirstOrDefault(frame => string.Equals(frame.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                var mapChanged = existing.Map is null ||
                                 !string.Equals(existing.Map.URI, map.URI, StringComparison.OrdinalIgnoreCase);
                var boundsChanged = !LayoutElementPlacement.BoundsMatch(existing.GetBounds(false), envelope);
                if (mapChanged) existing.SetMap(map);
                if (boundsChanged) LayoutElementPlacement.Apply(existing, envelope);
                ZoomToOperationalLayers(existing, map);
                var actual = existing.GetBounds(false);
                EnsureConverged(existing, map, actual, envelope);
                return new FrameMutationResult(
                    ProHandles.ForLayout(layout), ProHandles.ForMap(map), existing.Name,
                    actual.XMin, actual.YMin, actual.Width, actual.Height, false, mapChanged || boundsChanged);
            }
            var frame = ElementFactory.Instance.CreateMapFrameElement(layout, envelope, map, name, false, null);
            ZoomToOperationalLayers(frame, map);
            var createdBounds = frame.GetBounds(false);
            EnsureConverged(frame, map, createdBounds, envelope);
            return new FrameMutationResult(
                ProHandles.ForLayout(layout), ProHandles.ForMap(map), frame.Name,
                createdBounds.XMin, createdBounds.YMin, createdBounds.Width, createdBounds.Height, true, false);
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }

    private static void ZoomToOperationalLayers(MapFrame frame, Map map)
    {
        var layers = map.GetLayersAsFlattenedList().OfType<BasicFeatureLayer>().Cast<Layer>().ToArray();
        if (layers.Length > 0) frame.SetCamera(layers, false);
    }

    private static void EnsureConverged(MapFrame frame, Map requestedMap, Envelope actual, Envelope requested)
    {
        if (frame.Map is null ||
            !string.Equals(frame.Map.URI, requestedMap.URI, StringComparison.OrdinalIgnoreCase) ||
            !LayoutElementPlacement.BoundsMatch(actual, requested))
        {
            throw new InvalidOperationException($"Map frame '{frame.Name}' did not accept its requested map binding and bounds.");
        }
    }

    private sealed record FrameMutationResult(
        string Layout,
        string Map,
        string Name,
        double X,
        double Y,
        double Width,
        double Height,
        bool Created,
        bool Updated);
}

internal static class LayoutElementPlacement
{
    public static bool BoundsMatch(Envelope actual, Envelope expected) =>
        Math.Abs(actual.XMin - expected.XMin) <= 1e-6 &&
        Math.Abs(actual.YMin - expected.YMin) <= 1e-6 &&
        Math.Abs(actual.XMax - expected.XMax) <= 1e-6 &&
        Math.Abs(actual.YMax - expected.YMax) <= 1e-6;

    public static void Apply(Element element, Envelope expected)
    {
        element.SetWidth(expected.Width);
        element.SetHeight(expected.Height);
        var resized = element.GetBounds(false);
        element.SetX(element.GetX() + expected.XMin - resized.XMin);
        element.SetY(element.GetY() + expected.YMin - resized.YMin);
    }
}

internal sealed class LayoutActivateOperation() : ProOperationBase(OperationDescriptor.Create(
    "layout.activate", "Activate layout",
    "Opens or activates a layout view.",
    LayoutOperationSchemas.ActivateInput,
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
