using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.Operations.Tests;

/// <summary>
/// The pure page and surround rules behind layout.add-map-frame, layout.ensure-surround and
/// layout.set-text. The add-in feeds them what ArcGIS reports.
/// </summary>
public sealed class LayoutGeometryTests
{
    [Theory]
    [InlineData(0.5, 0.5, 12, 10)]
    [InlineData(0, 0, 17, 11)]
    // Within the 0.01 in tolerance of the page edge.
    [InlineData(-0.005, 0, 17.01, 11.005)]
    public void Boxes_on_the_page_are_accepted(double x, double y, double width, double height) =>
        LayoutGeometry.EnsureOnPage("Map frame 'Main'", new PageBox(x, y, width, height), 17, 11, "Inch");

    [Theory]
    // The live operation matrix placed a 5 x 5 frame at (40, 40) on a 17 x 11 page.
    [InlineData(40, 40, 5, 5)]
    [InlineData(-1, 1, 2, 2)]
    [InlineData(1, -0.5, 2, 2)]
    [InlineData(15, 1, 2.5, 1)]
    [InlineData(1, 10, 2, 1.5)]
    public void Boxes_off_the_page_are_refused_with_the_page_size(double x, double y, double width, double height)
    {
        var error = Assert.Throws<OperationException>(() =>
            LayoutGeometry.EnsureOnPage("Map frame 'Off Page Frame'", new PageBox(x, y, width, height), 17, 11, "Inch"));

        Assert.Equal(OperationErrorCodes.ElementOutsidePage, error.Code);
        Assert.Equal("element_outside_page", error.Code);
        Assert.Contains("Map frame 'Off Page Frame'", error.Message, StringComparison.Ordinal);
        Assert.Contains("17 x 11 Inch page", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_anchors_must_be_on_the_page()
    {
        LayoutGeometry.EnsurePointOnPage("Text 'Title'", 12.9, 9.9, 17, 11, "Inch");
        var error = Assert.Throws<OperationException>(() => LayoutGeometry.EnsurePointOnPage("Text 'Title'", 20, 9.9, 17, 11, "Inch"));
        Assert.Equal("element_outside_page", error.Code);
        Assert.Contains("Text 'Title'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_surround_at_the_requested_box_is_exact()
    {
        var box = new PageBox(12.9, 0.9, 2.5, 0.5);
        Assert.Equal(SurroundFit.Exact, LayoutGeometry.FitSurround("scale-bar", box, box with { Width = 2.51 }));
    }

    [Theory]
    // Style height, width rounded to whole divisions: the live failure was a 2.5 x 0.5 request.
    [InlineData(2.5, 0.62)]
    [InlineData(2.31, 0.5)]
    [InlineData(2.5, 0.3)]
    [InlineData(3.2, 0.75)]
    public void A_scale_bar_may_take_its_height_and_rounded_width_from_its_style(double width, double height)
    {
        var requested = new PageBox(12.9, 0.9, 2.5, 0.5);

        Assert.Equal(SurroundFit.Resized, LayoutGeometry.FitSurround("scale-bar", requested, new PageBox(12.9, 0.9, width, height)));
    }

    [Theory]
    // Not anchored where requested.
    [InlineData(13.2, 0.9, 2.5, 0.5)]
    [InlineData(12.9, 1.0, 2.5, 0.5)]
    // A width no division rounding explains.
    [InlineData(12.9, 0.9, 1.0, 0.5)]
    [InlineData(12.9, 0.9, 4.0, 0.5)]
    // Empty.
    [InlineData(12.9, 0.9, 2.5, 0)]
    public void A_scale_bar_off_its_anchor_or_far_from_its_width_is_rejected(double x, double y, double width, double height)
    {
        var requested = new PageBox(12.9, 0.9, 2.5, 0.5);

        Assert.Equal(SurroundFit.Rejected, LayoutGeometry.FitSurround("scale-bar", requested, new PageBox(x, y, width, height)));
    }

    [Fact]
    public void Legends_fit_inside_the_requested_box()
    {
        var requested = new PageBox(12.9, 3.0, 3.6, 5.6);

        // Live result: a fitted legend smaller than the box.
        Assert.Equal(SurroundFit.Resized, LayoutGeometry.FitSurround("legend", requested, new PageBox(12.9, 3.0, 1.6237, 2.9437)));
        Assert.Equal(SurroundFit.Rejected, LayoutGeometry.FitSurround("legend", requested, new PageBox(12.9, 3.0, 4.2, 2.9)));
        Assert.Equal(SurroundFit.Rejected, LayoutGeometry.FitSurround("legend", requested, new PageBox(12.9, 3.0, 0.1, 0.1)));
    }

    [Fact]
    public void North_arrows_keep_proportions_inside_the_box_with_one_dimension_converged()
    {
        var requested = new PageBox(15.7, 0.9, 0.7, 1.1);

        // Live result: height converged, width follows the arrow's proportions.
        Assert.Equal(SurroundFit.Resized, LayoutGeometry.FitSurround("north-arrow", requested, new PageBox(15.7, 0.9, 0.5273, 1.1081)));
        Assert.Equal(SurroundFit.Rejected, LayoutGeometry.FitSurround("north-arrow", requested, new PageBox(15.7, 0.9, 0.5, 0.8)));
        Assert.Equal(SurroundFit.Rejected, LayoutGeometry.FitSurround("north-arrow", requested, new PageBox(15.7, 0.9, 0.7, 1.5)));
    }

    [Fact]
    public void The_resized_notice_reports_actual_and_requested_sizes()
    {
        var message = LayoutGeometry.ResizedMessage("scale-bar", "Scale Bar", new PageBox(12.9, 0.9, 2.5, 0.5), new PageBox(12.9, 0.9, 2.4, 0.6234));

        Assert.Contains("scale-bar 'Scale Bar'", message, StringComparison.Ordinal);
        Assert.Contains("2.4 x 0.623", message, StringComparison.Ordinal);
        Assert.Contains("requested 2.5 x 0.5", message, StringComparison.Ordinal);
    }
}
