using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.AddIn.ArcGIS;
using ArcGISProMCP.AddIn.Services;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.AddIn.Operations;

internal sealed class LayoutSetTextOperation() : ProOperationBase(OperationDescriptor.Create(
    "layout.set-text", "Set layout text",
    "Creates or updates a named point-text element on a layout, positioned in page inches with explicit typography and color.",
    JsonSchemas.ObjectSchema(
        "\"layout\": {\"type\": \"string\", \"minLength\": 1}, \"name\": {\"type\": \"string\", \"minLength\": 1}, \"text\": {\"type\": \"string\"}, \"x\": {\"type\": \"number\"}, \"y\": {\"type\": \"number\"}, \"fontFamily\": {\"type\": \"string\", \"minLength\": 1}, \"fontStyle\": {\"type\": \"string\", \"minLength\": 1}, \"size\": {\"type\": \"number\", \"exclusiveMinimum\": 0}, \"color\": {\"type\": \"string\", \"pattern\": \"^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$\"}",
        "layout", "name", "text", "x", "y"),
    risk: OperationRisk.SafeWrite,
    capabilities: ["layouts"],
    tags: ["layout", "text", "title", "annotation", "typography", "presentation"],
    aliases: ["add layout title", "update layout text", "place text"],
    examples: ["Create a named title at x 0.5, y 8 with Aptos Display SemiBold at 24 points."],
    related: ["layout.ensure", "layout.add-map-frame", "layout.activate"],
    undoable: true))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(
        JsonElement arguments,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        var layoutReference = RequiredString(arguments, "layout");
        var name = RequiredString(arguments, "name");
        var text = RequiredText(arguments, "text");
        var x = RequiredNumber(arguments, "x");
        var y = RequiredNumber(arguments, "y");
        var fontFamily = OptionalString(arguments, "fontFamily") ?? "Arial";
        var fontStyle = OptionalString(arguments, "fontStyle") ?? "Regular";
        var size = OptionalDouble(arguments, "size", 12);
        var colorText = OptionalString(arguments, "color") ?? "#222222";
        var color = ColorParser.Parse(colorText);
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(arguments), "Text size must be positive.");

        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var layout = ProHandles.ResolveLayout(layoutReference);
            var symbol = SymbolFactory.Instance.ConstructTextSymbol(
                ColorFactory.Instance.CreateRGBColor(
                    color.Red,
                    color.Green,
                    color.Blue,
                    color.Alpha * 100d / byte.MaxValue),
                size,
                fontFamily,
                fontStyle);
            var existing = layout.GetElementsAsFlattenedList()
                .OfType<TextElement>()
                .FirstOrDefault(element => string.Equals(element.Name, name, StringComparison.OrdinalIgnoreCase));
            var created = existing is null;
            TextElement element;
            if (existing is null)
            {
                var point = MapPointBuilderEx.CreateMapPoint(x, y, null);
                element = (TextElement)ElementFactory.Instance.CreateTextGraphicElement(
                    layout,
                    TextType.PointText,
                    point,
                    symbol,
                    text,
                    name,
                    false,
                    null);
            }
            else
            {
                element = existing;
                var graphic = element.GetGraphic();
                if (graphic is not CIMTextGraphic textGraphic)
                    throw new InvalidOperationException($"Layout element '{name}' is not point text.");
                textGraphic.Text = text;
                textGraphic.Symbol = symbol.MakeSymbolReference();
                element.SetGraphic(textGraphic);
                element.SetX(x);
                element.SetY(y);
            }

            return new
            {
                layout = ProHandles.ForLayout(layout),
                element.Name,
                created,
                text,
                x,
                y,
                fontFamily,
                fontStyle,
                size,
                color = colorText
            };
        }, cancellationToken).ConfigureAwait(false);

        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }

    private static string RequiredText(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : throw new ArgumentException($"Required string argument '{name}' was missing.", nameof(arguments));

    private static double RequiredNumber(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : throw new ArgumentException($"Required numeric argument '{name}' was missing.", nameof(arguments));
}
