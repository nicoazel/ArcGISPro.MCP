using System.Diagnostics;
using ArcGISProMCP.Core.Geoprocessing;
using ArcGISProMCP.Evals;

namespace ArcGISProMCP.Evals.Tests;

/// <summary>
/// E2: natural-language tasks routed through <see cref="ToolboxCatalog.Search"/>, as <c>gp.search</c> uses it.
/// The full suite needs an installed ArcGIS Pro; the fixture subset always runs.
/// </summary>
[Trait("Category", "eval")]
public sealed class GpSearchEvalTests
{
    private static readonly Lazy<ToolboxCatalog> Installed = new(() => new ToolboxCatalog(EvalPaths.InstalledToolboxRoot));

    [Fact]
    public void E2_tasks_expect_installed_tools()
    {
        SkipWithoutPro();
        var tasks = EvalTaskLoader.LoadJsonl(EvalPaths.Tasks("gp-search.jsonl"));

        Assert.Equal(30, tasks.Length);
        Assert.All(tasks, task => Assert.All(task.Expected, name =>
            Assert.True(Installed.Value.Describe(name) is not null, $"{task.Id}: '{name}' is not an installed tool.")));
    }

    [Fact]
    public void E2_gp_search_on_installed_pro_holds_its_baseline()
    {
        SkipWithoutPro();
        var tasks = EvalTaskLoader.LoadJsonl(EvalPaths.Tasks("gp-search.jsonl"));

        var result = EvalRunner.Run("gp-search", $"ArcGIS Pro {ProVersion()} system toolboxes ({Installed.Value.ToolCount} tools)",
            tasks, EvalSearches.Geoprocessing(Installed.Value));

        EvalReporting.ReportAndCheck(result);
    }

    [Fact]
    public void E2_holdout_tasks_expect_installed_tools()
    {
        SkipWithoutPro();
        var tasks = EvalTaskLoader.LoadJsonl(EvalPaths.Tasks("gp-search-holdout.jsonl"));

        Assert.Equal(16, tasks.Length);
        Assert.All(tasks, task => Assert.All(task.Expected, name =>
            Assert.True(Installed.Value.Describe(name) is not null, $"{task.Id}: '{name}' is not an installed tool.")));
    }

    /// <summary>Held-out tasks written before search tuning; never used to choose synonyms, weights or toolbox priority.</summary>
    [Fact]
    public void E2_gp_search_holdout_on_installed_pro_holds_its_baseline()
    {
        SkipWithoutPro();
        var tasks = EvalTaskLoader.LoadJsonl(EvalPaths.Tasks("gp-search-holdout.jsonl"));

        var result = EvalRunner.Run("gp-search-holdout", $"ArcGIS Pro {ProVersion()} system toolboxes ({Installed.Value.ToolCount} tools)",
            tasks, EvalSearches.Geoprocessing(Installed.Value));

        EvalReporting.ReportAndCheck(result);
    }

    [Fact]
    public void E2_fixture_tasks_expect_fixture_tools()
    {
        var catalog = new ToolboxCatalog(EvalPaths.FixtureToolboxRoot);
        var tasks = EvalTaskLoader.LoadJsonl(EvalPaths.Tasks("gp-search-fixture.jsonl"));

        Assert.Equal(5, tasks.Length);
        Assert.All(tasks, task => Assert.All(task.Expected, name => Assert.NotNull(catalog.Describe(name))));
    }

    [Fact]
    public void E2_gp_search_on_fixture_toolboxes_holds_its_baseline()
    {
        var catalog = new ToolboxCatalog(EvalPaths.FixtureToolboxRoot);
        var tasks = EvalTaskLoader.LoadJsonl(EvalPaths.Tasks("gp-search-fixture.jsonl"));

        var result = EvalRunner.Run("gp-search-fixture", $"synthetic fixture toolboxes ({catalog.ToolCount} tools)",
            tasks, EvalSearches.Geoprocessing(catalog));

        EvalReporting.ReportAndCheck(result);
    }

    private static void SkipWithoutPro() =>
        Assert.SkipUnless(Directory.Exists(EvalPaths.InstalledToolboxRoot),
            $"ArcGIS Pro toolboxes not found at '{EvalPaths.InstalledToolboxRoot}'.");

    private static string ProVersion()
    {
        var executable = Path.GetFullPath(Path.Combine(EvalPaths.InstalledToolboxRoot, "..", "..", "..", "bin", "ArcGISPro.exe"));
        return File.Exists(executable) ? FileVersionInfo.GetVersionInfo(executable).ProductVersion ?? "unknown" : "unknown";
    }
}
