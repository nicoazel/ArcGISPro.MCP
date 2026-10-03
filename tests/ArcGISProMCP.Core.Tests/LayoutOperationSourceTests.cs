namespace ArcGISProMCP.Core.Tests;

public sealed class LayoutOperationSourceTests
{
    [Fact]
    public void Layout_inspection_is_registered_and_exposes_page_element_and_camera_evidence()
    {
        var catalog = ReadSource("ProOperationCatalog.cs");
        var operations = ReadSource("LayoutOperations.cs");

        Assert.Contains("new LayoutInspectOperation()", catalog, StringComparison.Ordinal);
        Assert.Contains("\"layout.inspect\"", operations, StringComparison.Ordinal);
        Assert.Contains("layout.GetPage()", operations, StringComparison.Ordinal);
        Assert.Contains("element.GetBounds(false)", operations, StringComparison.Ordinal);
        Assert.Contains("frame.Map", operations, StringComparison.Ordinal);
        Assert.Contains("frame.Camera", operations, StringComparison.Ordinal);
        Assert.Contains("camera.Heading", operations, StringComparison.Ordinal);
        Assert.Contains("camera.Pitch", operations, StringComparison.Ordinal);
        Assert.Contains("camera.Roll", operations, StringComparison.Ordinal);
        Assert.Contains("camera.Viewpoint.ToString()", operations, StringComparison.Ordinal);
        Assert.Contains("camera.ViewportWidth", operations, StringComparison.Ordinal);
    }

    [Fact]
    public void Existing_layout_converges_to_requested_inches_without_scaling_elements()
    {
        var operations = ReadSource("LayoutOperations.cs");

        Assert.Contains("var page = existing.GetPage();", operations, StringComparison.Ordinal);
        Assert.Contains("page.Units = LinearUnit.Inches;", operations, StringComparison.Ordinal);
        Assert.Contains("page.Width = width;", operations, StringComparison.Ordinal);
        Assert.Contains("page.Height = height;", operations, StringComparison.Ordinal);
        Assert.Contains("existing.SetPage(page, false);", operations, StringComparison.Ordinal);
        Assert.Contains("page = existing.GetPage();", operations, StringComparison.Ordinal);
        Assert.Contains("did not accept the requested", operations, StringComparison.Ordinal);
    }

    [Fact]
    public void Existing_map_frame_converges_map_binding_and_graphic_bounds()
    {
        var operations = ReadSource("LayoutOperations.cs");

        Assert.Contains("if (mapChanged) existing.SetMap(map);", operations, StringComparison.Ordinal);
        Assert.Contains("if (boundsChanged) LayoutElementPlacement.Apply(existing, envelope);", operations, StringComparison.Ordinal);
        Assert.Contains("var actual = existing.GetBounds(false);", operations, StringComparison.Ordinal);
        Assert.Contains("EnsureConverged(existing, map, actual, envelope);", operations, StringComparison.Ordinal);
        Assert.Contains("mapChanged || boundsChanged", operations, StringComparison.Ordinal);
    }

    [Fact]
    public void Existing_surround_converges_frame_binding_and_graphic_bounds()
    {
        var operations = ReadSource("LayoutSurroundOperations.cs");

        Assert.Contains("if (frameChanged) surround.SetMapFrame(frame);", operations, StringComparison.Ordinal);
        Assert.Contains("surround.SetLockedAspectRatio(false);", operations, StringComparison.Ordinal);
        Assert.Contains("created.SetLockedAspectRatio(false);", operations, StringComparison.Ordinal);
        // The size rule itself is LayoutGeometry.FitSurround, behavior-tested in
        // ArcGISProMCP.Operations.Tests (LayoutGeometryTests).
        Assert.Contains("Fit(kind, surround.GetBounds(false), requested) == SurroundFit.Rejected", operations, StringComparison.Ordinal);
        Assert.Contains("LayoutGeometry.FitSurround(kind, requested, Box(actual))", operations, StringComparison.Ordinal);
        Assert.Contains("if (boundsChanged)", operations, StringComparison.Ordinal);
        Assert.Contains("LayoutElementPlacement.Apply(surround, envelope);", operations, StringComparison.Ordinal);
        Assert.Contains("var actual = surround.GetBounds(false);", operations, StringComparison.Ordinal);
        Assert.Contains("EnsureConverged(surround, frame, kind, actual, requested);", operations, StringComparison.Ordinal);
        // The frame binding is still required; a resized surround is reported, never silently accepted.
        Assert.Contains("did not accept its binding to map frame", operations, StringComparison.Ordinal);
        Assert.Contains("\"surround_resized\"", operations, StringComparison.Ordinal);
        Assert.Contains("layout.DeleteElement(created);", operations, StringComparison.Ordinal);
        Assert.Contains("LayoutGeometry.EnsureOnPage(", operations, StringComparison.Ordinal);
        Assert.Contains("updated: frameChanged || boundsChanged", operations, StringComparison.Ordinal);
        Assert.Contains("LegendFittingStrategy.AdjustColumnsAndSize", operations, StringComparison.Ordinal);
        Assert.Contains("ConfigureLegend(createdLegend)", operations, StringComparison.Ordinal);
    }

    [Fact]
    public void Frames_surrounds_and_text_are_refused_off_the_page_before_anything_is_created()
    {
        var layout = ReadSource("LayoutOperations.cs");
        var frame = layout[layout.IndexOf("class LayoutAddMapFrameOperation", StringComparison.Ordinal)..];
        var check = frame.IndexOf("LayoutGeometry.EnsureOnPage($\"Map frame '{name}'\", new PageBox(x, y, width, height), page.Width, page.Height, page.Units.Name);", StringComparison.Ordinal);
        Assert.True(check > 0, "layout.add-map-frame no longer checks the frame against the page.");
        Assert.True(check < frame.IndexOf("CreateMapFrameElement", StringComparison.Ordinal), "The page check must run before the frame is created.");

        var surround = ReadSource("LayoutSurroundOperations.cs");
        Assert.True(surround.IndexOf("LayoutGeometry.EnsureOnPage(", StringComparison.Ordinal) < surround.IndexOf("CreateMapSurroundElement", StringComparison.Ordinal));
        Assert.Contains("ProHandles.ResolveMapFrame(layout, RequiredString(arguments, \"frame\"))", surround, StringComparison.Ordinal);

        var text = ReadSource("LayoutPresentationOperations.cs");
        Assert.True(text.IndexOf("LayoutGeometry.EnsurePointOnPage(", StringComparison.Ordinal) > 0);
        Assert.True(text.IndexOf("LayoutGeometry.EnsurePointOnPage(", StringComparison.Ordinal) < text.IndexOf("CreateTextGraphicElement", StringComparison.Ordinal));

        Assert.Contains("ProHandles.ResolveMapFrame(layout, name)", ReadSource("PresentationRefinementOperations.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void Frame_extent_supports_explicit_scene_heading_and_pitch()
    {
        var operations = ReadSource("PresentationRefinementOperations.cs");

        Assert.Contains("heading", operations, StringComparison.Ordinal);
        Assert.Contains("pitch", operations, StringComparison.Ordinal);
        Assert.Contains("OptionalDouble(arguments, \"heading\", camera.Heading)", operations, StringComparison.Ordinal);
        Assert.Contains("OptionalDouble(arguments, \"pitch\", camera.Pitch)", operations, StringComparison.Ordinal);
        Assert.Contains("actual.Pitch", operations, StringComparison.Ordinal);
        Assert.Contains("CameraViewpoint.LookFrom", operations, StringComparison.Ordinal);
        Assert.Contains("ResolveGroundZ(frame.Map, target)", operations, StringComparison.Ordinal);
        Assert.Contains("LayerElevationType.RelativeToGround", operations, StringComparison.Ordinal);
        Assert.Contains("Math.Tan(pitchRadians) * horizontalDistance", operations, StringComparison.Ordinal);
        Assert.Contains("target.X + Math.Sin(headingRadians) * horizontalDistance", operations, StringComparison.Ordinal);
    }

    private static string ReadSource(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "ArcGISProMCP.AddIn",
                "Operations",
                fileName);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException($"Could not locate {fileName} from the test output tree.");
    }
}
