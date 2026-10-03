using System.Text.Json;
using ArcGISProMCP.Core.Execution;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Operations.Services;
using ArcGISProMCP.Operations.Tests.Fakes;

namespace ArcGISProMCP.Operations.Tests;

/// <summary>
/// Every member name an operation writes into <c>data</c> is camelCase, at any depth, on success,
/// on failure and in dry runs. 0.3.0 leaked PascalCase from anonymous-object shorthand
/// (<c>new { result.IsFailed }</c>, <c>new { layer.Name }</c>) and from records serialized with
/// default options, so gp.run returned <c>IsFailed</c> beside gp.query's <c>isFailed</c>.
/// </summary>
/// <remarks>
/// Some objects in <c>data</c> are keyed by values taken from the user's data rather than by our
/// member names; those keys keep their original spelling and are listed in
/// <see cref="ValueKeyedObjects"/>. Nothing else may start with an upper-case letter.
/// </remarks>
public sealed class ResultCasingTests
{
    private const string BufferParameters = """["roads", "memory\\zones", "10 Meters", "left", "DISSOLVE", "a;b", "12", "0.5", "1;6", "#"]""";

    /// <summary>
    /// Paths (operation id, JSON path with <c>[]</c> for any array element) of objects whose keys
    /// are data values, not member names:
    /// feature.query rows are keyed by the layer's attribute field names (ZONE, OBJECTID, ...),
    /// exactly as ArcGIS spells them, so a client can match them against feature.layer.describe.
    /// </summary>
    private static readonly HashSet<(string Operation, string Path)> ValueKeyedObjects =
    [
        ("feature.query", "$.rows[]"),
    ];

    /// <summary>A case per operation (and per distinct result shape) the fake catalog can run.</summary>
    public static TheoryData<string> Cases => new(Scenarios.Keys.Order(StringComparer.Ordinal));

    private static readonly Dictionary<string, Func<Harness, Task<OperationResult>>> Scenarios = new(StringComparer.Ordinal)
    {
        ["project.get"] = h => h.Pro.RunAsync("project.get"),
        ["project.open"] = h => h.Pro.RunAsync("project.open", JsonSerializer.Serialize(new { path = h.Aprx() })),
        ["project.save"] = h => h.Pro.RunAsync("project.save"),
        ["map.list"] = h => h.Pro.RunAsync("map.list"),
        ["map.ensure (existing)"] = h => h.Pro.RunAsync("map.ensure", """{"name": "City"}"""),
        ["map.ensure (created)"] = h => h.Pro.RunAsync("map.ensure", """{"name": "Massing", "type": "scene"}"""),
        ["map.activate"] = h => h.Pro.RunAsync("map.activate", """{"map": "City"}"""),
        ["map.clear-selection"] = h => h.Pro.RunAsync("map.clear-selection", """{"map": "City"}"""),
        ["layer.list"] = h => h.Pro.RunAsync("layer.list", """{"map": "City"}"""),
        ["feature.layer.describe"] = h => h.Pro.RunAsync("feature.layer.describe", """{"layer": "Parcels"}"""),
        ["feature.query"] = h => h.Pro.RunAsync("feature.query", """{"layer": "Parcels"}"""),
        ["feature.select"] = h => h.Pro.RunAsync("feature.select", """{"layer": "Parcels", "where": "ZONE = 'R1'"}"""),
        ["feature.create"] = h => h.Pro.RunAsync("feature.create",
            """{"layer": "Parcels", "geometry": {"type": "point", "x": 5, "y": 6}, "attributes": {"ZONE": "R2"}}"""),
        ["feature.update"] = h => h.Pro.RunAsync("feature.update", """{"layer": "Parcels", "target": {"objectId": 1}, "attributes": {"ZONE": "C1"}}"""),
        ["feature.delete"] = h => h.Pro.RunAsync("feature.delete", """{"layer": "Parcels", "target": {"objectId": 2}}"""),
        ["gp.search"] = h => h.Pro.RunAsync("gp.search", """{"query": "buffer", "limit": 5}"""),
        ["gp.describe"] = h => h.Pro.RunAsync("gp.describe", """{"tool": "fixture.BufferZones"}"""),
        ["gp.describe (not found)"] = h => h.Pro.RunAsync("gp.describe", """{"tool": "fixture.BufferZone"}"""),
        ["gp.query"] = h =>
        {
            h.Pro.Geoprocessing.NextResult = FakeGeoprocessingService.Succeeded(new GeoprocessingMessage("Informative", "Start Time: now", 0))
                with { ReturnValue = "42", Values = ["42"], ValueTypes = ["GPLong"] };
            return h.Pro.RunAsync("gp.query", """{"tool": "management.GetCount", "parameters": ["parcels"]}""");
        },
        ["gp.query (invalid parameters)"] = h => h.Pro.RunAsync("gp.query", """{"tool": "management.GetCount", "parameters": []}"""),
        ["gp.run"] = h =>
        {
            h.Pro.Geoprocessing.NextResult = FakeGeoprocessingService.Succeeded(
                new GeoprocessingMessage("Informative", "Start Time: now", 0),
                new GeoprocessingMessage("Warning", "Output has no features.", 000117));
            return h.Pro.RunAsync("gp.run", $$$"""{"tool": "fixture.BufferZones", "parameters": {{{BufferParameters}}}, "environments": {"workspace": "memory"}}""");
        },
        ["gp.run (failed)"] = h =>
        {
            h.Pro.Geoprocessing.NextResult = FakeGeoprocessingService.Failed("ERROR 000732: Input Features: Dataset roads does not exist.");
            return h.Pro.RunAsync("gp.run", """{"tool": "fixture.EraseRows", "parameters": ["parcels"]}""");
        },
        ["gp.run (dry run)"] = h => ((IDryRunnableOperation)h.Pro.Operation("gp.run")).DryRunAsync(
            FakePro.Arguments($$"""{"tool": "fixture.BufferZones", "parameters": {{BufferParameters}}}"""), h.Pro.Context, TestContext.Current.CancellationToken),
        ["map.list (generic dry run)"] = async h =>
        {
            var revision = (await h.Pro.Workspace.GetSnapshotAsync(TestContext.Current.CancellationToken)).Revision;
            return await h.Pro.Executor.ExecuteAsync(
                new OperationRequest("map.list", FakePro.Arguments("{}"), revision, DryRun: true), TestContext.Current.CancellationToken);
        },
        ["view.capture"] = h => h.Pro.RunAsync("view.capture", """{"width": 640, "height": 480}"""),
        ["arcpy.inspect-script"] = h =>
        {
            h.ArcPy.WriteScript("count.py", "import arcpy\n");
            return h.Pro.RunAsync("arcpy.inspect-script", """{"scriptPath": "count.py"}""");
        },
        ["arcpy.run-script"] = h =>
        {
            // The fixture's python.exe cannot start, which still returns the full evidence object.
            var hash = h.ArcPy.WriteScript("run.py", "print('safe')\n");
            return h.Pro.RunAsync("arcpy.run-script", JsonSerializer.Serialize(new { scriptPath = "run.py", scriptSha256 = hash, timeoutSeconds = 30 }));
        },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Result_data_uses_camelCase_member_names_at_every_depth(string scenario)
    {
        using var harness = new Harness();
        var result = await Scenarios[scenario](harness);

        Assert.True(result.Data.HasValue, $"{scenario} returned no data ({result.ErrorCode}: {result.Message}).");
        var operation = scenario.Split(' ')[0];
        var offenders = new List<string>();
        Collect(operation, result.Data.Value, "$", offenders);
        Assert.True(offenders.Count == 0, $"{scenario} wrote PascalCase member names: {string.Join(", ", offenders.Distinct())}");
    }

    /// <summary>
    /// The add-in operations need ArcGIS Pro and cannot run here, so they are held to the same
    /// serializer by source: every operation (portable or add-in) builds <c>data</c> with
    /// <c>ProOperationBase.Json</c> or <c>GeoprocessingJson.Serialize</c>, never JsonSerializer directly.
    /// </summary>
    [Fact]
    public void Operations_serialize_data_only_through_the_shared_camelCase_options()
    {
        var root = RepositoryRoot();
        var sources = Directory.GetFiles(Path.Combine(root, "src", "ArcGISProMCP.Operations"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(root, "src", "ArcGISProMCP.AddIn", "Operations"), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(sources);

        var direct = sources
            .SelectMany(path => File.ReadAllLines(path).Select((line, index) => (path, line, index)))
            .Where(item => item.line.Contains("JsonSerializer.Serialize", StringComparison.Ordinal))
            .Select(item => $"{Path.GetRelativePath(root, item.path)}:{item.index + 1}")
            .ToArray();

        // The one allowed call is GeoprocessingJson.Serialize itself, which ProOperationBase.Json delegates to.
        var allowed = Assert.Single(direct);
        Assert.StartsWith(Path.Combine("src", "ArcGISProMCP.Operations", "GeoprocessingCatalogOperations.cs"), allowed, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ArcGISPro.MCP.slnx"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root from the test output tree.");
    }

    [Fact]
    public void Every_operation_in_the_fake_catalog_has_a_casing_case()
    {
        using var harness = new Harness();
        var covered = Scenarios.Keys.Select(key => key.Split(' ')[0]).ToHashSet(StringComparer.Ordinal);

        Assert.All(harness.Pro.Registry.Descriptors, descriptor =>
            Assert.True(covered.Contains(descriptor.Id), $"{descriptor.Id} has no case in {nameof(ResultCasingTests)}."));
    }

    [Fact]
    public void The_walker_finds_nested_PascalCase_and_honours_the_allow_list()
    {
        var offenders = new List<string>();
        var data = FakePro.Arguments("""{"rows": [{"ZONE": "R1", "nested": {"Inner": 1}}], "items": [{"Name": "x"}]}""");

        Collect("feature.query", data, "$", offenders);

        Assert.Equal(["$.rows[].*.Inner", "$.items[].Name"], offenders);
    }

    private static void Collect(string operation, JsonElement element, string path, List<string> offenders)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var valueKeyed = ValueKeyedObjects.Contains((operation, path));
                foreach (var property in element.EnumerateObject())
                {
                    var child = $"{path}.{property.Name}";
                    if (!valueKeyed && property.Name.Length > 0 && char.IsUpper(property.Name[0]))
                        offenders.Add(child);
                    // A value-keyed object's own keys are data, but objects nested inside it are ours again.
                    Collect(operation, property.Value, valueKeyed ? $"{path}.*" : child, offenders);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Collect(operation, item, $"{path}[]", offenders);
                break;
        }
    }

    /// <summary>A fake Pro with an open map holding the Parcels feature layer, plus ArcPy settings.</summary>
    internal sealed class Harness : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("ArcGISProMCP-Casing-").FullName;

        public Harness()
        {
            ArcPy = new ArcPyRuntimeFixture();
            Pro = new FakePro(arcPy: ArcPy.Settings);
            var parcels = FakeLayer.Feature("Parcels", new LayerElevation("on-ground", 0, 1));
            parcels.Table!.AddRow(1, 1, "R1");
            parcels.Table.AddRow(2, 2, "C2");
            var map = Pro.State.AddMap("City", "Map", parcels, FakeLayer.Other("Imagery", "RasterLayer"));
            map.HasOpenView = true;
            Pro.State.ActiveMapName = "City";
        }

        public ArcPyRuntimeFixture ArcPy { get; }

        public FakePro Pro { get; }

        public string Aprx()
        {
            var path = Path.Combine(_directory, "Other.aprx");
            File.WriteAllText(path, string.Empty);
            return path;
        }

        public void Dispose()
        {
            Pro.Dispose();
            ArcPy.Dispose();
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
