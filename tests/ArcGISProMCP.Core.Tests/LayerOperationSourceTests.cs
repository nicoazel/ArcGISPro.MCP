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
