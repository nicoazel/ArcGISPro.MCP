using System.Text.Json;
using ArcGISProMCP.Core.Infrastructure;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.Core.Tests;

public sealed class JsonLineAuditLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arcgis-mcp-audit-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Writes_one_web_cased_json_line_per_event_with_safety_fields()
    {
        var path = Path.Combine(_root, "audit", "operations.jsonl");
        using var log = new JsonLineAuditLog(path);

        await log.WriteAsync(Event("map.list"), TestContext.Current.CancellationToken);
        await log.WriteAsync(Event("feature.update") with
        {
            Kind = OperationAuditKinds.Approval,
            Decision = "approved",
            Actor = "panel-card"
        }, TestContext.Current.CancellationToken);
        await log.WriteAsync(Event("gp.run") with { AutonomousBypass = true }, TestContext.Current.CancellationToken);

        var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(3, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("operation", first.RootElement.GetProperty("kind").GetString());
        Assert.False(first.RootElement.GetProperty("autonomousBypass").GetBoolean());
        using var approval = JsonDocument.Parse(lines[1]);
        Assert.Equal("approval", approval.RootElement.GetProperty("kind").GetString());
        Assert.Equal("approved", approval.RootElement.GetProperty("decision").GetString());
        Assert.Equal("panel-card", approval.RootElement.GetProperty("actor").GetString());
        using var bypass = JsonDocument.Parse(lines[2]);
        Assert.True(bypass.RootElement.GetProperty("autonomousBypass").GetBoolean());
    }

    [Fact]
    public async Task Rotates_when_the_active_file_exceeds_the_size_limit()
    {
        var path = Path.Combine(_root, "operations.jsonl");
        var clock = new SteppingClock();
        using var log = new JsonLineAuditLog(path, maximumBytes: 64, retainedFiles: 5, timeProvider: clock);

        await log.WriteAsync(Event("first"), TestContext.Current.CancellationToken);
        await log.WriteAsync(Event("second"), TestContext.Current.CancellationToken);

        var rotated = Directory.GetFiles(_root, "operations.*.jsonl");
        var archive = Assert.Single(rotated);
        Assert.Matches(@"operations\.\d{8}T\d{9}Z\.jsonl$", Path.GetFileName(archive));
        Assert.Contains("\"first\"", await File.ReadAllTextAsync(archive, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        var active = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
        Assert.Contains("\"second\"", Assert.Single(active), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keeps_only_the_newest_rotated_files()
    {
        var path = Path.Combine(_root, "operations.jsonl");
        var clock = new SteppingClock();
        using var log = new JsonLineAuditLog(path, maximumBytes: 1, retainedFiles: 2, timeProvider: clock);

        for (var index = 0; index < 6; index++)
            await log.WriteAsync(Event($"op{index}"), TestContext.Current.CancellationToken);

        var rotated = Directory.GetFiles(_root, "operations.*.jsonl").OrderBy(file => file, StringComparer.Ordinal).ToArray();
        Assert.Equal(2, rotated.Length);
        Assert.Contains("\"op3\"", await File.ReadAllTextAsync(rotated[0], TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Contains("\"op4\"", await File.ReadAllTextAsync(rotated[1], TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Contains("\"op5\"", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Same_timestamp_rotations_do_not_overwrite_each_other()
    {
        var path = Path.Combine(_root, "operations.jsonl");
        using var log = new JsonLineAuditLog(path, maximumBytes: 1, retainedFiles: 5, timeProvider: new FrozenClock());

        for (var index = 0; index < 3; index++)
            await log.WriteAsync(Event($"op{index}"), TestContext.Current.CancellationToken);

        Assert.Equal(2, Directory.GetFiles(_root, "operations.*.jsonl").Length);
    }

    [Fact]
    public void Defaults_are_sixteen_mebibytes_and_five_files()
    {
        Assert.Equal(16L * 1024 * 1024, JsonLineAuditLog.DefaultMaximumBytes);
        Assert.Equal(5, JsonLineAuditLog.DefaultRetainedFiles);
    }

    private static OperationAuditEvent Event(string operationId) => new(
        "correlation",
        operationId,
        "1.0.0",
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch,
        true,
        "r1",
        "r1",
        null,
        "hash");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private sealed class SteppingClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now = _now.AddSeconds(1);
    }

    private sealed class FrozenClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    }
}
