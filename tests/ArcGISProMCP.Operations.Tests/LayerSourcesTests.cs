namespace ArcGISProMCP.Operations.Tests;

/// <summary>
/// The pure source-comparison and reuse rules behind <c>layer.add</c> with a <c>name</c>. The add-in
/// feeds them what ArcGIS reports; a healthy layer must never be removed because ArcGIS spelled its
/// source differently.
/// </summary>
public sealed class LayerSourcesTests
{
    private static readonly Uri Requested = new(@"D:\data\Parcels.shp");

    [Theory]
    [InlineData(@"D:\data\Parcels.shp", @"D:\data\Parcels.shp")]
    [InlineData(@"d:\DATA\parcels.SHP", @"D:\data\Parcels.shp")]
    [InlineData(@"D:\data\Parcels", @"D:\data\Parcels.shp")]
    [InlineData(@"D:\data\Parcels.shp", @"D:\data\Parcels")]
    [InlineData(@"D:\data\City.gdb\Roads\", @"D:\data\City.gdb\Roads")]
    [InlineData("file:///D:/data/City.gdb/Roads", @"D:\data\City.gdb\Roads")]
    [InlineData(@"\\server\share\City.gdb\Roads", @"\\server\share\City.gdb\Roads")]
    public void Same_source_ignores_case_trailing_separators_shp_and_slash_direction(string actual, string requested) =>
        Assert.True(LayerSources.SameSource(new Uri(actual), new Uri(requested)));

    [Theory]
    [InlineData(@"D:\data\Other.shp", @"D:\data\Parcels.shp")]
    [InlineData(@"D:\data\City.gdb\Transport\Roads", @"D:\data\City.gdb\Roads")]
    // A UNC path and a mapped drive may be the same share; that is not resolved by path comparison.
    [InlineData(@"\\server\share\City.gdb\Roads", @"S:\City.gdb\Roads")]
    [InlineData("https://services.example.com/arcgis/rest/services/Roads/FeatureServer/0", @"D:\data\Roads.shp")]
    public void Different_sources_do_not_match(string actual, string requested) =>
        Assert.False(LayerSources.SameSource(new Uri(actual), new Uri(requested)));

    [Fact]
    public void Null_or_relative_actual_paths_never_match()
    {
        Assert.False(LayerSources.SameSource(null, Requested));
        Assert.False(LayerSources.SameSource(new Uri(@"data\Parcels.shp", UriKind.Relative), Requested));
    }

    [Fact]
    public void Service_urls_compare_ignoring_case_and_a_trailing_slash()
    {
        var requested = new Uri("https://services.example.com/arcgis/rest/services/Roads/FeatureServer/0");
        Assert.True(LayerSources.SameSource(new Uri("https://SERVICES.example.com/arcgis/rest/services/Roads/FeatureServer/0/"), requested));
        Assert.False(LayerSources.SameSource(new Uri("https://services.example.com/arcgis/rest/services/Roads/FeatureServer/1"), requested));
    }

    [Theory]
    [InlineData(@"D:\data\Parcels.shp", @"D:\data\Parcels")]
    [InlineData(@"D:/data/Parcels.SHP", @"D:\data\Parcels")]
    [InlineData(@"D:\data\City.gdb\", @"D:\data\City.gdb")]
    [InlineData(@"\\server\share\x.gdb\Roads", @"\\server\share\x.gdb\Roads")]
    public void Normalize_file_produces_a_comparable_full_path(string path, string expected) =>
        Assert.Equal(expected, LayerSources.NormalizeFile(path), ignoreCase: true);

    [Fact]
    public void Normalize_file_makes_relative_paths_full()
    {
        var normalized = LayerSources.NormalizeFile(@"data\Parcels.shp");
        Assert.True(Path.IsPathFullyQualified(normalized));
        Assert.EndsWith(@"\data\Parcels", normalized, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"D:\data\City.gdb", "Roads", @"D:\data\City.gdb\", "roads", true)]
    [InlineData(@"D:\data", "Parcels.shp", @"d:\DATA", "Parcels", true)]
    [InlineData(@"D:\data\City.gdb", "Roads", @"D:\data\Other.gdb", "Roads", false)]
    [InlineData(@"D:\data\City.gdb", "Roads", @"D:\data\City.gdb", "Rails", false)]
    [InlineData(null, "Roads", @"D:\data\City.gdb", "Roads", false)]
    [InlineData(@"D:\data\City.gdb", "", @"D:\data\City.gdb", "Roads", false)]
    public void Same_dataset_compares_workspace_and_name(string? actualWorkspace, string? actualName, string requestedWorkspace, string requestedName, bool expected) =>
        Assert.Equal(expected, LayerSources.SameDataset(actualWorkspace, actualName, requestedWorkspace, requestedName));

    [Theory]
    [InlineData(@"D:\data\Parcels.shp", @"D:\data", "Parcels", true)]
    [InlineData(@"D:\data\City.gdb\Roads", @"D:\data\City.gdb", "Roads", false)]
    [InlineData(@"D:\data\City.gdb\Transport\Roads", @"D:\data\City.gdb", "Roads", false)]
    [InlineData(@"D:/data/City.gdb/Roads/", @"D:\data\City.gdb", "Roads", false)]
    // The last ".gdb" segment is the geodatabase, not a parent folder named like one.
    [InlineData(@"D:\archive.gdb\projects\City.gdb\Roads", @"D:\archive.gdb\projects\City.gdb", "Roads", false)]
    public void Split_local_dataset_finds_the_workspace_and_dataset(string path, string workspace, string name, bool shapefile)
    {
        Assert.True(LayerSources.TrySplitLocalDataset(path, out var actualWorkspace, out var actualName, out var isShapefile));
        Assert.Equal(workspace, actualWorkspace);
        Assert.Equal(name, actualName);
        Assert.Equal(shapefile, isShapefile);
    }

    [Theory]
    [InlineData(@"D:\data\roads.lyrx")]
    [InlineData(@"D:\data\City.gdb")]
    [InlineData(@"D:\data\City.gdb\a\b\Roads")]
    [InlineData(@"D:\data\.gdb\Roads")]
    [InlineData(@"\.shp")]
    [InlineData("")]
    public void Split_local_dataset_rejects_other_paths(string path) =>
        Assert.False(LayerSources.TrySplitLocalDataset(path, out _, out _, out _));

    [Fact]
    public void A_broken_layer_may_be_repaired_or_recreated()
    {
        Assert.Equal(ExistingLayerAction.RepairOrRecreate,
            LayerSources.DecideExisting(broken: true, requestIsLayerFile: false, Requested, Requested, () => true));
        Assert.Equal(ExistingLayerAction.RepairOrRecreate,
            LayerSources.DecideExisting(broken: true, requestIsLayerFile: true, null, new Uri(@"D:\data\roads.lyrx"), () => false));
    }

    [Fact]
    public void A_healthy_layer_with_no_reported_path_is_reused_unverified()
    {
        var service = new Uri("https://services.example.com/arcgis/rest/services/Roads/FeatureServer/0");
        Assert.Equal(ExistingLayerAction.ReuseUnverified,
            LayerSources.DecideExisting(broken: false, requestIsLayerFile: false, null, service, () => throw new InvalidOperationException("not needed")));
    }

    [Fact]
    public void A_healthy_layer_is_reused_when_the_path_or_the_dataset_matches()
    {
        Assert.Equal(ExistingLayerAction.Reuse,
            LayerSources.DecideExisting(false, false, new Uri(@"d:\data\parcels"), Requested, () => throw new InvalidOperationException("not needed")));
        Assert.Equal(ExistingLayerAction.Reuse,
            LayerSources.DecideExisting(false, false, new Uri(@"D:\data\City.gdb\Transport\Roads"), new Uri(@"D:\data\City.gdb\Roads"), () => true));
        Assert.Equal(ExistingLayerAction.Reuse,
            LayerSources.DecideExisting(false, true, new Uri(@"D:\data\Other.shp"), new Uri(@"D:\data\roads.lyrx"), () => false));
    }

    [Fact]
    public void A_healthy_layer_reading_other_data_is_only_ever_repaired_in_place()
    {
        var action = LayerSources.DecideExisting(false, false, new Uri(@"D:\data\Other.shp"), Requested, () => false);
        Assert.Equal(ExistingLayerAction.RepairInPlaceOnly, action);
        Assert.NotEqual(ExistingLayerAction.RepairOrRecreate, action);
    }

    [Theory]
    [InlineData(true, false, false, @"D:\data\Parcels.shp", "broken")]
    [InlineData(false, false, false, null, "unverified")]
    [InlineData(false, false, true, null, "ok")]
    [InlineData(false, true, false, @"D:\data\Other.shp", "ok")]
    [InlineData(false, false, false, @"D:\data\Parcels", "ok")]
    [InlineData(false, false, false, @"D:\data\Other.shp", "mismatch")]
    public void Data_source_status_reports_what_the_layer_actually_reads(bool broken, bool layerFile, bool created, string? actual, string expected) =>
        Assert.Equal(expected, LayerSources.DataSourceStatus(
            broken, layerFile, created, actual is null ? null : new Uri(actual), Requested, () => false));

    [Fact]
    public void Data_source_status_accepts_a_matching_dataset_spelled_differently() =>
        Assert.Equal("ok", LayerSources.DataSourceStatus(
            false, false, false, new Uri(@"D:\data\City.gdb\Transport\Roads"), new Uri(@"D:\data\City.gdb\Roads"), () => true));

    /// <summary>A fake file system: the listed files and directories exist, nothing else.</summary>
    private static (Func<string, bool> FileExists, Func<string, bool> DirectoryExists) FileSystem(string[] files, string[] directories) =>
        (path => files.Contains(path, StringComparer.OrdinalIgnoreCase), path => directories.Contains(path, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void A_shapefile_whose_files_were_renamed_away_is_missing()
    {
        // The live operation matrix renamed Broken_Boundary.* to Moved_Broken_Boundary.*; ArcGIS reports the path without ".shp".
        var (file, directory) = FileSystem([@"D:\ops\broken\Moved_Broken_Boundary.shp"], [@"D:\ops\broken", @"D:\ops", @"D:\"]);

        Assert.True(LayerSources.LocalSourceMissing(new Uri(@"D:\ops\broken\Broken_Boundary"), file, directory));
        Assert.True(LayerSources.LocalSourceMissing(new Uri(@"D:\ops\broken\Broken_Boundary.shp"), file, directory));
    }

    [Theory]
    [InlineData(@"D:\ops\Parcels")]
    [InlineData(@"D:\ops\Parcels.shp")]
    [InlineData(@"D:\ops\City.gdb")]
    public void An_existing_dataset_is_not_missing(string path)
    {
        var (file, directory) = FileSystem([@"D:\ops\Parcels.shp"], [@"D:\ops", @"D:\ops\City.gdb", @"D:\"]);

        Assert.False(LayerSources.LocalSourceMissing(new Uri(path), file, directory));
    }

    [Theory]
    // A feature class (or one in a feature dataset) inside an existing geodatabase is not a file-system entry.
    [InlineData(@"D:\ops\City.gdb\Roads")]
    [InlineData(@"D:\ops\City.gdb\Transport\Roads")]
    // Datasets inside container files.
    [InlineData(@"D:\ops\Sites.gpkg\main.Sites")]
    public void Datasets_inside_an_existing_container_cannot_be_judged(string path)
    {
        var (file, directory) = FileSystem([@"D:\ops\Sites.gpkg"], [@"D:\ops", @"D:\ops\City.gdb", @"D:\"]);

        Assert.Null(LayerSources.LocalSourceMissing(new Uri(path), file, directory));
    }

    [Fact]
    public void A_dataset_in_a_deleted_geodatabase_is_missing()
    {
        var (file, directory) = FileSystem([], [@"D:\ops", @"D:\"]);

        Assert.True(LayerSources.LocalSourceMissing(new Uri(@"D:\ops\Gone.gdb\Transport\Roads"), file, directory));
    }

    [Fact]
    public void Services_and_unknown_paths_cannot_be_judged()
    {
        var (file, directory) = FileSystem([], []);

        Assert.Null(LayerSources.LocalSourceMissing(null, file, directory));
        Assert.Null(LayerSources.LocalSourceMissing(new Uri("https://services.example.com/arcgis/rest/services/Roads/FeatureServer/0"), file, directory));
        Assert.Null(LayerSources.LocalSourceMissing(new Uri(@"data\Parcels.shp", UriKind.Relative), file, directory));
    }
}
