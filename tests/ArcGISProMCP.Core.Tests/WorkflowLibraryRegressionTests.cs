using System.Collections.Immutable;
using System.Text.Json;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Registry;
using ArcGISProMCP.Core.Workflows;
using Xunit;

namespace ArcGISProMCP.Core.Tests;

public sealed class WorkflowLibraryRegressionTests
{
    [Fact]
    public async Task RecordRunAsync_writes_one_json_object_per_line_for_ranking()
    {
        var root = Path.Combine(Path.GetTempPath(), "arcgis-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var library = new FileWorkflowLibrary(root, new OperationRegistry());
            await library.RecordRunAsync(Summary("run-1", "succeeded"), TestContext.Current.CancellationToken);
            await library.RecordRunAsync(Summary("run-2", "failed"), TestContext.Current.CancellationToken);

            var lines = await File.ReadAllLinesAsync(Path.Combine(root, "runs.jsonl"), TestContext.Current.CancellationToken);
            Assert.Equal(2, lines.Length);
            Assert.All(lines, line =>
            {
                using var json = JsonDocument.Parse(line);
                Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
            });
            var ranking = await library.RankAsync(TestContext.Current.CancellationToken);
            var item = Assert.Single(ranking);
            Assert.Equal(1, item.SuccessfulRuns);
            Assert.Equal(1, item.FailedRuns);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ValidateAsync_reports_duplicate_steps_without_throwing()
    {
        using var arguments = JsonDocument.Parse("{}");
        var duplicate = new WorkflowDefinition(
            "workflow.duplicate-steps", "1.0.0", "Duplicate", "Duplicate test",
            ImmutableHashSet<string>.Empty, ImmutableHashSet<string>.Empty,
            ImmutableArray<WorkflowParameter>.Empty,
            ImmutableArray.Create(
                new WorkflowStep("same", "arcgis.test.unknown", arguments.RootElement.Clone(), ImmutableArray<string>.Empty),
                new WorkflowStep("same", "arcgis.test.unknown", arguments.RootElement.Clone(), ImmutableArray<string>.Empty)));
        var root = Path.Combine(Path.GetTempPath(), "arcgis-mcp-tests", Guid.NewGuid().ToString("N"));
        using var library = new FileWorkflowLibrary(root, new OperationRegistry());

        var exception = await Record.ExceptionAsync(() => library.ValidateAsync(duplicate, TestContext.Current.CancellationToken));

        Assert.Null(exception);
        var result = await library.ValidateAsync(duplicate, TestContext.Current.CancellationToken);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "duplicate_step");
    }

    private static WorkflowRunSummary Summary(string runId, string outcome) => new(
        runId, "workflow.test", "1.0.0", DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow, outcome, outcome == "succeeded" ? 1 : 0,
        outcome == "failed" ? 1 : 0);
}
