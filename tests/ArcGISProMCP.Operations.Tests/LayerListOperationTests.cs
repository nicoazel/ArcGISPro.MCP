using System.Text.Json;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Operations.Services;
using ArcGISProMCP.Operations.Tests.Fakes;

namespace ArcGISProMCP.Operations.Tests;

public sealed class LayerListOperationTests
{
    [Fact]
    public async Task Lists_the_flattened_tree_in_drawing_order_with_elevation_for_feature_layers_only()
    {
        using var pro = new FakePro();
        var parcels = FakeLayer.Feature("Parcels", new LayerElevation("relative-to-ground", 12.5, 2));
        parcels.Transparency = 40;
        var imagery = FakeLayer.Other("Imagery", "RasterLayer");
        imagery.IsVisible = false;
        var scene = pro.State.AddMap("City", "Scene", parcels, imagery);
        pro.State.AddMap("Other", "Map", FakeLayer.Feature("Ignored"));

        var result = await pro.RunAsync("layer.list", """{"map": "City"}""");

        Assert.True(result.Success);
        Assert.Equal(1, pro.Dispatcher.MainCimCalls);
        var data = result.Data!.Value;
        Assert.Equal(FakeProState.MapHandle(scene), data.GetProperty("map").GetString());
        var layers = data.GetProperty("layers").EnumerateArray().ToArray();
        Assert.Equal(2, layers.Length);
        Assert.Equal(
            ["id", "Name", "type", "IsVisible", "Transparency", "drawingOrder", "isFeatureLayer", "elevation"],
            layers[0].EnumerateObject().Select(property => property.Name));
        Assert.Equal(FakeProState.LayerHandle(parcels), layers[0].GetProperty("id").GetString());
        Assert.Equal(0, layers[0].GetProperty("drawingOrder").GetInt32());
        Assert.Equal(40, layers[0].GetProperty("Transparency").GetDouble());
        var elevation = layers[0].GetProperty("elevation");
        Assert.Equal("relative-to-ground", elevation.GetProperty("mode").GetString());
        Assert.Equal(12.5, elevation.GetProperty("offset").GetDouble());
        Assert.Equal(2, elevation.GetProperty("verticalExaggeration").GetDouble());
        Assert.Equal("RasterLayer", layers[1].GetProperty("type").GetString());
        Assert.False(layers[1].GetProperty("IsVisible").GetBoolean());
        Assert.False(layers[1].GetProperty("isFeatureLayer").GetBoolean());
        Assert.Equal(JsonValueKind.Null, layers[1].GetProperty("elevation").ValueKind);
    }

    [Fact]
    public async Task Output_validates_against_the_declared_output_schema()
    {
        using var pro = new FakePro();
        pro.State.AddMap("City", "Scene", FakeLayer.Feature("Parcels"), FakeLayer.Other("Imagery", "RasterLayer"));

        var result = await pro.RunAsync("layer.list");

        var issues = OperationArgumentValidator.Validate(result.Data!.Value, pro.Operation("layer.list").Descriptor.OutputSchema!.Value);
        Assert.Empty(issues);
    }

    [Fact]
    public async Task Unknown_map_is_reported_by_the_host()
    {
        using var pro = new FakePro();
        pro.State.AddMap("City");

        var exception = await Assert.ThrowsAsync<OperationException>(() => pro.RunAsync("layer.list", """{"map": "Nowhere"}"""));

        Assert.Equal("map_not_found", exception.Code);
        Assert.Equal("Map 'Nowhere' was not found.", exception.Message);
    }
}
