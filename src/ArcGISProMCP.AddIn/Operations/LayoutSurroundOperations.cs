using System.Text.Json;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Layouts;
using ArcGISProMCP.AddIn.ArcGIS;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.AddIn.Operations;

internal sealed class LayoutEnsureSurroundOperation() : ProOperationBase(OperationDescriptor.Create(
    "layout.ensure-surround", "Add a map surround",
    "Adds a named legend, north arrow, or scale bar linked to a map frame using ArcGIS default styling. Existing matching surrounds are retained unchanged.",
    JsonSchemas.ObjectSchema("\"layout\":{\"type\":\"string\"},\"frame\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"},\"kind\":{\"type\":\"string\",\"enum\":[\"legend\",\"north-arrow\",\"scale-bar\"]},\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"width\":{\"type\":\"number\",\"exclusiveMinimum\":0},\"height\":{\"type\":\"number\",\"exclusiveMinimum\":0}", "layout", "frame", "name", "kind", "x", "y", "width", "height"),
    risk: OperationRisk.SafeWrite, capabilities: ["layouts"], tags: ["layout", "legend", "north arrow", "scale bar", "cartography"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var layout = ProHandles.ResolveLayout(RequiredString(arguments, "layout"));
            var frameName = RequiredString(arguments, "frame");
            if (!layout.GetElementsAsFlattenedList().OfType<MapFrame>().Any(frame => frame.Name == frameName))
                throw new ArgumentException($"Map frame '{frameName}' was not found.");
            var name = RequiredString(arguments, "name");
            var kind = RequiredString(arguments, "kind");
            var existing = layout.GetElementsAsFlattenedList().FirstOrDefault(element => element.Name == name);
            if (existing is not null)
            {
                var matches = kind switch { "legend" => existing is Legend, "north-arrow" => existing is NorthArrow, "scale-bar" => existing is ScaleBar, _ => false };
                if (!matches) throw new ArgumentException($"Element '{name}' has a different type.");
                return new { name, kind, created = false };
            }
            MapSurroundInfo info = kind switch
            {
                "legend" => new LegendInfo(), "north-arrow" => new NorthArrowInfo(), "scale-bar" => new ScaleBarInfo(),
                _ => throw new ArgumentException("Unsupported surround kind.")
            };
            info.MapFrameName = frameName;
            var x = arguments.GetProperty("x").GetDouble();
            var y = arguments.GetProperty("y").GetDouble();
            var width = arguments.GetProperty("width").GetDouble();
            var height = arguments.GetProperty("height").GetDouble();
            if (width <= 0 || height <= 0) throw new ArgumentException("Surround dimensions must be positive.");
            ElementFactory.Instance.CreateMapSurroundElement(layout, EnvelopeBuilderEx.CreateEnvelope(x, y, x + width, y + height), info, name, false);
            return new { name, kind, created = true };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }
}
