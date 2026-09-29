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
}
