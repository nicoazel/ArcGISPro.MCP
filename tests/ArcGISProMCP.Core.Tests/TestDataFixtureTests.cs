using System.Text;
using System.Text.Json;

namespace ArcGISProMCP.Core.Tests;

/// <summary>
/// The urban stress workflows (tools/run-urban-stress.ps1) and the live acceptance drivers
/// read tests/data, which tools/create-synthetic-test-data.py generates. A shapefile missing
/// a sidecar only fails once ArcGIS Pro opens it, and a stray file can carry a local path or
/// third-party content into the repository, so check the folder here.
/// </summary>
public sealed class TestDataFixtureTests
{
    private static readonly string[] RequiredShapefileParts = [".shp", ".shx", ".dbf", ".prj"];
    private static readonly string[] AllowedShapefileParts = [".shp", ".shx", ".dbf", ".prj", ".cpg"];

    // The five layers run-urban-stress.ps1 binds into the urban workflows.
    private static readonly string[] Shapefiles =
    [
        "Polygon_MixedMultiPart_Parcels",
        "Polyline_MultipartMix_Streets",
        "Polygons_Single_Buildings",
        "Point_Multi_Mixed",
        "Boundary_Multipart_Polygon",
    ];

    // Strings that must never ship in fixture files: local paths (including any drive-letter
    // path, ":\"), and names of the third-party data, vendors and places the synthetic set
    // replaced. Matched case-insensitively as ASCII and as UTF-16LE, which file geodatabases
    // use for text. Keep in sync with FORBIDDEN in tools/create-synthetic-test-data.py.
    private static readonly string[] ForbiddenStrings =
    [
        "_11_Git", "Users", "rhino", "scratch", "Dynamap", "T068437", "UrbanFootprint",
        "TomTom", "PICTOMETRY", "Pittsburgh", "Calthorpe", "Allegheny", "nicoazel", "azel",
        ":\\",
    ];

    // The generated data the byte scan covers. tests/data/README.md is documentation and
    // legitimately quotes the ArcGIS Pro Python path, so it is not scanned.
    private static readonly string[] GeneratedEntries = ["SHP", "MasterPlan.gdb", "expected-statistics.json"];

    private static string DataRoot() => Path.Combine(AcceptanceManifestTests.RepositoryRoot(), "tests", "data");

    [Fact]
    public void Every_shapefile_set_is_complete()
    {
        var shpRoot = Path.Combine(DataRoot(), "SHP");
        Assert.True(Directory.Exists(shpRoot), $"Missing fixture folder {shpRoot}.");

        var baseNames = Directory.EnumerateFiles(shpRoot)
            .Where(path => RequiredShapefileParts.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Select(Path.GetFileNameWithoutExtension)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Superset(
            new HashSet<string>(Shapefiles, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(baseNames!, StringComparer.OrdinalIgnoreCase));

        foreach (var baseName in baseNames)
            foreach (var extension in RequiredShapefileParts)
                Assert.True(File.Exists(Path.Combine(shpRoot, baseName + extension)),
                    $"Shapefile set '{baseName}' is missing {extension}.");
    }

    [Fact]
    public void Shapefile_folder_holds_only_generated_parts()
    {
        var shpRoot = Path.Combine(DataRoot(), "SHP");
        var unexpected = Directory.EnumerateFileSystemEntries(shpRoot)
            .Where(path => Directory.Exists(path)
                || !AllowedShapefileParts.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
                || !Shapefiles.Contains(Path.GetFileNameWithoutExtension(path), StringComparer.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();
        Assert.True(unexpected.Length == 0,
            $"tests/data/SHP holds files the generator does not write: {string.Join(", ", unexpected)}.");
    }

    [Fact]
    public void Proposal_geodatabase_is_present()
    {
        var gdb = Path.Combine(DataRoot(), "MasterPlan.gdb");
        Assert.True(File.Exists(Path.Combine(gdb, "gdb")), "MasterPlan.gdb is missing its 'gdb' marker file.");
        Assert.NotEmpty(Directory.EnumerateFiles(gdb, "*.gdbtable"));
    }

    [Fact]
    public void No_lock_files_are_committed()
    {
        var locks = Directory.EnumerateFiles(DataRoot(), "*.lock", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(DataRoot(), path))
            .ToArray();
        Assert.True(locks.Length == 0, $"Lock files under tests/data: {string.Join(", ", locks)}.");
    }

    [Fact]
    public void No_fixture_file_contains_a_forbidden_string()
    {
        var hits = new List<string>();
        foreach (var entry in GeneratedEntries)
        {
            var full = Path.Combine(DataRoot(), entry);
            Assert.True(Directory.Exists(full) || File.Exists(full), $"Missing generated fixture entry {entry}.");
            var files = Directory.Exists(full)
                ? Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
                : [full];
            foreach (var path in files)
                foreach (var text in FindForbiddenStrings(File.ReadAllBytes(path)))
                    hits.Add($"{Path.GetRelativePath(DataRoot(), path)}: '{text}'");
        }
        Assert.True(hits.Count == 0, "Fixture files contain forbidden strings:\n" + string.Join("\n", hits));
    }

    [Theory]
    [InlineData(@"C:\Data\parcels.shp", @":\")]
    [InlineData("Street data (c) TOMTOM", "TomTom")]
    [InlineData("Hazel Avenue", "azel")]
    [InlineData("ALLEGHENY county", "Allegheny")]
    [InlineData("pictometry imagery", "PICTOMETRY")]
    public void Forbidden_string_scan_matches_ascii_and_utf16_case_insensitively(string content, string expected)
    {
        Assert.Contains(expected, FindForbiddenStrings(Encoding.ASCII.GetBytes(content)));
        Assert.Contains(expected, FindForbiddenStrings(Encoding.Unicode.GetBytes(content)));
    }

    [Fact]
    public void Forbidden_string_scan_accepts_the_synthetic_street_names()
    {
        Assert.Empty(FindForbiddenStrings(Encoding.ASCII.GetBytes("Hawthorn Avenue, Ginkgo Avenue, 4th Street")));
        Assert.Empty(FindForbiddenStrings(Encoding.Unicode.GetBytes("Hawthorn Avenue, Ginkgo Avenue, 4th Street")));
    }

    /// <summary>The forbidden strings found in <paramref name="content"/>, ASCII-case-insensitively.</summary>
    private static string[] FindForbiddenStrings(byte[] content)
    {
        var bytes = (byte[])content.Clone();
        for (var i = 0; i < bytes.Length; i++)
            if (bytes[i] is >= (byte)'A' and <= (byte)'Z')
                bytes[i] = (byte)(bytes[i] + 32);
        return ForbiddenStrings
            .Where(text =>
            {
                var lower = text.ToLowerInvariant();
                return bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(lower)) >= 0
                    || bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(lower)) >= 0;
            })
            .ToArray();
    }

    [Fact]
    public void Expected_statistics_describe_the_fixture()
    {
        var path = Path.Combine(DataRoot(), "expected-statistics.json");
        Assert.True(File.Exists(path), "tests/data/expected-statistics.json is missing.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        var matched = root.GetProperty("matched").GetInt32();
        Assert.True(matched > 0, "expected-statistics.json must report matched > 0.");
        Assert.True(root.GetProperty("area_gross").GetDouble() > 0, "area_gross must be positive.");
        foreach (var field in new[] { "pop", "du", "emp" })
        {
            var sum = root.GetProperty(field).GetDouble();
            Assert.True(sum > 0 && sum == Math.Floor(sum), $"{field} must be a positive whole-number sum.");
        }

        var counts = root.GetProperty("counts");
        Assert.Equal(matched, counts.GetProperty("Polygon_MixedMultiPart_Parcels").GetInt32());
        foreach (var name in Shapefiles)
            Assert.True(counts.GetProperty(name).GetInt32() > 0, $"counts.{name} must be positive.");
        Assert.Equal(2, counts.GetProperty("Point_Multi_Mixed").GetInt32());
        Assert.Equal(1, counts.GetProperty("Boundary_Multipart_Polygon").GetInt32());
        Assert.Equal(9, counts.GetProperty("ProposedBuildings").GetInt32());
        Assert.Equal(9, counts.GetProperty("ProposedMassing").GetInt32());
        Assert.Equal(1, counts.GetProperty("DesignSites").GetInt32());

        // The urban workflows symbolise these land_use_1 classes.
        var landUse = root.GetProperty("land_use_1");
        foreach (var value in new[] { "multifamily", "retail_commercial", "office", "parks_recreation", "civic_facilities" })
            Assert.True(landUse.TryGetProperty(value, out var count) && count.GetInt32() > 0,
                $"land_use_1 has no '{value}' parcels.");
        Assert.Equal(matched, landUse.EnumerateObject().Sum(entry => entry.Value.GetInt32()));
    }
}
