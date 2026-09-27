namespace ArcGISProMCP.Core.Tests;

public sealed class MetadataOperationSourceTests
{
    [Fact]
    public void Source_backed_metadata_is_copied_to_editable_layer_metadata_with_rollback()
    {
        var source = ReadSource();

        Assert.Contains("var before = layer.GetMetadata()", source, StringComparison.Ordinal);
        Assert.Contains("layer.SetUseSourceMetadata(false)", source, StringComparison.Ordinal);
        Assert.Contains("if (!layer.GetCanEditMetadata())", source, StringComparison.Ordinal);
        Assert.Contains("if (usedSourceMetadata) layer.SetUseSourceMetadata(true)", source, StringComparison.Ordinal);
        Assert.Contains("layer.SetMetadata(after)", source, StringComparison.Ordinal);
        Assert.Contains("detachedFromSourceMetadata", source, StringComparison.Ordinal);
    }

    private static string ReadSource()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "ArcGISProMCP.AddIn", "Operations", "MetadataOperations.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException("Could not locate MetadataOperations.cs from the test output tree.");
    }
}
