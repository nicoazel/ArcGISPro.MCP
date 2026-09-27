using System.Collections.Immutable;
using ArcGISProMCP.Core.Geoprocessing;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Registry;

namespace ArcGISProMCP.Evals;

/// <summary>Adapters from the product search APIs to the ranked-id form the runner scores.</summary>
public static class EvalSearches
{
    /// <summary>E1: <see cref="OperationRegistry.Search"/> with the task's filters, as <c>registry_search</c> calls it.</summary>
    public static Func<EvalTask, IEnumerable<string>> Registry(IOperationRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return task =>
        {
            var filters = task.Filters;
            var query = new OperationQuery(
                task.Task,
                filters?.Domain,
                filters?.Capabilities is { IsDefaultOrEmpty: false } capabilities
                    ? capabilities.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase)
                    : null,
                filters?.MaxRisk is { } risk ? Enum.Parse<OperationRisk>(risk, ignoreCase: true) : null,
                EvalRunner.RankDepth);
            return registry.Search(query).Select(hit => hit.Descriptor.Id);
        };
    }

    /// <summary>E2: <see cref="ToolboxCatalog.Search"/>, as <c>gp.search</c> calls it.</summary>
    public static Func<EvalTask, IEnumerable<string>> Geoprocessing(ToolboxCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return task => catalog.Search(task.Task, EvalRunner.RankDepth).Select(hit => hit.Tool.ExecutionName);
    }
}
