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

    [Fact]
    public void Operations_project_never_references_the_ArcGIS_Pro_SDK()
    {
        var projectRoot = Path.Combine(FindRepositoryRoot(), "src", "ArcGISProMCP.Operations");
        var sources = Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.NotEmpty(sources);
        foreach (var file in sources)
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("using ArcGIS.", source, StringComparison.Ordinal);
            Assert.DoesNotContain("global::ArcGIS.", source, StringComparison.Ordinal);
        }

        var project = File.ReadAllText(Path.Combine(projectRoot, "ArcGISProMCP.Operations.csproj"));
        Assert.DoesNotContain("Esri.", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ArcGISProMCP.AddIn.csproj", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<UseWPF>", project, StringComparison.OrdinalIgnoreCase);
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
