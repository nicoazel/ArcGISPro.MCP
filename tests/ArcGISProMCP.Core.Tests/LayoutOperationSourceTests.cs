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
        Assert.Contains("SurroundBoundsMatch(kind, surround.GetBounds(false), envelope)", operations, StringComparison.Ordinal);
        Assert.Contains("Treat the request", operations, StringComparison.Ordinal);
        Assert.Contains("kind == \"legend\"", operations, StringComparison.Ordinal);
        Assert.Contains("actual.Width >= 0.25", operations, StringComparison.Ordinal);
        Assert.Contains("kind == \"legend\" || primaryDimensionConverged", operations, StringComparison.Ordinal);
        Assert.Contains("actual.Width >= requested.Width * 0.5", operations, StringComparison.Ordinal);
        Assert.Contains("primaryDimensionConverged", operations, StringComparison.Ordinal);
        Assert.Contains("if (boundsChanged)", operations, StringComparison.Ordinal);
        Assert.Contains("LayoutElementPlacement.Apply(surround, envelope);", operations, StringComparison.Ordinal);
        Assert.Contains("var actual = surround.GetBounds(false);", operations, StringComparison.Ordinal);
        Assert.Contains("EnsureConverged(surround, frame, kind, actual, envelope);", operations, StringComparison.Ordinal);
        Assert.Contains("updated = frameChanged || boundsChanged", operations, StringComparison.Ordinal);
        Assert.Contains("LegendFittingStrategy.AdjustColumnsAndSize", operations, StringComparison.Ordinal);
        Assert.Contains("ConfigureLegend(createdLegend)", operations, StringComparison.Ordinal);
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

    [Fact]
    public void Project_save_waits_for_the_clean_snapshot_before_publishing_revision()
    {
        var operations = ReadSource("ProjectOperations.cs");

        Assert.Contains("snapshot.Project.IsDirty", operations, StringComparison.Ordinal);
        Assert.Contains("did not reach a clean project state", operations, StringComparison.Ordinal);
        Assert.Contains("snapshot = await context.Workspace.GetSnapshotAsync", operations, StringComparison.Ordinal);
    }

    [Fact]
    public void Layout_capture_reports_and_stores_the_requested_pixel_dimensions()
    {
        var operations = ReadSource("ViewOperations.cs");

        Assert.Contains("Math.Ceiling(Math.Max(width / page.Width, height / page.Height))", operations, StringComparison.Ordinal);
        Assert.Contains("bytes = ResizePng(bytes, width, height);", operations, StringComparison.Ordinal);
        Assert.Contains("width = capture.Width", operations, StringComparison.Ordinal);
        Assert.Contains("height = capture.Height", operations, StringComparison.Ordinal);
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
