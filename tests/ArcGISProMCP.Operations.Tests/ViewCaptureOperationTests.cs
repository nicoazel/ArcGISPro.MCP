using System.Text;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Operations.Tests.Fakes;

namespace ArcGISProMCP.Operations.Tests;

public sealed class ViewCaptureOperationTests
{
    [Fact]
    public async Task Layout_capture_reports_and_stores_the_requested_pixel_dimensions()
    {
        using var pro = new FakePro();
        var layout = pro.State.AddLayout("Poster");

        var result = await pro.RunAsync("view.capture", """{"view": "layout", "layout": "Poster", "width": 800, "height": 600}""");

        Assert.True(result.Success);
        // The export runs on the CIM thread, overshoots the requested size, and is resized back.
        Assert.Equal(["view.export-layout Poster 800x600", "view.resize 800x600"], pro.State.Calls);
        Assert.Equal(1, pro.Dispatcher.MainCimCalls);
        var data = result.Data!.Value;
        Assert.Equal(800, data.GetProperty("width").GetInt32());
        Assert.Equal(600, data.GetProperty("height").GetInt32());
        Assert.Equal("layout", data.GetProperty("sourceKind").GetString());
        Assert.Equal("Poster", data.GetProperty("sourceName").GetString());
        Assert.Equal(layout.Uri, data.GetProperty("sourceUri").GetString());
        var resource = Assert.Single(result.Resources);
        Assert.Equal("image/png", resource.MimeType);
        Assert.Equal(resource.Uri, data.GetProperty("resource").GetString());
        Assert.True(pro.Resources.TryGetLocalPath(resource.Uri, out var stored));
        Assert.Equal("layout:Poster@800x600", Encoding.UTF8.GetString(File.ReadAllBytes(stored)));
        // The temporary export is always removed.
        Assert.False(File.Exists(Assert.Single(pro.Views.ExportedPaths)));
    }

    [Fact]
    public async Task Layout_capture_without_a_reference_uses_the_active_layout()
    {
        using var pro = new FakePro();
        pro.State.AddLayout("Poster");
        pro.State.AddLayout("Handout");
        pro.State.ActiveLayoutName = "Handout";

        var result = await pro.RunAsync("view.capture", """{"view": "LAYOUT"}""");

        Assert.Equal("Handout", result.Data!.Value.GetProperty("sourceName").GetString());
        Assert.Equal(1280, result.Data!.Value.GetProperty("width").GetInt32());
        Assert.Equal(800, result.Data!.Value.GetProperty("height").GetInt32());
    }

    [Fact]
    public async Task Failed_layout_export_still_removes_the_temporary_file()
    {
        using var pro = new FakePro();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => pro.RunAsync("view.capture", """{"view": "layout"}"""));

        Assert.Equal("Specify a layout or activate one before capture.", exception.Message);
        Assert.Empty(pro.Views.ExportedPaths);
    }

    [Fact]
    public async Task Map_capture_runs_on_the_UI_thread_and_reports_the_captured_view()
    {
        using var pro = new FakePro();
        var map = pro.State.AddMap("Zoning");
        pro.State.ActiveMapName = "Zoning";

        var result = await pro.RunAsync("view.capture", """{"width": 640, "height": 480}""");

        Assert.True(result.Success);
        Assert.Equal(1, pro.Dispatcher.UiCalls);
        Assert.Equal(0, pro.Dispatcher.MainCimCalls);
        var data = result.Data!.Value;
        Assert.Equal("map", data.GetProperty("sourceKind").GetString());
        Assert.Equal("Zoning", data.GetProperty("sourceName").GetString());
        Assert.Equal(map.Uri, data.GetProperty("sourceUri").GetString());
        Assert.Equal(640, data.GetProperty("width").GetInt32());
        var issues = OperationArgumentValidator.Validate(data, pro.Operation("view.capture").Descriptor.OutputSchema!.Value);
        Assert.Empty(issues);
    }

    [Theory]
    [InlineData(63, 480)]
    [InlineData(640, 4097)]
    public async Task Dimensions_outside_64_to_4096_are_rejected_before_capture(int width, int height)
    {
        using var pro = new FakePro();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            pro.RunAsync("view.capture", $$"""{"width": {{width}}, "height": {{height}}}"""));

        Assert.Equal(0, pro.Dispatcher.UiCalls);
        Assert.Empty(pro.State.Calls);
    }
}
