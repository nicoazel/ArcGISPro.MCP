using System.Diagnostics;
using ArcGISProMCP.Core.Geoprocessing;

namespace ArcGISProMCP.Core.Tests;

/// <summary>Sanity checks against a locally installed ArcGIS Pro. Skipped when Pro is not installed.</summary>
public sealed class GeoprocessingRealInstallTests
{
    private static readonly string InstalledRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "ArcGIS", "Pro", "Resources", "ArcToolBox", "toolboxes");

    [Fact]
    public void Indexes_the_installed_system_toolboxes()
    {
        Assert.SkipUnless(Directory.Exists(InstalledRoot), $"ArcGIS Pro toolboxes not found at '{InstalledRoot}'.");

        var catalog = new ToolboxCatalog(InstalledRoot);
        var stopwatch = Stopwatch.StartNew();
        var count = catalog.ToolCount;
        stopwatch.Stop();

        Assert.True(count > 1000, $"Only {count} tools indexed.");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Indexing took {stopwatch.Elapsed.TotalMilliseconds:F0} ms.");
        TestContext.Current.SendDiagnosticMessage(
            $"Indexed {count} tools in {catalog.Toolboxes.Count} toolboxes in {stopwatch.Elapsed.TotalMilliseconds:F0} ms; {catalog.Warnings.Count} warnings.");

        var buffer = catalog.Describe("analysis.Buffer");
        Assert.NotNull(buffer);
        Assert.Contains(buffer.Parameters, parameter => parameter.Name == "in_features" && parameter.Required);
        Assert.Equal("analysis.Buffer", catalog.Search("buffer")[0].Tool.ExecutionName);
    }

    [Fact]
    public void Curated_policy_names_exist_in_the_installed_toolboxes()
    {
        Assert.SkipUnless(Directory.Exists(InstalledRoot), $"ArcGIS Pro toolboxes not found at '{InstalledRoot}'.");

        var catalog = new ToolboxCatalog(InstalledRoot);

        foreach (var name in GeoprocessingRiskPolicy.ReadOnlyQueryTools)
            Assert.Equal(GpRiskTier.ReadOnlyQuery, catalog.Describe(name)?.Risk.Tier);
        foreach (var name in GeoprocessingRiskPolicy.CuratedDestructiveTools)
            Assert.Equal(GpRiskTier.Destructive, catalog.Describe(name)?.Risk.Tier);
        var calculateField = catalog.Describe("management.CalculateField")!.Risk;
        Assert.Equal(GpRiskTier.Destructive, calculateField.Tier);
        Assert.True(calculateField.AcceptsPythonExpression);
        Assert.Equal(GpRiskTier.UserCode, catalog.Describe("mb.CalculateValue")?.Risk.Tier);
    }
}
