using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.AddIn.ArcGIS;
using ArcGISProMCP.AddIn.Services;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Operations;

namespace ArcGISProMCP.AddIn.Operations;

internal sealed class LayoutSetFrameExtentOperation() : ProOperationBase(OperationDescriptor.Create(
    "layout.set-frame-extent", "Frame a layer on a layout",
    "Fits a named map frame to a feature layer with optional scale padding and camera heading/pitch overrides.",
    PresentationRefinementOperationSchemas.SetFrameExtentInput,
    risk: OperationRisk.SafeWrite, capabilities: ["layouts"], tags: ["layout", "extent", "camera", "zoom"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var layout = ProHandles.ResolveLayout(RequiredString(arguments, "layout"));
            var name = RequiredString(arguments, "frame");
            var frame = layout.GetElementsAsFlattenedList().OfType<MapFrame>().SingleOrDefault(item => item.Name == name)
                ?? throw new ArgumentException($"Map frame '{name}' was not found.");
            var layer = ProHandles.ResolveLayer(frame.Map, RequiredString(arguments, "layer"));
            // A broken data layer has no extent to frame; group layers report their own status.
            if (layer is not ILayerContainer) LayerData.EnsureAvailable(layer);
            frame.SetCamera(layer, false);
            var camera = frame.Camera;
            var padding = OptionalDouble(arguments, "padding", 1.2);
            if (frame.Map.MapType == MapType.Scene)
            {
                // A scene camera is an observer position, not a 2D scale. Resolve the
                // ground Z at the layer center and place an explicit LookFrom observer
                // above and opposite the requested viewing direction. This also keeps
                // relative-to-ground geometry and the camera on the same vertical datum.
                var extent = layer.QueryExtent(false);
                if (extent.Width <= 0 || extent.Height <= 0 || extent.SpatialReference is null)
                    throw new InvalidOperationException($"Layer '{layer.Name}' has no usable scene extent.");
                var frameBounds = frame.GetBounds(false);
                var frameAspect = frameBounds.Width / frameBounds.Height;
                var target = extent.Center;
                var sourceCenterZ = double.IsFinite(extent.ZMin) && double.IsFinite(extent.ZMax)
                    ? (extent.ZMin + extent.ZMax) / 2d
                    : 0d;
                var groundZ = ResolveGroundZ(frame.Map, target);
                var elevationType = layer.GetElevationTypeDefinition().ElevationType;
                var targetZ = elevationType is LayerElevationType.OnGround or LayerElevationType.RelativeToGround
                    ? groundZ + sourceCenterZ
                    : sourceCenterZ;
                var pitch = OptionalDouble(arguments, "pitch", -55d);
                if (pitch is > -5d or <= -90d)
                    throw new ArgumentOutOfRangeException(nameof(arguments), "Scene pitch must be greater than -90 and no greater than -5 degrees.");
                var heading = OptionalDouble(arguments, "heading", 15d);
                var horizontalDistance = Math.Max(extent.Width, extent.Height * frameAspect) * padding * 1.25d;
                var headingRadians = heading * Math.PI / 180d;
                var pitchRadians = Math.Abs(pitch) * Math.PI / 180d;
                camera = new Camera(
                    // ArcGIS SDK heading is positive toward west (90 = west),
                    // so the X component has the opposite sign from a conventional
                    // compass-bearing vector.
                    target.X + Math.Sin(headingRadians) * horizontalDistance,
                    target.Y - Math.Cos(headingRadians) * horizontalDistance,
                    targetZ + Math.Tan(pitchRadians) * horizontalDistance,
                    pitch,
                    heading,
                    extent.SpatialReference,
                    CameraViewpoint.LookFrom);
                frame.SetCamera(camera);
            }
            else
            {
                camera.Scale *= padding;
                if (arguments.TryGetProperty("heading", out _)) camera.Heading = OptionalDouble(arguments, "heading", camera.Heading);
                if (arguments.TryGetProperty("pitch", out _)) camera.Pitch = OptionalDouble(arguments, "pitch", camera.Pitch);
                frame.SetCamera(camera);
            }
            var actual = frame.Camera;
            return new
            {
                frame = frame.Name,
                actual.Scale,
                actual.Heading,
                actual.Pitch,
                viewpoint = actual.Viewpoint.ToString(),
                X = double.IsFinite(actual.X) ? actual.X : (double?)null,
                Y = double.IsFinite(actual.Y) ? actual.Y : (double?)null,
                Z = double.IsFinite(actual.Z) ? actual.Z : (double?)null,
                actual.ViewportWidth,
                actual.ViewportHeight
            };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }

    private static double ResolveGroundZ(Map map, MapPoint target)
    {
        try
        {
            var profile = map.GetElevationProfileFromSurface([target]);
            var first = profile.Status == SurfaceZsResultStatus.Ok
                ? profile.Polyline?.Points.FirstOrDefault()
                : null;
            return first is not null && double.IsFinite(first.Z) ? first.Z : 0d;
        }
        catch (InvalidOperationException)
        {
            return 0d;
        }
        catch (ArgumentException)
        {
            return 0d;
        }
    }
}

internal sealed class SymbologySetUniqueValuesOperation() : ProOperationBase(OperationDescriptor.Create(
    "symbology.set-unique-values", "Style polygon categories",
    "Applies explicitly specified colors and labels to values in one polygon-layer field.",
    PresentationRefinementOperationSchemas.SetUniqueValuesInput,
    risk: OperationRisk.SafeWrite, capabilities: ["maps"], tags: ["symbology", "categories", "zoning", "land use", "unique values"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var items = arguments.GetProperty("classes").EnumerateArray().ToArray();
        if (items.Length is < 1 or > 100) throw new ArgumentException("Specify from 1 to 100 categories.");
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var map = ProHandles.ResolveMap(OptionalString(arguments, "map"));
            var layer = ProHandles.ResolveLayer(map, RequiredString(arguments, "layer")) as FeatureLayer
                ?? throw new ArgumentException("A feature layer is required.");
            using var featureClass = LayerData.OpenFeatureClass(layer);
            using var definition = featureClass.GetDefinition();
            if (definition.GetShapeType() != GeometryType.Polygon) throw new ArgumentException("This category renderer supports polygon layers.");
            var field = RequiredString(arguments, "field");
            if (definition.FindField(field) < 0) throw new ArgumentException($"Unknown field '{field}'.");
            var classes = items.Select(item => new CIMUniqueValueClass
            {
                Label = OptionalString(item, "label") ?? RequiredString(item, "value"),
                Values = [new CIMUniqueValue { FieldValues = [RequiredString(item, "value")] }],
                Symbol = Symbol(RequiredString(item, "color")),
                Visible = true
            }).ToArray();
            layer.SetRenderer(new CIMUniqueValueRenderer
            {
                Fields = [field],
                Groups = [new CIMUniqueValueGroup { Heading = field, Classes = classes }],
                UseDefaultSymbol = true,
                DefaultLabel = "Other / unclassified",
                DefaultSymbol = Symbol(OptionalString(arguments, "defaultColor") ?? "#DBDDD8")
            });
            return new { layer = ProHandles.ForLayer(layer), field, classes = classes.Length };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }

    private static CIMSymbolReference Symbol(string value)
    {
        var color = ColorParser.Parse(value);
        return SymbolFactory.Instance.ConstructPolygonSymbol(
            ColorFactory.Instance.CreateRGBColor(color.Red, color.Green, color.Blue, color.Alpha * 100d / 255),
            SimpleFillStyle.Solid,
            SymbolFactory.Instance.ConstructStroke(ColorFactory.Instance.CreateRGBColor(255, 255, 255), 0.35, SimpleLineStyle.Solid)).MakeSymbolReference();
    }
}
