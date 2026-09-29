using System.Globalization;
using System.Text;
using ArcGISProMCP.Evals;

namespace ArcGISProMCP.Evals.Tests;

/// <summary>Shared tail of every eval test: report, optionally write the scorecard, then gate on the baseline.</summary>
internal static class EvalReporting
{
    private static readonly Lazy<EvalCommit> Commit = new(() => EvalEnvironment.ResolveCommit(EvalPaths.RepositoryRoot));

    // Eval classes run in parallel; the scorecard merge is a read-modify-write of one file.
    private static readonly Lock ScorecardGate = new();

    public static void ReportAndCheck(EvalSuiteResult result)
    {
        var metrics = result.Metrics;
        var report = new StringBuilder();
        report.Append(CultureInfo.InvariantCulture,
            $"{result.Suite} ({result.Host}): tasks={metrics.Tasks} recall@1={metrics.RecallAt1:0.000} recall@5={metrics.RecallAt5:0.000} mrr={metrics.Mrr:0.000} misses={metrics.Misses}");
        foreach (var failure in result.Failures)
        {
            report.Append(CultureInfo.InvariantCulture,
                $"\n  FAIL {failure.Task.Id} \"{failure.Task.Task}\" expected [{string.Join(", ", failure.Task.Expected)}] rank={failure.FirstHitRank?.ToString(CultureInfo.InvariantCulture) ?? "miss"} top=[{string.Join(", ", failure.Ranked.Take(5))}]");
        }

        TestContext.Current.SendDiagnosticMessage(report.ToString());
        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());

        if (EvalEnvironment.WriteResults)
        {
            lock (ScorecardGate)
                ScorecardWriter.Merge(Commit.Value.ResultsDirectory(EvalPaths.RepositoryRoot), Commit.Value, result);
        }

        var regression = EvalBaseline.Load(EvalPaths.Baseline).CheckRegression(result);
        Assert.True(regression is null, regression + "\n" + report);
    }
}
