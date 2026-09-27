using System.Collections.Immutable;

namespace ArcGISProMCP.Evals;

/// <summary>The ranked result of one task and where the first acceptable id appeared (1-based).</summary>
public sealed record TaskOutcome(EvalTask Task, ImmutableArray<string> Ranked, int? FirstHitRank)
{
    public bool HitAt(int k) => FirstHitRank is { } rank && rank <= k;

    /// <summary>True when the task met its own cutoff <see cref="EvalTask.K"/>.</summary>
    public bool Passed => HitAt(Task.K);
}

/// <summary>Aggregate retrieval metrics for one suite.</summary>
/// <param name="RecallAt1">Share of tasks whose top result is an expected id.</param>
/// <param name="RecallAt5">Share of tasks with an expected id in the top five.</param>
/// <param name="Mrr">Mean reciprocal rank of the first expected id (0 when it is not retrieved).</param>
/// <param name="TaskPassRate">Share of tasks that met their own <c>k</c>.</param>
/// <param name="MeanFirstHitRank">Mean rank of the first expected id over tasks that retrieved one; null when none did.</param>
/// <param name="Misses">Tasks whose expected ids were not retrieved at all within the ranked depth.</param>
public sealed record EvalMetrics(
    int Tasks,
    double RecallAt1,
    double RecallAt5,
    double Mrr,
    double TaskPassRate,
    double? MeanFirstHitRank,
    int Misses);

/// <summary>One suite run: its outcomes, metrics and the environment the search ran against.</summary>
public sealed record EvalSuiteResult(string Suite, string Host, ImmutableArray<TaskOutcome> Outcomes, EvalMetrics Metrics)
{
    public ImmutableArray<TaskOutcome> Failures => Outcomes.Where(outcome => !outcome.Passed).ToImmutableArray();
}

/// <summary>Runs retrieval tasks through a search function and scores the rankings. Deterministic; no model calls.</summary>
public static class EvalRunner
{
    /// <summary>How many results each search is asked for; MRR and first-hit ranks are measured to this depth.</summary>
    public const int RankDepth = 20;

    public static EvalSuiteResult Run(string suite, string host, IEnumerable<EvalTask> tasks, Func<EvalTask, IEnumerable<string>> search)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(search);
        var outcomes = tasks.Select(task => Score(task, search(task).Take(RankDepth).ToImmutableArray())).ToImmutableArray();
        return new EvalSuiteResult(suite, host, outcomes, Measure(outcomes));
    }

    public static TaskOutcome Score(EvalTask task, ImmutableArray<string> ranked)
    {
        ArgumentNullException.ThrowIfNull(task);
        var expected = new HashSet<string>(task.Expected, StringComparer.OrdinalIgnoreCase);
        int? first = null;
        for (var i = 0; i < ranked.Length; i++)
        {
            if (!expected.Contains(ranked[i])) continue;
            first = i + 1;
            break;
        }

        return new TaskOutcome(task, ranked, first);
    }

    public static EvalMetrics Measure(IReadOnlyCollection<TaskOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        if (outcomes.Count == 0) return new EvalMetrics(0, 0, 0, 0, 0, null, 0);
        double count = outcomes.Count;
        var hits = outcomes.Where(outcome => outcome.FirstHitRank is not null).ToArray();
        return new EvalMetrics(
            outcomes.Count,
            Round(outcomes.Count(outcome => outcome.HitAt(1)) / count),
            Round(outcomes.Count(outcome => outcome.HitAt(5)) / count),
            Round(outcomes.Sum(outcome => outcome.FirstHitRank is { } rank ? 1d / rank : 0d) / count),
            Round(outcomes.Count(outcome => outcome.Passed) / count),
            hits.Length == 0 ? null : Round(hits.Average(outcome => (double)outcome.FirstHitRank!.Value)),
            outcomes.Count - hits.Length);
    }

    private static double Round(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);
}
