using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.AddIn.ArcGIS;
using ArcGISProMCP.AddIn.Services;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.AddIn.Operations;

internal sealed class SymbologySetSimpleOperation() : ProOperationBase(OperationDescriptor.Create(
    "symbology.set-simple", "Set simple symbology",
    "Applies a single-symbol renderer appropriate to a point, line, or polygon feature layer.",
    SymbologyOperationSchemas.SetSimpleInput,
    risk: OperationRisk.SafeWrite, capabilities: ["maps"], tags: ["symbology", "renderer", "color", "cartography"],
    aliases: ["change color", "simple renderer", "restyle layer"], related: ["style.search", "layer.set-appearance", "label.configure"], undoable: true))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var layerReference = RequiredString(arguments, "layer");
        var mapReference = OptionalString(arguments, "map");
        var color = ColorParser.Parse(RequiredString(arguments, "color"));
        var outline = ColorParser.Parse(OptionalString(arguments, "outlineColor") ?? "#333333");
        var size = OptionalDouble(arguments, "size", 8);
        var outlineWidth = OptionalDouble(arguments, "outlineWidth", 1);
        if (size <= 0 || outlineWidth < 0) throw new ArgumentOutOfRangeException(nameof(arguments), "Symbol size must be positive and outline width non-negative.");

        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var map = ProHandles.ResolveMap(mapReference);
            var featureLayer = ProHandles.ResolveLayer(map, layerReference) as FeatureLayer
                ?? throw new InvalidOperationException("Simple symbology requires a feature layer.");
            using var featureClass = featureLayer.GetFeatureClass();
            var shapeType = featureClass.GetDefinition().GetShapeType();
            var fillColor = ToCim(color);
            var strokeColor = ToCim(outline);
            CIMSymbol symbol = shapeType switch
            {
                GeometryType.Point or GeometryType.Multipoint => SymbolFactory.Instance.ConstructPointSymbol(fillColor, size),
                GeometryType.Polyline => SymbolFactory.Instance.ConstructLineSymbol(fillColor, size),
                GeometryType.Polygon or GeometryType.Multipatch => SymbolFactory.Instance.ConstructPolygonSymbol(
                    fillColor, SimpleFillStyle.Solid,
                    SymbolFactory.Instance.ConstructStroke(strokeColor, outlineWidth, SimpleLineStyle.Solid)),
                _ => throw new NotSupportedException($"Geometry type '{shapeType}' does not support simple feature symbology.")
            };
            featureLayer.SetRenderer(new CIMSimpleRenderer { Symbol = symbol.MakeSymbolReference() });
            return new { layer = ProHandles.ForLayer(featureLayer), featureLayer.Name, geometryType = shapeType.ToString(), color = RequiredString(arguments, "color"), size };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }

    private static CIMColor ToCim(RgbaColor color) => ColorFactory.Instance.CreateRGBColor(
        color.Red, color.Green, color.Blue, color.Alpha * 100d / byte.MaxValue);
}

internal sealed class LabelConfigureOperation() : ProOperationBase(OperationDescriptor.Create(
    "label.configure", "Configure labels",
    "Enables or disables feature labels and optionally sets the Arcade expression, font family, size, and text color.",
    SymbologyOperationSchemas.LabelConfigureInput,
    risk: OperationRisk.SafeWrite, capabilities: ["maps"], tags: ["label", "font", "text", "cartography"],
    aliases: ["turn labels on", "change label font", "label field"], related: ["symbology.set-simple", "layer.set-appearance"], undoable: true))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var layerReference = RequiredString(arguments, "layer");
        var mapReference = OptionalString(arguments, "map");
        var enabled = OptionalBoolean(arguments, "enabled", true);
        var expression = OptionalString(arguments, "expression");
        var fontFamily = OptionalString(arguments, "fontFamily") ?? "Arial";
        var fontStyle = OptionalString(arguments, "fontStyle") ?? "Regular";
        var size = OptionalDouble(arguments, "size", 10);
        var color = ColorParser.Parse(OptionalString(arguments, "color") ?? "#222222");
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(arguments), "Label size must be positive.");

        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var map = ProHandles.ResolveMap(mapReference);
            var featureLayer = ProHandles.ResolveLayer(map, layerReference) as FeatureLayer
                ?? throw new InvalidOperationException("Labels require a feature layer.");
            if (featureLayer.LabelClasses.Count == 0) featureLayer.AddLabelClass("MCP labels");
            var definition = featureLayer.GetDefinition() as CIMFeatureLayer
                ?? throw new InvalidOperationException("Feature layer definition could not be read.");
            var labelClass = definition.LabelClasses?.FirstOrDefault()
                ?? throw new InvalidOperationException("A label class could not be created.");
            if (expression is not null)
            {
                labelClass.Expression = expression;
                labelClass.ExpressionEngine = LabelExpressionEngine.Arcade;
            }
            labelClass.TextSymbol = SymbolFactory.Instance.ConstructTextSymbol(
                ColorFactory.Instance.CreateRGBColor(color.Red, color.Green, color.Blue, color.Alpha * 100d / byte.MaxValue),
                size, fontFamily, fontStyle).MakeSymbolReference();
            featureLayer.SetDefinition(definition);
            featureLayer.SetLabelVisibility(enabled);
            return new { layer = ProHandles.ForLayer(featureLayer), featureLayer.Name, enabled, expression, fontFamily, fontStyle, size };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }
}
