using System.Text.Json.Nodes;
using ArcGISProMCP.Evals;

namespace ArcGISProMCP.Evals.Tests;

/// <summary>Unit tests for the metric arithmetic, task loading and scorecard format (not evals themselves).</summary>
public sealed class EvalRunnerTests
{
    private static EvalTask Task(string id, params string[] expected) => new(id, "query " + id, [.. expected]);

    [Fact]
    public void Metrics_use_the_first_acceptable_id()
    {
        var outcomes = new[]
        {
            EvalRunner.Score(Task("a", "x"), ["x", "y"]),
            EvalRunner.Score(Task("b", "y", "z"), ["q", "r", "z", "y"]),
            EvalRunner.Score(Task("c", "x"), ["q", "r", "s", "t", "u", "x"]),
            EvalRunner.Score(Task("d", "x"), ["q"])
        };

        var metrics = EvalRunner.Measure(outcomes);

        Assert.Equal([1, 3, 6, null], outcomes.Select(outcome => outcome.FirstHitRank));
        Assert.Equal(4, metrics.Tasks);
        Assert.Equal(0.25, metrics.RecallAt1);
        Assert.Equal(0.5, metrics.RecallAt5);
        Assert.Equal(Math.Round((1 + 1d / 3 + 1d / 6) / 4, 4), metrics.Mrr);
        Assert.Equal(Math.Round(10d / 3, 4), metrics.MeanFirstHitRank);
        Assert.Equal(1, metrics.Misses);
    }

    [Fact]
    public void Task_k_decides_pass_or_fail()
    {
        var strict = EvalRunner.Score(new EvalTask("s", "q", ["x"], K: 1), ["y", "x"]);
        var loose = EvalRunner.Score(new EvalTask("l", "q", ["x"], K: 5), ["y", "x"]);

        Assert.False(strict.Passed);
        Assert.True(loose.Passed);
    }

    [Fact]
    public void Loader_reads_defaults_and_rejects_duplicates()
    {
        var directory = Directory.CreateTempSubdirectory("evals-loader-");
        try
        {
            var path = Path.Combine(directory.FullName, "tasks.jsonl");
            File.WriteAllText(path, "// comment\n{\"id\":\"a\",\"task\":\"t\",\"expected\":[\"x\"],\"filters\":{\"maxRisk\":\"ReadOnly\"}}\n\n");
            var task = Assert.Single(EvalTaskLoader.LoadJsonl(path));
            Assert.Equal(5, task.K);
            Assert.Equal("ReadOnly", task.Filters?.MaxRisk);

            File.AppendAllText(path, "{\"id\":\"a\",\"task\":\"t\",\"expected\":[\"x\"]}\n");
            Assert.Throws<InvalidDataException>(() => EvalTaskLoader.LoadJsonl(path));

            File.WriteAllText(path, "{\"id\":\"a\",\"task\":\"t\",\"expected\":[]}\n");
            Assert.Throws<InvalidDataException>(() => EvalTaskLoader.LoadJsonl(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Scorecard_merges_suites_and_lists_failures()
    {
        var directory = Directory.CreateTempSubdirectory("evals-scorecard-");
        try
        {
            var commit = new EvalCommit("abc1234", false, "2026-09-26");
            var first = EvalRunner.Run("b-suite", "host", [new EvalTask("t1", "q", ["x"], K: 1)], _ => ["y", "x"]);
            var second = EvalRunner.Run("a-suite", "host", [Task("t2", "x")], _ => ["x"]);
            var rerun = EvalRunner.Run("b-suite", "host", [Task("t1", "x"), Task("t3", "x")], _ => ["x"]);

            ScorecardWriter.Merge(directory.FullName, commit, first);
            ScorecardWriter.Merge(directory.FullName, commit, second);
            var afterFirst = JsonNode.Parse(File.ReadAllText(Path.Combine(directory.FullName, ScorecardWriter.JsonFileName)))!;
            ScorecardWriter.Merge(directory.FullName, commit, rerun);
            var json = JsonNode.Parse(File.ReadAllText(Path.Combine(directory.FullName, ScorecardWriter.JsonFileName)))!;
            var markdown = File.ReadAllText(Path.Combine(directory.FullName, ScorecardWriter.MarkdownFileName));

            Assert.Equal(0, afterFirst["suites"]![1]!["metrics"]!["recall@1"]!.GetValue<double>());
            Assert.Equal("t1", (string?)afterFirst["suites"]![1]!["failures"]![0]!["id"]);
            Assert.Equal("abc1234", (string?)json["sha"]);
            Assert.Equal(["a-suite", "b-suite"], json["suites"]!.AsArray().Select(suite => (string?)suite!["suite"]));
            Assert.Equal(2, json["suites"]![1]!["tasks"]!.GetValue<int>());
            Assert.Empty(json["suites"]![1]!["failures"]!.AsArray());
            Assert.Contains("| a-suite | 1 | 1.000 | 1.000 | 1.000 |", markdown, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Baseline_allows_no_additional_failing_task()
    {
        var baseline = new EvalBaseline("n", new Dictionary<string, BaselineMetrics> { ["s"] = new(0.5, 0.8, 0.6, 10) });
        EvalSuiteResult WithRecallAt5(double recall) =>
            new("s", "h", [], new EvalMetrics(10, 0.5, recall, 0.6, recall, 1, 0));
        EvalSuiteResult WithTasks(int tasks) =>
            new("s", "h", [], new EvalMetrics(tasks, 0.5, 0.8, 0.6, 0.8, 1, 0));

        Assert.Null(baseline.CheckRegression(WithRecallAt5(0.8)));
        Assert.Null(baseline.CheckRegression(WithRecallAt5(0.9)));
        // One more task out of the top 5 (8 of 10 -> 7 of 10) is a regression.
        Assert.NotNull(baseline.CheckRegression(WithRecallAt5(0.7)));
        Assert.NotNull(baseline.CheckRegression(WithTasks(11)));
        Assert.NotNull(baseline.CheckRegression(new EvalSuiteResult("unknown", "h", [], new EvalMetrics(0, 0, 0, 0, 0, null, 0))));
    }

    [Fact]
    public void Baseline_gates_on_one_extra_failing_task_in_a_large_suite()
    {
        // 30 tasks: 26 -> 25 hits is a drop of 0.033, inside the old 0.05 tolerance but still one more failure.
        var baseline = new EvalBaseline("n", new Dictionary<string, BaselineMetrics> { ["s"] = new(0.6, 26 / 30.0, 0.7, 30) });

        var result = baseline.CheckRegression(new EvalSuiteResult("s", "h", [], new EvalMetrics(30, 0.6, 25 / 30.0, 0.7, 25 / 30.0, 1, 0)));

        Assert.NotNull(result);
        Assert.Contains("recall@5", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Baseline_gates_on_mrr()
    {
        var baseline = new EvalBaseline("n", new Dictionary<string, BaselineMetrics> { ["s"] = new(0.5, 0.8, 0.6, 10) });
        EvalSuiteResult WithMrr(double mrr) => new("s", "h", [], new EvalMetrics(10, 0.5, 0.8, mrr, 0.8, 1, 0));

        Assert.Null(baseline.CheckRegression(WithMrr(0.56)));
        var result = baseline.CheckRegression(WithMrr(0.54));
        Assert.NotNull(result);
        Assert.Contains("MRR", result, StringComparison.Ordinal);
        Assert.DoesNotContain("recall@5", result, StringComparison.Ordinal);
    }
}
