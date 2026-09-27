using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Layouts;
using ArcGISProMCP.AddIn.ArcGIS;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.AddIn.Operations;

internal sealed class LayoutEnsureSurroundOperation() : ProOperationBase(OperationDescriptor.Create(
    "layout.ensure-surround", "Add a map surround",
    "Adds or converges a named legend, north arrow, or scale bar linked to a map frame using ArcGIS default styling.",
    JsonSchemas.ObjectSchema("\"layout\":{\"type\":\"string\"},\"frame\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"},\"kind\":{\"type\":\"string\",\"enum\":[\"legend\",\"north-arrow\",\"scale-bar\"]},\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"width\":{\"type\":\"number\",\"exclusiveMinimum\":0},\"height\":{\"type\":\"number\",\"exclusiveMinimum\":0}", "layout", "frame", "name", "kind", "x", "y", "width", "height"),
    risk: OperationRisk.SafeWrite, capabilities: ["layouts"], tags: ["layout", "legend", "north arrow", "scale bar", "cartography"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var layout = ProHandles.ResolveLayout(RequiredString(arguments, "layout"));
            var frameName = RequiredString(arguments, "frame");
            var frame = layout.GetElementsAsFlattenedList().OfType<MapFrame>()
                .SingleOrDefault(candidate => string.Equals(candidate.Name, frameName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Map frame '{frameName}' was not found.");
            var name = RequiredString(arguments, "name");
            var kind = RequiredString(arguments, "kind");
            var x = arguments.GetProperty("x").GetDouble();
            var y = arguments.GetProperty("y").GetDouble();
            var width = arguments.GetProperty("width").GetDouble();
            var height = arguments.GetProperty("height").GetDouble();
            if (width <= 0 || height <= 0) throw new ArgumentException("Surround dimensions must be positive.");
            var envelope = EnvelopeBuilderEx.CreateEnvelope(x, y, x + width, y + height);
            var existing = layout.GetElementsAsFlattenedList().FirstOrDefault(element =>
                string.Equals(element.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                var matches = kind switch { "legend" => existing is Legend, "north-arrow" => existing is NorthArrow, "scale-bar" => existing is ScaleBar, _ => false };
                if (!matches) throw new ArgumentException($"Element '{name}' has a different type.");
                var surround = (MapSurround)existing;
                if (surround is Legend existingLegend) ConfigureLegend(existingLegend);
                var frameChanged = surround.MapFrame is null ||
                                   !string.Equals(surround.MapFrame.Name, frame.Name, StringComparison.OrdinalIgnoreCase);
                var boundsChanged = !SurroundBoundsMatch(kind, surround.GetBounds(false), envelope);
                if (frameChanged) surround.SetMapFrame(frame);
                if (boundsChanged)
                {
                    // North arrows and some scale-bar styles are created with their aspect
                    // ratio locked. Without releasing that lock, SetWidth/SetHeight can
                    // silently preserve the old proportions and defeat ensure semantics.
                    surround.SetLockedAspectRatio(false);
                    LayoutElementPlacement.Apply(surround, envelope);
                }
                var actual = surround.GetBounds(false);
                EnsureConverged(surround, frame, kind, actual, envelope);
                return new
                {
                    name = surround.Name,
                    kind,
                    frame = surround.MapFrame?.Name,
                    created = false,
                    updated = frameChanged || boundsChanged,
                    bounds = new { x = actual.XMin, y = actual.YMin, width = actual.Width, height = actual.Height }
                };
            }
            MapSurroundInfo info = kind switch
            {
                "legend" => new LegendInfo(),
                "north-arrow" => new NorthArrowInfo(),
                "scale-bar" => new ScaleBarInfo(),
                _ => throw new ArgumentException("Unsupported surround kind.")
            };
            info.MapFrameName = frame.Name;
            var created = ElementFactory.Instance.CreateMapSurroundElement(layout, envelope, info, name, false);
            if (created is Legend createdLegend) ConfigureLegend(createdLegend);
            var createdBounds = created.GetBounds(false);
            if (!SurroundBoundsMatch(kind, createdBounds, envelope))
            {
                created.SetLockedAspectRatio(false);
                LayoutElementPlacement.Apply(created, envelope);
                createdBounds = created.GetBounds(false);
            }
            EnsureConverged(created, frame, kind, createdBounds, envelope);
            return new
            {
                name = created.Name,
                kind,
                frame = created.MapFrame?.Name,
                created = true,
                updated = false,
                bounds = new { x = createdBounds.XMin, y = createdBounds.YMin, width = createdBounds.Width, height = createdBounds.Height }
            };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }

    private static bool SurroundBoundsMatch(string kind, Envelope actual, Envelope requested)
    {
        // Map-surround styles retain intrinsic padding or proportions even after
        // SetLockedAspectRatio(false). Treat the request as an anchored maximum
        // box: the accepted footprint must stay inside that box, remain useful,
        // and converge one primary dimension. Legends additionally use a fitting
        // strategy that adapts columns and text size to this box.
        const double anchorTolerance = 0.01;
        const double sizeTolerance = 0.02;
        var anchored = Math.Abs(actual.XMin - requested.XMin) <= anchorTolerance &&
                       Math.Abs(actual.YMin - requested.YMin) <= anchorTolerance;
        var bounded = actual.Width <= requested.Width + sizeTolerance &&
                      actual.Height <= requested.Height + sizeTolerance;
        var useful = kind == "legend"
            ? actual.Width >= 0.25 && actual.Height >= 0.15
            : actual.Width >= requested.Width * 0.5 && actual.Height >= requested.Height * 0.5;
        var primaryDimensionConverged = Math.Abs(actual.Width - requested.Width) <= sizeTolerance ||
                                        Math.Abs(actual.Height - requested.Height) <= sizeTolerance;
        var contentConverged = kind == "legend" || primaryDimensionConverged;
        return anchored && bounded && useful && contentConverged;
    }

    private static void ConfigureLegend(Legend legend)
    {
        var definition = legend.GetDefinition() as CIMLegend
            ?? throw new InvalidOperationException($"Legend '{legend.Name}' definition could not be read.");
        definition.FittingStrategy = LegendFittingStrategy.AdjustColumnsAndSize;
        legend.SetDefinition(definition);
    }

    private static void EnsureConverged(MapSurround surround, MapFrame requestedFrame, string kind, Envelope actual, Envelope requested)
    {
        if (surround.MapFrame is null ||
            !string.Equals(surround.MapFrame.Name, requestedFrame.Name, StringComparison.OrdinalIgnoreCase) ||
            !SurroundBoundsMatch(kind, actual, requested))
        {
            throw new InvalidOperationException($"Map surround '{surround.Name}' did not accept its requested frame binding and bounds.");
        }
    }
}
