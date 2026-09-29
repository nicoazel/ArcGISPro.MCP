using ArcGISProMCP.Evals;

namespace ArcGISProMCP.Evals.Tests;

/// <summary>
/// E1: natural-language tasks routed through the real <see cref="Core.Registry.OperationRegistry"/> search
/// over the add-in's real descriptors, loaded from the descriptor dump.
/// </summary>
[Trait("Category", "eval")]
public sealed class RegistrySearchEvalTests
{
    private const string Suite = "registry-search";

    [Fact]
    public void E1_tasks_expect_registered_operations()
    {
        var ids = OperationDescriptorDump.Load(EvalPaths.DescriptorDump).Select(descriptor => descriptor.Id).ToHashSet(StringComparer.Ordinal);
        var tasks = EvalTaskLoader.LoadJsonl(EvalPaths.Tasks("registry-search.jsonl"));

        Assert.Equal(30, tasks.Length);
        Assert.All(tasks, task => Assert.All(task.Expected, id => Assert.Contains(id, ids)));
        // Every capability group in docs/reference.md is exercised.
        Assert.Equal(5, tasks.Select(task => task.Group).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void E1_registry_search_holds_its_baseline()
    {
        var descriptors = OperationDescriptorDump.Load(EvalPaths.DescriptorDump);
        var registry = OperationDescriptorDump.CreateRegistry(descriptors);
        var tasks = EvalTaskLoader.LoadJsonl(EvalPaths.Tasks("registry-search.jsonl"));

        var result = EvalRunner.Run(Suite, $"OperationRegistry over {descriptors.Length} add-in descriptors (descriptor dump)",
            tasks, EvalSearches.Registry(registry));

        EvalReporting.ReportAndCheck(result);
    }

    [Fact]
    public void E1_holdout_tasks_expect_registered_operations()
    {
        var ids = OperationDescriptorDump.Load(EvalPaths.DescriptorDump).Select(descriptor => descriptor.Id).ToHashSet(StringComparer.Ordinal);
        var tasks = EvalTaskLoader.LoadJsonl(EvalPaths.Tasks("registry-search-holdout.jsonl"));

        Assert.Equal(16, tasks.Length);
        Assert.All(tasks, task => Assert.All(task.Expected, id => Assert.Contains(id, ids)));
    }

    /// <summary>Held-out tasks written before search tuning; never used to choose synonyms or weights.</summary>
    [Fact]
    public void E1_registry_search_holdout_holds_its_baseline()
    {
        var descriptors = OperationDescriptorDump.Load(EvalPaths.DescriptorDump);
        var registry = OperationDescriptorDump.CreateRegistry(descriptors);
        var tasks = EvalTaskLoader.LoadJsonl(EvalPaths.Tasks("registry-search-holdout.jsonl"));

        var result = EvalRunner.Run(Suite + "-holdout", $"OperationRegistry over {descriptors.Length} add-in descriptors (descriptor dump)",
            tasks, EvalSearches.Registry(registry));

        EvalReporting.ReportAndCheck(result);
    }
}
