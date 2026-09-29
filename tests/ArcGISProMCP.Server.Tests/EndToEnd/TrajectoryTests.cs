using Xunit;

namespace ArcGISProMCP.Server.Tests.EndToEnd;

/// <summary>
/// E3 golden trajectories (evals/trajectories/*.json) replayed against the in-process end-to-end
/// server. Each must score schemaValidArgs = 1, approvalDiscipline = 1 and taskSuccess.
/// </summary>
[Trait("Category", "eval")]
public sealed class TrajectoryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<string> Trajectories =>
        [.. Directory.EnumerateFiles(Trajectory.Directory, "*.json").Select(path => Path.GetFileNameWithoutExtension(path)!).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Trajectories))]
    public async Task Golden_trajectory_passes(string name)
    {
        var trajectory = Trajectory.Load(Path.Combine(Trajectory.Directory, name + ".json"));

        var result = await TrajectoryRunner.RunAsync(trajectory, Token);

        TestContext.Current.TestOutputHelper?.WriteLine(result.ToString());
        Assert.True(result.TaskSuccess, result.ToString());
        Assert.Equal(1.0, result.SchemaValidArgs);
        Assert.Equal(1.0, result.ApprovalDiscipline);
    }

    [Fact]
    public void The_corpus_has_six_to_ten_uniquely_named_trajectories_with_final_state_checks()
    {
        var trajectories = Directory.EnumerateFiles(Trajectory.Directory, "*.json").Select(path => (Path: path, Trajectory: Trajectory.Load(path))).ToArray();

        Assert.InRange(trajectories.Length, 6, 10);
        Assert.All(trajectories, item =>
        {
            Assert.Equal(Path.GetFileNameWithoutExtension(item.Path), item.Trajectory.Id);
            Assert.False(string.IsNullOrWhiteSpace(item.Trajectory.Task));
            Assert.Equal(System.Text.Json.JsonValueKind.Object, item.Trajectory.Expect.ValueKind);
            Assert.NotEmpty(item.Trajectory.Expect.EnumerateObject());
        });
    }

    [Fact]
    public async Task The_grader_flags_an_invoke_whose_arguments_differ_from_the_reviewed_ones()
    {
        var trajectory = Trajectory.Parse("""
            {
              "id": "bad-approval", "task": "Delete parcel 8 but invoke a different target.",
              "steps": [
                {"tool": "system_get_state", "arguments": {}, "capture": {"revision": "$.workspace.revision"}},
                {"tool": "approval_request", "arguments": {"operationId": "feature.delete", "arguments": {"layer": "Parcels", "target": {"objectId": 8}}, "expectedRevision": "${revision}"}, "capture": {"requestId": "$.requestId"}},
                {"approve": "feature.delete"},
                {"tool": "approval_status", "arguments": {"requestId": "${requestId}"}, "capture": {"token": "$.confirmationToken"}},
                {"tool": "registry_invoke", "arguments": {"operationId": "feature.delete", "arguments": {"layer": "Parcels", "target": {"objectId": 7}}, "expectedRevision": "${revision}", "confirmationToken": "${token}"}}
              ],
              "expect": {"rowCounts": {"Parcels": 7}}
            }
            """);

        var result = await TrajectoryRunner.RunAsync(trajectory, Token);

        Assert.False(result.TaskSuccess);
        Assert.Equal(0.0, result.ApprovalDiscipline);
        Assert.Equal(1.0, result.SchemaValidArgs);
        Assert.Contains(result.Failures, failure => failure.Contains("without a preceding approval_request", StringComparison.Ordinal));
        // The host agrees: the token is bound to the reviewed arguments.
        Assert.Contains(result.Failures, failure => failure.Contains("unexpected isError confirmation_required", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("Parcels has 8 rows, expected 7", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_grader_flags_arguments_that_break_the_tool_or_operation_schema()
    {
        var trajectory = Trajectory.Parse("""
            {
              "id": "bad-schema", "task": "Malformed calls.",
              "steps": [
                {"tool": "registry_search", "arguments": {"query": "parcels", "limit": "five"}, "expectError": "sdk_rejected_arguments"},
                {"tool": "registry_invoke", "arguments": {"operationId": "feature.query", "arguments": {"layer": "Parcels", "limit": 0}}, "expectError": "invalid_arguments"},
                {"tool": "system_get_state", "arguments": {"verbose": true}}
              ],
              "expect": {"dirty": false}
            }
            """);

        var result = await TrajectoryRunner.RunAsync(trajectory, Token);

        Assert.Equal(0.0, result.SchemaValidArgs);
        Assert.Contains(result.Failures, failure => failure.Contains("$.limit: expected integer", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("arguments.limit", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("unknown argument 'verbose'", StringComparison.Ordinal));
    }
}
