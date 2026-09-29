using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArcGISProMCP.Evals;

/// <summary>
/// Writes <c>scorecard.json</c> and <c>scorecard.md</c> into a results directory. Each suite run is
/// merged into the existing scorecard (replacing an earlier entry with the same suite name), so the
/// suites of one test run can be written independently.
/// </summary>
public static class ScorecardWriter
{
    public const string JsonFileName = "scorecard.json";
    public const string MarkdownFileName = "scorecard.md";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// Merges <paramref name="result"/> into <paramref name="directory"/>/scorecard.json and re-renders the markdown.
    /// </summary>
    /// <param name="model">The model that chose the calls; null for deterministic suites.</param>
    public static void Merge(string directory, EvalCommit commit, EvalSuiteResult result, string? model = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(result);
        Directory.CreateDirectory(directory);
        var jsonPath = Path.Combine(directory, JsonFileName);

        var existingSuites = File.Exists(jsonPath) && JsonNode.Parse(File.ReadAllText(jsonPath)) is JsonObject existing &&
                             existing["suites"] is JsonArray suites
            ? suites.OfType<JsonObject>()
                .Where(suite => (string?)suite["suite"] != result.Suite)
                .Select(suite => suite.DeepClone())
                .ToList()
            : [];
        existingSuites.Add(ToJson(result));

        var scorecard = new JsonObject
        {
            ["schema"] = 1,
            ["sha"] = commit.Sha,
            ["dirty"] = commit.Dirty,
            ["date"] = commit.Date,
            ["model"] = model,
            ["suites"] = new JsonArray(existingSuites
                .OrderBy(suite => (string?)suite!["suite"], StringComparer.Ordinal)
                .ToArray())
        };

        File.WriteAllText(jsonPath, scorecard.ToJsonString(WriteOptions) + "\n");
        File.WriteAllText(Path.Combine(directory, MarkdownFileName), RenderMarkdown(scorecard));
    }

    public static JsonObject ToJson(EvalSuiteResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var metrics = result.Metrics;
        return new JsonObject
        {
            ["suite"] = result.Suite,
            ["host"] = result.Host,
            ["tasks"] = metrics.Tasks,
            ["metrics"] = new JsonObject
            {
                ["recall@1"] = metrics.RecallAt1,
                ["recall@5"] = metrics.RecallAt5,
                ["mrr"] = metrics.Mrr,
                ["taskPassRate"] = metrics.TaskPassRate,
                ["meanFirstHitRank"] = metrics.MeanFirstHitRank,
                ["misses"] = metrics.Misses,
                // Trajectory metrics; not applicable to retrieval suites.
                ["schemaValidArgs"] = null,
                ["approvalDiscipline"] = null,
                ["taskSuccess"] = null
            },
            ["failures"] = new JsonArray(result.Failures.Select(outcome => (JsonNode)new JsonObject
            {
                ["id"] = outcome.Task.Id,
                ["task"] = outcome.Task.Task,
                ["expected"] = new JsonArray(outcome.Task.Expected.Select(id => (JsonNode)JsonValue.Create(id)).ToArray()),
                ["k"] = outcome.Task.K,
                ["firstHitRank"] = outcome.FirstHitRank,
                ["top"] = new JsonArray(outcome.Ranked.Take(5).Select(id => (JsonNode)JsonValue.Create(id)).ToArray())
            }).ToArray())
        };
    }

    public static string RenderMarkdown(JsonObject scorecard)
    {
        ArgumentNullException.ThrowIfNull(scorecard);
        var text = new StringBuilder();
        var dirty = scorecard["dirty"]?.GetValue<bool>() == true ? " (dirty worktree)" : string.Empty;
        text.Append("# Eval scorecard ").Append(scorecard["date"]).Append(' ').Append(scorecard["sha"]).Append(dirty).Append("\n\n");
        text.Append("Model: ").Append((string?)scorecard["model"] ?? "none (deterministic retrieval)").Append("\n\n");
        text.Append("| Suite | Tasks | recall@1 | recall@5 | MRR | Pass (own k) | Misses | Host |\n");
        text.Append("|---|---:|---:|---:|---:|---:|---:|---|\n");
        var suites = scorecard["suites"]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
        foreach (var suite in suites)
        {
            var metrics = suite["metrics"]!.AsObject();
            text.Append(CultureInfo.InvariantCulture,
                $"| {suite["suite"]} | {suite["tasks"]} | {F(metrics["recall@1"])} | {F(metrics["recall@5"])} | {F(metrics["mrr"])} | {F(metrics["taskPassRate"])} | {metrics["misses"]} | {suite["host"]} |\n");
        }

        foreach (var suite in suites)
        {
            var failures = suite["failures"]!.AsArray().OfType<JsonObject>().ToArray();
            text.Append("\n## ").Append(suite["suite"]).Append(" failures\n\n");
            if (failures.Length == 0)
            {
                text.Append("None.\n");
                continue;
            }

            text.Append("| Task | Query | Expected | k | First hit | Top 5 |\n|---|---|---|---:|---:|---|\n");
            foreach (var failure in failures)
            {
                var rank = failure["firstHitRank"] is { } value ? value.ToJsonString() : "miss";
                text.Append(CultureInfo.InvariantCulture,
                    $"| {failure["id"]} | {Escape((string?)failure["task"])} | {Join(failure["expected"])} | {failure["k"]} | {rank} | {Join(failure["top"])} |\n");
            }
        }

        return text.ToString();
    }

    private static string F(JsonNode? node) =>
        node is null ? "-" : node.GetValue<double>().ToString("0.000", CultureInfo.InvariantCulture);

    private static string Join(JsonNode? node) =>
        string.Join(", ", node?.AsArray().Select(item => $"`{(string?)item}`") ?? []);

    private static string Escape(string? value) => (value ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal);
}
