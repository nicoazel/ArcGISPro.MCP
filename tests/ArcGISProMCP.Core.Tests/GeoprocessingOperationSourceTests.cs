namespace ArcGISProMCP.Core.Tests;

/// <summary>
/// The add-in's catalog composition (one shared ToolboxCatalog.Default) is only visible in its
/// source. gp.* behavior is tested against the real operations in Operations.Tests.
/// </summary>
public sealed class GeoprocessingOperationSourceTests
{
    [Fact]
    public void Catalog_registers_every_gp_operation_with_one_shared_toolbox_catalog()
    {
        var source = ReadSource("ProOperationCatalog.cs");

        Assert.Equal(1, Count(source, "ToolboxCatalog.Default"));
        Assert.Contains("new GeoprocessingSearchOperation(toolboxes)", source, StringComparison.Ordinal);
        Assert.Contains("new GeoprocessingDescribeOperation(toolboxes)", source, StringComparison.Ordinal);
        Assert.Contains("new GeoprocessingQueryOperation(toolboxes, services.Geoprocessing)", source, StringComparison.Ordinal);
        Assert.Contains("new GeoprocessingRunOperation(toolboxes, services.Geoprocessing)", source, StringComparison.Ordinal);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
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
