namespace ArcGISProMCP.Core.Tests;

public sealed class ArcGisOnlySeparationTests
{
    [Fact]
    public void Runtime_source_has_no_rhino_dependency_or_operations()
    {
        var root = FindRepositoryRoot();
        var sourceRoot = Path.Combine(root, "src");
        var files = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("RhinoCommon", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("RhinoInside", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("rhino.", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("RhinoUiThread", source, StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ArcGISPro.MCP.slnx")))
                return directory.FullName;

        throw new DirectoryNotFoundException("Could not locate the ArcGISPro.MCP repository root.");
    }
}
