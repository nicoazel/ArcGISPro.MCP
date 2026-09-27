using ArcGISProMCP.Evals;

namespace ArcGISProMCP.Evals.Tests;

/// <summary>
/// E1: natural-language tasks routed through the real <see cref="Core.Registry.OperationRegistry"/> search
/// over the add-in's real descriptors (interim: via the extracted descriptor fixture).
/// </summary>
[Trait("Category", "eval")]
public sealed class RegistrySearchEvalTests
{
    private const string Suite = "registry-search";

    [Fact]
    public void E1_tasks_expect_registered_operations()
    {
        var ids = DescriptorFixtures.Load(EvalPaths.DescriptorFixture).Operations.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var tasks = EvalTaskLoader.LoadJsonl(EvalPaths.Tasks("registry-search.jsonl"));

        Assert.Equal(30, tasks.Length);
        Assert.All(tasks, task => Assert.All(task.Expected, id => Assert.Contains(id, ids)));
        // Every capability group in docs/reference.md is exercised.
        Assert.Equal(5, tasks.Select(task => task.Group).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void E1_registry_search_holds_its_baseline()
    {
        var fixture = DescriptorFixtures.Load(EvalPaths.DescriptorFixture);
        var registry = DescriptorFixtures.CreateRegistry(fixture);
        var tasks = EvalTaskLoader.LoadJsonl(EvalPaths.Tasks("registry-search.jsonl"));

        var result = EvalRunner.Run(Suite, $"OperationRegistry over {fixture.Operations.Length} add-in descriptors (source fixture)",
            tasks, EvalSearches.Registry(registry));

        EvalReporting.ReportAndCheck(result);
    }
}
