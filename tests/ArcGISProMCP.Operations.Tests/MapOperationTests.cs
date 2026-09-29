using System.Text.Json;
using ArcGISProMCP.Operations.Services;
using ArcGISProMCP.Operations.Tests.Fakes;

namespace ArcGISProMCP.Operations.Tests;

public sealed class MapOperationTests
{
    [Fact]
    public async Task List_returns_every_map_sorted_by_name_with_handles_and_view_state()
    {
        using var pro = new FakePro();
        pro.State.AddMap("Zoning", "Map", FakeLayer.Feature("Parcels"), FakeLayer.Feature("Zones"));
        pro.State.AddMap("Buildings", "Scene", FakeLayer.Feature("Massing"));
        pro.State.ActiveMapName = "Zoning";

        var result = await pro.RunAsync("map.list");

        Assert.True(result.Success);
        Assert.Equal(1, pro.Dispatcher.MainCimCalls);
        var maps = result.Data!.Value.EnumerateArray().ToArray();
        Assert.Equal(["Buildings", "Zoning"], maps.Select(map => map.GetProperty("Name").GetString()));
        Assert.Equal("pro://map/CIMPATH%3Dmap%2Fzoning.xml", maps[1].GetProperty("id").GetString());
        Assert.Equal("Scene", maps[0].GetProperty("type").GetString());
        Assert.Equal(2, maps[1].GetProperty("layerCount").GetInt32());
        Assert.True(maps[1].GetProperty("isActive").GetBoolean());
        Assert.False(maps[0].GetProperty("isActive").GetBoolean());
        Assert.Equal(["id", "Name", "type", "layerCount", "isActive"], maps[0].EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task Ensure_returns_an_existing_map_without_creating_it()
    {
        using var pro = new FakePro();
        pro.State.AddMap("Transit");

        var result = await pro.RunAsync("map.ensure", """{"name": "transit", "type": "scene"}""");

        Assert.True(result.Success);
        Assert.False(result.Data!.Value.GetProperty("Created").GetBoolean());
        Assert.Equal("Transit", result.Data!.Value.GetProperty("Name").GetString());
        Assert.Single(pro.State.Maps);
        Assert.Empty(pro.State.Calls);
    }

    [Theory]
    [InlineData(null, "Map")]
    [InlineData("map", "Map")]
    [InlineData("scene", "LocalScene")]
    [InlineData("global-scene", "GlobalScene")]
    [InlineData("GLOBAL-SCENE", "GlobalScene")]
    public async Task Ensure_creates_the_requested_view_kind(string? type, string expected)
    {
        using var pro = new FakePro();
        var arguments = type is null ? """{"name": "Massing"}""" : $$"""{"name": "Massing", "type": "{{type}}"}""";

        var result = await pro.RunAsync("map.ensure", arguments);

        Assert.True(result.Success);
        Assert.True(result.Data!.Value.GetProperty("Created").GetBoolean());
        Assert.Equal(["Id", "Name", "Created"], result.Data!.Value.EnumerateObject().Select(property => property.Name));
        var created = Assert.Single(pro.State.Maps);
        Assert.Equal(Enum.Parse<MapViewKind>(expected), created.CreatedAs);
        Assert.Equal("None", created.Basemap);
    }

    [Theory]
    [InlineData("Imagery", "Satellite")]
    [InlineData("imagery", "Satellite")]
    [InlineData("Open Street Map", "OpenStreetMap")]
    [InlineData("topographic", "Topographic")]
    public async Task Ensure_normalizes_basemap_names_to_the_host_name(string basemap, string expected)
    {
        using var pro = new FakePro();

        await pro.RunAsync("map.ensure", JsonSerializer.Serialize(new { name = "Base", basemap }));

        Assert.Equal(expected, Assert.Single(pro.State.Maps).Basemap);
    }

    [Fact]
    public async Task Ensure_rejects_an_unknown_basemap_before_touching_the_project()
    {
        using var pro = new FakePro();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            pro.RunAsync("map.ensure", """{"name": "Base", "basemap": "Moon"}"""));

        Assert.Equal(
            "Unknown basemap 'Moon'. Use a Pro basemap name such as None, Topographic, Streets, Imagery, or OpenStreetMap.",
            exception.Message);
        Assert.Equal(0, pro.Dispatcher.MainCimCalls);
        Assert.Empty(pro.State.Maps);
    }

    [Theory]
    [InlineData("map", "Map")]
    [InlineData("scene", "LocalScene")]
    [InlineData("local-scene", "LocalScene")]
    [InlineData("Scene", "LocalScene")]
    [InlineData("global-scene", "GlobalScene")]
    [InlineData("anything", "Map")]
    public void View_kind_follows_the_original_type_mapping(string type, string expected) =>
        Assert.Equal(Enum.Parse<MapViewKind>(expected), MapEnsureOperation.ToViewKind(type));

    [Fact]
    public async Task Activate_resolves_on_the_CIM_thread_then_opens_or_activates_on_the_UI_thread()
    {
        using var pro = new FakePro();
        var transit = pro.State.AddMap("Transit");
        pro.State.AddMap("Zoning");

        var opened = await pro.RunAsync("map.activate", """{"map": "transit"}""");
        var activated = await pro.RunAsync("map.activate", JsonSerializer.Serialize(new { map = FakeProState.MapHandle(transit) }));

        Assert.True(opened.Success);
        Assert.Equal(["map.open-view Transit", "map.activate-pane Transit"], pro.State.Calls);
        Assert.Equal(2, pro.Dispatcher.MainCimCalls);
        Assert.Equal(2, pro.Dispatcher.UiCalls);
        Assert.Equal(FakeProState.MapHandle(transit), activated.Data!.Value.GetProperty("id").GetString());
        Assert.Equal("Transit", activated.Data!.Value.GetProperty("Name").GetString());
        Assert.Equal("Transit", pro.State.ActiveMapName);
    }

    [Fact]
    public async Task Activate_surfaces_an_unknown_map()
    {
        using var pro = new FakePro();
        pro.State.AddMap("Zoning");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => pro.RunAsync("map.activate", """{"map": "Nope"}"""));

        Assert.Equal("Map 'Nope' was not found.", exception.Message);
        Assert.Equal(0, pro.Dispatcher.UiCalls);
    }

    [Fact]
    public async Task Clear_selection_defaults_to_the_active_map()
    {
        using var pro = new FakePro();
        pro.State.AddMap("Zoning").SelectionCount = 3;
        var transit = pro.State.AddMap("Transit");
        transit.SelectionCount = 5;
        pro.State.ActiveMapName = "Transit";

        var result = await pro.RunAsync("map.clear-selection");

        Assert.True(result.Success);
        Assert.Equal(0, transit.SelectionCount);
        Assert.Equal(3, pro.State.Maps[0].SelectionCount);
        Assert.Equal(FakeProState.MapHandle(transit), result.Data!.Value.GetProperty("map").GetString());
        Assert.True(result.Data!.Value.GetProperty("cleared").GetBoolean());
        Assert.Equal("rev-1", result.WorkspaceRevision);
    }

    [Fact]
    public async Task Ensure_waits_for_the_structural_notification_only_after_creating()
    {
        using var pro = new FakePro(mapStructuralSettleDelay: TimeSpan.FromMilliseconds(300));
        pro.State.AddMap("Existing");

        var existing = System.Diagnostics.Stopwatch.StartNew();
        await pro.RunAsync("map.ensure", """{"name": "Existing"}""");
        existing.Stop();
        var created = System.Diagnostics.Stopwatch.StartNew();
        await pro.RunAsync("map.ensure", """{"name": "New"}""");
        created.Stop();

        Assert.True(created.Elapsed >= TimeSpan.FromMilliseconds(250), $"created in {created.Elapsed}");
        Assert.True(existing.Elapsed < TimeSpan.FromMilliseconds(250), $"existing in {existing.Elapsed}");
        Assert.Equal(TimeSpan.FromSeconds(5), MapEnsureOperation.DefaultStructuralSettleDelay);
    }
}
