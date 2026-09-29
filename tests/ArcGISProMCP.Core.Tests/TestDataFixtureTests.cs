namespace ArcGISProMCP.Core.Tests;

/// <summary>
/// The urban stress workflows (tools/run-urban-stress.ps1) read tests/data. A shapefile
/// missing a sidecar only fails once ArcGIS Pro opens it, so check completeness here.
/// </summary>
public sealed class TestDataFixtureTests
{
    private static readonly string[] RequiredShapefileParts = [".shp", ".shx", ".dbf", ".prj"];

    [Fact]
    public void Every_shapefile_set_is_complete()
    {
        var shpRoot = Path.Combine(AcceptanceManifestTests.RepositoryRoot(), "tests", "data", "SHP");
        Assert.True(Directory.Exists(shpRoot), $"Missing fixture folder {shpRoot}.");

        var baseNames = Directory.EnumerateFiles(shpRoot)
            .Where(path => RequiredShapefileParts.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Select(Path.GetFileNameWithoutExtension)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // The five layers run-urban-stress.ps1 binds into the urban workflows.
        Assert.Superset(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Polygon_MixedMultiPart_Parcels",
                "Polyline_MultipartMix_Streets",
                "Polygons_Single_Buildings",
                "Point_Multi_Mixed",
                "Boundary_Multipart_Polygon",
            },
            new HashSet<string>(baseNames!, StringComparer.OrdinalIgnoreCase));

        foreach (var baseName in baseNames)
            foreach (var extension in RequiredShapefileParts)
                Assert.True(File.Exists(Path.Combine(shpRoot, baseName + extension)),
                    $"Shapefile set '{baseName}' is missing {extension}.");
    }

    [Fact]
    public void Proposal_geodatabase_is_present()
    {
        var gdb = Path.Combine(AcceptanceManifestTests.RepositoryRoot(), "tests", "data", "MasterPlan.gdb");
        Assert.True(File.Exists(Path.Combine(gdb, "gdb")), "MasterPlan.gdb is missing its 'gdb' marker file.");
        Assert.NotEmpty(Directory.EnumerateFiles(gdb, "*.gdbtable"));
    }
}
