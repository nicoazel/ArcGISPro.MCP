using System.Globalization;
using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Layouts;
using ArcGISProMCP.AddIn.ArcGIS;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Operations;

namespace ArcGISProMCP.AddIn.Operations;

internal sealed class LayoutEnsureSurroundOperation() : ProOperationBase(OperationDescriptor.Create(
    "layout.ensure-surround", "Add a map surround",
    "Adds or converges a named legend, north arrow, or scale bar linked to a map frame using ArcGIS default styling.",
    LayoutSurroundOperationSchemas.EnsureSurroundInput,
    risk: OperationRisk.SafeWrite, capabilities: ["layouts"], tags: ["layout", "legend", "north arrow", "scale bar", "cartography"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var (data, notice) = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var layout = ProHandles.ResolveLayout(RequiredString(arguments, "layout"));
            var frame = ProHandles.ResolveMapFrame(layout, RequiredString(arguments, "frame"));
            var name = RequiredString(arguments, "name");
            var kind = RequiredString(arguments, "kind");
            var x = arguments.GetProperty("x").GetDouble();
            var y = arguments.GetProperty("y").GetDouble();
            var width = arguments.GetProperty("width").GetDouble();
            var height = arguments.GetProperty("height").GetDouble();
            if (width <= 0 || height <= 0) throw OperationException.InvalidArgument("Surround width and height must be positive.");
            var requested = new PageBox(x, y, width, height);
            var page = layout.GetPage();
            LayoutGeometry.EnsureOnPage($"Map surround '{name}'", requested, page.Width, page.Height, page.Units.Name);
            var envelope = EnvelopeBuilderEx.CreateEnvelope(x, y, x + width, y + height);
            var existing = layout.GetElementsAsFlattenedList().FirstOrDefault(element =>
                string.Equals(element.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                var matches = kind switch { "legend" => existing is Legend, "north-arrow" => existing is NorthArrow, "scale-bar" => existing is ScaleBar, _ => false };
                if (!matches) throw OperationException.InvalidArgument($"Layout element '{name}' already exists and is not a {kind}; choose a different name.");
                var surround = (MapSurround)existing;
                if (surround is Legend existingLegend) ConfigureLegend(existingLegend);
                var frameChanged = surround.MapFrame is null ||
                                   !string.Equals(surround.MapFrame.Name, frame.Name, StringComparison.OrdinalIgnoreCase);
                var boundsChanged = Fit(kind, surround.GetBounds(false), requested) == SurroundFit.Rejected;
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
                var fit = EnsureConverged(surround, frame, kind, actual, requested);
                return (Result(surround, kind, created: false, updated: frameChanged || boundsChanged, actual),
                    Notice(fit, kind, surround.Name, requested, actual));
            }
            MapSurroundInfo info = kind switch
            {
                "legend" => new LegendInfo(),
                "north-arrow" => new NorthArrowInfo(),
                "scale-bar" => new ScaleBarInfo(),
                _ => throw OperationException.InvalidArgument($"Unsupported surround kind '{kind}'. Use legend, north-arrow or scale-bar.")
            };
            info.MapFrameName = frame.Name;
            var created = ElementFactory.Instance.CreateMapSurroundElement(layout, envelope, info, name, false);
            try
            {
                if (created is Legend createdLegend) ConfigureLegend(createdLegend);
                var createdBounds = created.GetBounds(false);
                if (Fit(kind, createdBounds, requested) == SurroundFit.Rejected)
                {
                    created.SetLockedAspectRatio(false);
                    LayoutElementPlacement.Apply(created, envelope);
                    createdBounds = created.GetBounds(false);
                }
                var createdFit = EnsureConverged(created, frame, kind, createdBounds, requested);
                return (Result(created, kind, created: true, updated: false, createdBounds),
                    Notice(createdFit, kind, created.Name, requested, createdBounds));
            }
            catch
            {
                // A new surround that did not converge is removed again, so a failed ensure leaves no stray element.
                layout.DeleteElement(created);
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision, notice is null ? null : [notice]);
    }

    private static PageBox Box(Envelope bounds) => new(bounds.XMin, bounds.YMin, bounds.Width, bounds.Height);

    private static SurroundFit Fit(string kind, Envelope actual, PageBox requested) =>
        LayoutGeometry.FitSurround(kind, requested, Box(actual));

    private static object Result(MapSurround surround, string kind, bool created, bool updated, Envelope actual) => new
    {
        name = surround.Name,
        kind,
        frame = surround.MapFrame?.Name,
        created,
        updated,
        bounds = new { x = actual.XMin, y = actual.YMin, width = actual.Width, height = actual.Height }
    };

    private static OperationNotice? Notice(SurroundFit fit, string kind, string name, PageBox requested, Envelope actual) =>
        fit == SurroundFit.Resized
            ? new OperationNotice("surround_resized", LayoutGeometry.ResizedMessage(kind, name, requested, Box(actual)))
            : null;

    private static void ConfigureLegend(Legend legend)
    {
        var definition = legend.GetDefinition() as CIMLegend
            ?? throw new InvalidOperationException($"Legend '{legend.Name}' definition could not be read.");
        definition.FittingStrategy = LegendFittingStrategy.AdjustColumnsAndSize;
        legend.SetDefinition(definition);
    }

    /// <summary>
    /// The frame binding must have taken and the surround must sit at the requested anchor; its size
    /// may follow the style (see <see cref="LayoutGeometry.FitSurround"/>).
    /// </summary>
    private static SurroundFit EnsureConverged(MapSurround surround, MapFrame requestedFrame, string kind, Envelope actual, PageBox requested)
    {
        if (surround.MapFrame is null ||
            !string.Equals(surround.MapFrame.Name, requestedFrame.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Map surround '{surround.Name}' did not accept its binding to map frame '{requestedFrame.Name}'.");
        }

        var fit = Fit(kind, actual, requested);
        if (fit == SurroundFit.Rejected)
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "Map surround '{0}' was placed at x {1:0.###}, y {2:0.###} with size {3:0.###} x {4:0.###}, which does not match the requested anchor x {5:0.###}, y {6:0.###} and size {7:0.###} x {8:0.###}.",
                surround.Name, actual.XMin, actual.YMin, actual.Width, actual.Height, requested.X, requested.Y, requested.Width, requested.Height));
        }

        return fit;
    }
}
