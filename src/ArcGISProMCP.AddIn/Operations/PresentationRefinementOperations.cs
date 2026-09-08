using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.AddIn.ArcGIS;
using ArcGISProMCP.AddIn.Services;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.AddIn.Operations;

internal sealed class LayoutSetFrameExtentOperation() : ProOperationBase(OperationDescriptor.Create(
    "layout.set-frame-extent", "Frame a layer on a layout",
    "Fits a named map frame to a feature layer with optional scale padding.",
    JsonSchemas.ObjectSchema(
        "\"layout\":{\"type\":\"string\"},\"frame\":{\"type\":\"string\"},\"layer\":{\"type\":\"string\"},\"padding\":{\"type\":\"number\",\"minimum\":1,\"maximum\":20}",
        "layout", "frame", "layer"),
    risk: OperationRisk.SafeWrite, capabilities: ["layouts"], tags: ["layout", "extent", "camera", "zoom"] ))
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
            frame.SetCamera(layer, false);
            var camera = frame.Camera;
            camera.Scale *= OptionalDouble(arguments, "padding", 1.2);
            frame.SetCamera(camera);
            return new { frame = frame.Name, camera.Scale, camera.Heading };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }
}

internal sealed class SymbologySetUniqueValuesOperation() : ProOperationBase(OperationDescriptor.Create(
    "symbology.set-unique-values", "Style polygon categories",
    "Applies explicitly specified colors and labels to values in one polygon-layer field.",
    JsonSchemas.ObjectSchema(
        "\"map\":{\"type\":\"string\"},\"layer\":{\"type\":\"string\"},\"field\":{\"type\":\"string\"},\"classes\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"},\"label\":{\"type\":\"string\"},\"color\":{\"type\":\"string\"}},\"required\":[\"value\",\"color\"],\"additionalProperties\":false}},\"defaultColor\":{\"type\":\"string\"}",
        "layer", "field", "classes"),
    risk: OperationRisk.SafeWrite, capabilities: ["maps"], tags: ["symbology", "categories", "zoning", "land use", "unique values"] ))
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
            using var featureClass = layer.GetFeatureClass();
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
                Fields = [field], Groups = [new CIMUniqueValueGroup { Heading = field, Classes = classes }],
                UseDefaultSymbol = true, DefaultLabel = "Other / unclassified",
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
