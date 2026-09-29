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

    /// <summary>
    /// Source-text guard only: the reuse/repair decision itself is the pure
    /// <c>LayerSources.DecideExisting</c>, behavior-tested in ArcGISProMCP.Operations.Tests
    /// (LayerSourcesTests). This pins that the add-in routes through it and that removing a layer is
    /// reachable only from the broken-layer branch, never for a healthy layer.
    /// </summary>
    [Fact]
    public void Layer_add_routes_through_the_pure_decision_and_removes_only_broken_layers()
    {
        var operations = ReadSource("LayerOperations.cs");
        var add = operations[operations.IndexOf("class LayerAddOperation", StringComparison.Ordinal)..
            operations.IndexOf("class LayerSetAppearanceOperation", StringComparison.Ordinal)];

        Assert.Contains("LayerSources.DecideExisting(broken, requestIsLayerFile, actual, uri,", add, StringComparison.Ordinal);
        Assert.Contains("LayerSources.DataSourceStatus(", add, StringComparison.Ordinal);
        // The old rule compared paths only and deleted a healthy layer whose path was spelled differently.
        Assert.DoesNotContain("LayerData.SameSource", add, StringComparison.Ordinal);

        const string ReplaceCall = "Replace(map, existing, uri, requestedSource)";
        var replaceCall = add.IndexOf(ReplaceCall, StringComparison.Ordinal);
        Assert.True(replaceCall > 0, "layer.add no longer re-adds broken layers.");
        Assert.Equal(replaceCall, add.LastIndexOf(ReplaceCall, StringComparison.Ordinal));
        var healthyBranch = add.IndexOf("case ExistingLayerAction.RepairInPlaceOnly:", StringComparison.Ordinal);
        var brokenBranch = add.IndexOf("default:", healthyBranch, StringComparison.Ordinal);
        Assert.True(healthyBranch > 0 && brokenBranch > healthyBranch && replaceCall > brokenBranch,
            "Removing and re-adding a layer must be reachable only from the broken-layer branch.");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(add, @"\.RemoveLayer\("));

        // The datastore stays open until the swap is done.
        Assert.Contains("CanReplaceDataSource(dataset)", add, StringComparison.Ordinal);
        Assert.Contains("ReplaceDataSource(dataset)", add, StringComparison.Ordinal);
        Assert.Contains("using var geodatabase", add, StringComparison.Ordinal);
        Assert.Contains("\"layer_repaired\"", add, StringComparison.Ordinal);
        Assert.Contains("\"layer_source_mismatch\"", add, StringComparison.Ordinal);
        Assert.Contains("\"source_unverified\"", add, StringComparison.Ordinal);
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
