namespace ArcGISProMCP.Operations.Tests;

/// <summary>Which ArcGIS map member events advance the workspace revision.</summary>
public sealed class WorkspaceEventHintsTests
{
    [Theory]
    [InlineData("ConnectionStatus")]
    [InlineData("Expansion")]
    [InlineData("ConnectionStatus", "Expansion")]
    public void Connection_and_expansion_only_events_are_not_content(params string[] hints) =>
        Assert.True(WorkspaceEventHints.IsNonContentOnly(hints));

    [Theory]
    [InlineData("Renderer")]
    [InlineData("DataSource")]
    [InlineData("Name")]
    [InlineData("ConnectionStatus", "DataSource")]
    [InlineData("Expansion", "Visibility")]
    public void Any_content_hint_advances_the_revision(params string[] hints) =>
        Assert.False(WorkspaceEventHints.IsNonContentOnly(hints));

    [Fact]
    public void An_event_without_hints_is_treated_as_content() =>
        Assert.False(WorkspaceEventHints.IsNonContentOnly([]));

    [Fact]
    public void Hint_names_are_matched_exactly() =>
        Assert.False(WorkspaceEventHints.IsNonContentOnly(["connectionstatus"]));

    [Theory]
    [InlineData("MapFrameNavigated")]
    [InlineData("MapFrameActivated")]
    [InlineData("MapFrameDeactivated")]
    [InlineData("SelectionChanged")]
    public void Frame_navigation_and_selection_are_view_state(string hint) =>
        Assert.True(WorkspaceEventHints.IsAutomaticElementChange(hint, ["other"]));

    [Theory]
    [InlineData("PropertyChanged", "surround")]
    [InlineData("PropertyChanged", "mapframe")]
    [InlineData("PropertyChanged", "surround", "mapframe")]
    [InlineData("PlacementChanged", "surround")]
    public void Surround_and_frame_refreshes_are_automatic(string hint, params string[] kinds) =>
        Assert.True(WorkspaceEventHints.IsAutomaticElementChange(hint, kinds));

    [Theory]
    [InlineData("PropertyChanged", "other")]
    [InlineData("PropertyChanged", "surround", "other")]
    [InlineData("PlacementChanged", "mapframe")]
    [InlineData("PlacementChanged", "other")]
    [InlineData("ElementAdded", "surround")]
    [InlineData("ElementRemoved", "mapframe")]
    [InlineData("StyleChanged", "surround")]
    public void Layout_edits_still_advance_the_revision(string hint, params string[] kinds) =>
        Assert.False(WorkspaceEventHints.IsAutomaticElementChange(hint, kinds));

    [Fact]
    public void A_property_change_without_elements_counts_as_content() =>
        Assert.False(WorkspaceEventHints.IsAutomaticElementChange("PropertyChanged", []));
}
