using ArcGISProMCP.Core.Geoprocessing;

namespace ArcGISProMCP.Core.Tests;

public sealed class GeoprocessingCatalogTests
{
    private static ToolboxCatalog Catalog() => new(GeoprocessingFixtures.Root);

    [Fact]
    public void Indexes_every_system_toolbox_under_the_root()
    {
        var catalog = Catalog();

        Assert.Empty(catalog.Warnings);
        Assert.Equal(13, catalog.ToolCount);
        Assert.Equal(13, catalog.Tools.Count);
        Assert.Equal(["fixture", "management"], catalog.Toolboxes.Select(toolbox => toolbox.Alias));
        Assert.All(catalog.Toolboxes, toolbox => Assert.True(toolbox.IsSystem));
    }

    [Fact]
    public void Search_ranks_name_matches_above_keyword_and_description_matches_and_demotes_deprecated_tools()
    {
        var hits = Catalog().Search("buffer");

        Assert.Equal(["fixture.BufferZones", "fixture.OldBuffer"], hits.Select(hit => hit.Tool.ExecutionName));
        Assert.True(hits[0].Score > hits[1].Score);
        Assert.True(hits[1].Tool.Deprecated);
    }

    [Fact]
    public void Search_matches_keywords_and_reports_matched_terms()
    {
        var hits = Catalog().Search("surround distance");

        Assert.Equal("fixture.BufferZones", hits[0].Tool.ExecutionName);
        Assert.Equal(["surround", "distance"], hits[0].MatchedTerms);
    }

    [Fact]
    public void Search_by_display_name_words_and_summary()
    {
        var catalog = Catalog();

        Assert.Equal("fixture.EraseRows", catalog.Search("erase rows")[0].Tool.ExecutionName);
        Assert.Equal("fixture.GeocodeOnline", catalog.Search("addresses")[0].Tool.ExecutionName);
        Assert.Empty(catalog.Search("nonexistentword"));
    }

    [Fact]
    public void Exact_execution_name_ranks_first()
    {
        var hits = Catalog().Search("management.GetCount");

        Assert.Equal("management.GetCount", hits[0].Tool.ExecutionName);
        Assert.Equal(GpRiskTier.ReadOnlyQuery, hits[0].Tool.RiskTier);
    }

    [Fact]
    public void Search_honours_limit_and_empty_query_lists_non_deprecated_tools()
    {
        var catalog = Catalog();

        Assert.Single(catalog.Search("records", 1));
        var all = catalog.Search(null, 100);
        Assert.Equal(12, all.Count);
        Assert.DoesNotContain(all, hit => hit.Tool.Deprecated);
        Assert.Equal(all.Select(hit => hit.Tool.ExecutionName).Order(StringComparer.OrdinalIgnoreCase), all.Select(hit => hit.Tool.ExecutionName));
    }

    [Fact]
    public void Summary_carries_toolbox_toolset_type_and_tier()
    {
        var summary = Catalog().Search("fixture.ScriptSummary")[0].Tool;

        Assert.Equal("ScriptSummary", summary.Name);
        Assert.Equal("Script Summary", summary.DisplayName);
        Assert.Equal("Summarizes a table with a script.", summary.Summary);
        Assert.Equal("Fixture Tools", summary.Toolbox);
        Assert.Equal("fixture", summary.ToolboxAlias);
        Assert.Equal("Scripted", summary.Toolset);
        Assert.Equal("ScriptTool", summary.ToolType);
        Assert.False(summary.Deprecated);
        Assert.True(summary.IsSystem);
    }

    [Fact]
    public void Describe_accepts_case_insensitive_and_arcpy_underscore_names()
    {
        var catalog = Catalog();

        Assert.Equal("fixture.BufferZones", catalog.Describe("FIXTURE.bufferzones")!.Tool.ExecutionName);
        Assert.Equal("fixture.BufferZones", catalog.Describe("BufferZones_fixture")!.Tool.ExecutionName);
        Assert.Null(catalog.Describe("fixture.Missing"));
        Assert.Null(catalog.Describe(""));
        Assert.EndsWith("fixture.tbx", catalog.Describe("fixture.BufferZones")!.ToolboxPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void User_atbx_is_indexed_through_the_zip_source()
    {
        var atbx = GeoprocessingFixtures.CreateAtbx(GeoprocessingFixtures.CreateTempDirectory());
        var catalog = new ToolboxCatalog(GeoprocessingFixtures.CreateTempDirectory(), [atbx]);

        var toolbox = Assert.Single(catalog.Toolboxes);
        Assert.Equal(GpToolboxKind.Archive, toolbox.Kind);
        Assert.False(toolbox.IsSystem);
        Assert.Equal(10, catalog.ToolCount);
        Assert.Equal("fixture.BufferZones", catalog.Search("buffer zones")[0].Tool.ExecutionName);
        Assert.False(catalog.Describe("fixture.BufferZones")!.Tool.IsSystem);
        // Deprecation comes from the system deprecated.list only.
        Assert.False(catalog.Describe("fixture.OldBuffer")!.Tool.Deprecated);
    }

    [Fact]
    public void System_toolboxes_may_list_tools_from_sibling_toolboxes()
    {
        var copy = Catalog().Describe("management.RasterCopy");

        Assert.NotNull(copy);
        Assert.Equal("RasterCopy", copy.Tool.Name);
        Assert.Equal("Fast Raster", copy.Tool.DisplayName);
        Assert.Equal("Fixture Management Tools", copy.Tool.Toolbox);
        Assert.Equal(["in_raster", "out_raster"], copy.Parameters.Select(parameter => parameter.Name));
    }

    [Fact]
    public void User_toolboxes_cannot_reference_folders_outside_themselves()
    {
        var directory = GeoprocessingFixtures.CreateTempDirectory();
        var toolbox = Path.Combine(directory, "escape.tbx");
        Directory.CreateDirectory(toolbox);
        File.WriteAllText(Path.Combine(toolbox, "toolbox.content"),
            "{\"alias\": \"escape\", \"toolsets\": {\"<root>\": {\"tools\": [\"Leak:..\\\\other.tbx\\\\Leak.tool\"]}}}");
        var other = Path.Combine(directory, "other.tbx", "Leak.tool");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "tool.content"), "{\"type\": \"FunctionTool\", \"params\": {}}");

        var catalog = new ToolboxCatalog(GeoprocessingFixtures.CreateTempDirectory(), [toolbox]);

        Assert.Equal(0, catalog.ToolCount);
        Assert.Contains(catalog.Warnings, warning => warning.Contains("escape.Leak", StringComparison.Ordinal));
    }

    [Fact]
    public void Python_and_legacy_binary_toolboxes_are_listed_unindexed()
    {
        var root = GeoprocessingFixtures.CreateTempDirectory();
        File.WriteAllBytes(Path.Combine(root, "Legacy Tools.tbx"), [0xD0, 0xCF, 0x11, 0xE0]);
        var userDirectory = GeoprocessingFixtures.CreateTempDirectory();
        var pyt = Path.Combine(userDirectory, "Custom.pyt");
        File.WriteAllText(pyt, "class Toolbox(object):\n    pass\n");

        var catalog = new ToolboxCatalog(root, [pyt, Path.Combine(userDirectory, "missing.atbx")]);

        Assert.Equal(0, catalog.ToolCount);
        var legacy = catalog.Toolboxes.Single(toolbox => toolbox.Kind == GpToolboxKind.LegacyBinary);
        Assert.False(legacy.Indexed);
        Assert.True(legacy.IsSystem);
        Assert.True(legacy.MayExecuteUserCode);
        var python = catalog.Toolboxes.Single(toolbox => toolbox.Kind == GpToolboxKind.PythonToolbox);
        Assert.False(python.Indexed);
        Assert.Equal(ToolboxCatalog.UnindexedUserCodeReason, python.UnindexedReason);
        Assert.True(python.MayExecuteUserCode);
        Assert.Contains(catalog.Warnings, warning => warning.Contains("missing.atbx", StringComparison.Ordinal));
    }

    [Fact]
    public void Alias_collisions_keep_the_system_tool_and_warn()
    {
        var atbx = GeoprocessingFixtures.CreateAtbx(GeoprocessingFixtures.CreateTempDirectory());
        var catalog = new ToolboxCatalog(GeoprocessingFixtures.Root, [atbx]);

        Assert.Equal(13, catalog.ToolCount);
        Assert.True(catalog.Describe("fixture.ScriptSummary")!.Tool.IsSystem);
        Assert.Contains(catalog.Warnings, warning => warning.Contains("Duplicate tool 'fixture.BufferZones'", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_root_yields_an_empty_catalog_with_a_warning()
    {
        var catalog = new ToolboxCatalog(Path.Combine(GeoprocessingFixtures.CreateTempDirectory(), "absent"));

        Assert.Equal(0, catalog.ToolCount);
        Assert.Empty(catalog.Search("buffer"));
        Assert.Contains(catalog.Warnings, warning => warning.Contains(ToolboxCatalog.RootEnvironmentVariable, StringComparison.Ordinal));
    }

    [Fact]
    public void Index_is_cached_until_the_root_changes()
    {
        var root = GeoprocessingFixtures.CreateTempDirectory();
        CopyDirectory(Path.Combine(GeoprocessingFixtures.Root, "mgmt.tbx"), Path.Combine(root, "mgmt.tbx"));
        Directory.SetLastWriteTimeUtc(root, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var catalog = new ToolboxCatalog(root);
        var first = catalog.Toolboxes;

        Assert.Same(first, catalog.Toolboxes);
        Assert.Equal(2, catalog.ToolCount);
        Assert.Contains(catalog.Warnings, warning => warning.Contains("management.RasterCopy", StringComparison.Ordinal));

        CopyDirectory(GeoprocessingFixtures.FixtureToolbox, Path.Combine(root, "fixture.tbx"));
        Directory.SetLastWriteTimeUtc(root, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.NotSame(first, catalog.Toolboxes);
        Assert.Equal(13, catalog.ToolCount);
    }

    [Fact]
    public void Default_root_honours_the_environment_override()
    {
        var previous = Environment.GetEnvironmentVariable(ToolboxCatalog.RootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ToolboxCatalog.RootEnvironmentVariable, GeoprocessingFixtures.Root);
            Assert.Equal(GeoprocessingFixtures.Root, ToolboxCatalog.ResolveDefaultRoot());

            Environment.SetEnvironmentVariable(ToolboxCatalog.RootEnvironmentVariable, null);
            var expected = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "..", "Resources", "ArcToolBox", "toolboxes"));
            Assert.Equal(expected, ToolboxCatalog.ResolveDefaultRoot());
        }
        finally
        {
            Environment.SetEnvironmentVariable(ToolboxCatalog.RootEnvironmentVariable, previous);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(directory.Replace(source, destination, StringComparison.Ordinal));
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, destination, StringComparison.Ordinal));
    }
}
