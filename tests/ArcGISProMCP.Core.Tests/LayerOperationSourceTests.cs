namespace ArcGISProMCP.Core.Tests;

public sealed class LayerOperationSourceTests
{
    [Fact]
    public void Scene_elevation_is_registered_reported_and_applied_through_typed_sdk()
    {
        var catalog = ReadSource("ProOperationCatalog.cs");
        var operations = ReadSource("LayerOperations.cs");

        Assert.Contains("new LayerSetElevationOperation()", catalog, StringComparison.Ordinal);
        Assert.Contains("\"layer.set-elevation\"", operations, StringComparison.Ordinal);
        Assert.Contains("GetElevationTypeDefinition()", operations, StringComparison.Ordinal);
        Assert.Contains("LayerElevationType.RelativeToGround", operations, StringComparison.Ordinal);
        Assert.Contains("LayerElevationType.AtAbsoluteHeight", operations, StringComparison.Ordinal);
        Assert.Contains("CanSetElevationTypeDefinition", operations, StringComparison.Ordinal);
        Assert.Contains("SetElevationTypeDefinition", operations, StringComparison.Ordinal);
        Assert.Contains("CartographicOffset", operations, StringComparison.Ordinal);
        Assert.Contains("VerticalExaggeration", operations, StringComparison.Ordinal);
    }

    [Fact]
    public void Layer_add_reuses_only_healthy_same_source_layers_and_reports_repairs()
    {
        var operations = ReadSource("LayerOperations.cs");
        var add = operations[operations.IndexOf("class LayerAddOperation", StringComparison.Ordinal)..
            operations.IndexOf("class LayerSetAppearanceOperation", StringComparison.Ordinal)];

        Assert.Contains("LayerData.IsBroken(existing)", add, StringComparison.Ordinal);
        Assert.Contains("LayerData.SameSource(actual, uri)", add, StringComparison.Ordinal);
        Assert.Contains("CanReplaceDataSource(dataset)", add, StringComparison.Ordinal);
        Assert.Contains("ReplaceDataSource(dataset)", add, StringComparison.Ordinal);
        Assert.Contains("container.RemoveLayer(existing)", add, StringComparison.Ordinal);
        Assert.Contains("\"layer_repaired\"", add, StringComparison.Ordinal);
        Assert.Contains("bool Repaired", add, StringComparison.Ordinal);
        Assert.Contains("bool Replaced", add, StringComparison.Ordinal);
        // The reported source is what the layer actually reads, never just the request echoed back.
        Assert.Contains("LayerData.Display(actual) ?? (created ? requestedSource : null)", add, StringComparison.Ordinal);
    }

    [Fact]
    public void Layer_data_is_opened_only_through_the_broken_source_guard()
    {
        var root = AddInRoot();
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !path.EndsWith("LayerData.cs", StringComparison.Ordinal))
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                return text.Contains(".GetTable()", StringComparison.Ordinal) ||
                       text.Contains(".GetFeatureClass()", StringComparison.Ordinal);
            })
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Empty(offenders);
        var guard = File.ReadAllText(Path.Combine(root, "ArcGIS", "LayerData.cs"));
        Assert.Contains("ConnectionStatus.Broken", guard, StringComparison.Ordinal);
        Assert.Contains("OperationException.LayerDataSourceUnavailable", guard, StringComparison.Ordinal);
    }

    private static string ReadSource(string fileName) =>
        File.ReadAllText(Path.Combine(AddInRoot(), "Operations", fileName));

    private static string AddInRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "ArcGISProMCP.AddIn");
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException("Could not locate src/ArcGISProMCP.AddIn from the test output tree.");
    }
}
