using System.Text.Json;
using ArcGISProMCP.Evals;

namespace ArcGISProMCP.Evals.Tests;

/// <summary>
/// Keeps <c>evals/fixtures/operation-descriptors.json</c> identical to the add-in's descriptor source.
/// Regenerate with <c>EVALS_UPDATE_FIXTURE=1 dotnet test --filter FullyQualifiedName~DescriptorFixtureTests</c>.
/// Interim until the Phase 4 operations seam lets tests build the real catalog.
/// </summary>
public sealed class DescriptorFixtureTests
{
    public const string FixtureNote =
        "Interim: extracted from src/ArcGISProMCP.AddIn/Operations/*.cs by DescriptorSourceExtractor " +
        "(tests/ArcGISProMCP.Evals.Tests). Replace with the real ProOperationCatalog once the Phase 4 operations seam lands.";

    [Fact]
    public void Fixture_matches_the_addin_descriptor_sources()
    {
        var root = EvalPaths.RepositoryRoot;
        var extracted = new DescriptorFixture(FixtureNote, DescriptorSourceExtractor.Extract(
            Path.Combine(root, "src", "ArcGISProMCP.AddIn", "Operations"), root));
        var expected = JsonSerializer.Serialize(extracted, DescriptorFixtures.JsonOptions).ReplaceLineEndings("\n") + "\n";

        if (string.Equals(Environment.GetEnvironmentVariable("EVALS_UPDATE_FIXTURE"), "1", StringComparison.Ordinal))
            File.WriteAllText(EvalPaths.DescriptorFixture, expected);

        Assert.True(extracted.Operations.Length >= 30, $"Only {extracted.Operations.Length} descriptors extracted.");
        var committed = File.ReadAllText(EvalPaths.DescriptorFixture).ReplaceLineEndings("\n");
        Assert.True(expected == committed,
            "evals/fixtures/operation-descriptors.json is out of date with the add-in descriptors. " +
            "Regenerate it with EVALS_UPDATE_FIXTURE=1 and re-run the evals.");
    }

    [Fact]
    public void Fixture_builds_a_real_registry()
    {
        var fixture = DescriptorFixtures.Load(EvalPaths.DescriptorFixture);

        var registry = DescriptorFixtures.CreateRegistry(fixture);

        Assert.Equal(fixture.Operations.Length, registry.Descriptors.Count);
        Assert.All(fixture.Operations, entry => Assert.Equal(entry.Id.Split('.')[0], entry.Domain));
    }
}
