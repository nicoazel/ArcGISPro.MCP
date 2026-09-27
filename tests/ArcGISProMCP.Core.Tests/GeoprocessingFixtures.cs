using System.IO.Compression;

namespace ArcGISProMCP.Core.Tests;

/// <summary>Paths to the synthetic toolbox fixtures and scratch directories for toolbox tests.</summary>
internal static class GeoprocessingFixtures
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "Fixtures", "toolboxes");

    public static string FixtureToolbox => Path.Combine(Root, "fixture.tbx");

    public static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "arcgispro-mcp-gp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Zips the fixture directory toolbox into an .atbx (same layout Pro uses).</summary>
    public static string CreateAtbx(string directory, string? fileName = null)
    {
        var path = Path.Combine(directory, fileName ?? "fixture.atbx");
        ZipFile.CreateFromDirectory(FixtureToolbox, path, CompressionLevel.Fastest, includeBaseDirectory: false);
        return path;
    }
}
