using System.Collections.Immutable;
using ArcGISProMCP.Core.Workspaces;

namespace ArcGISProMCP.Core.Tests;

/// <summary>
/// A write publishes the revision the host settles on after its deferred notifications
/// (ArcGIS raises map-member and layout-element events after many SDK mutators return).
/// </summary>
public sealed class WorkspaceSnapshotSettlerTests
{
    [Fact]
    public async Task Returns_after_three_consecutive_samples_repeat_the_revision()
    {
        var samples = new SampleSequence("a", "b", "c", "c", "c", "c", "d");

        var settled = await WorkspaceSnapshotSettler.SettleAsync(samples.NextAsync, TestContext.Current.CancellationToken, TimeSpan.Zero);

        Assert.Equal("c", settled.Snapshot.Revision);
        Assert.True(settled.Settled);
        Assert.Equal(6, settled.Samples);
        Assert.Equal(6, samples.Taken);
    }

    [Fact]
    public async Task A_changing_revision_resets_the_quiet_count()
    {
        var samples = new SampleSequence("a", "a", "a", "b", "b", "b", "b", "c");

        var snapshot = await WorkspaceSnapshotSettler.WaitForSettledSnapshotAsync(samples.NextAsync, TestContext.Current.CancellationToken, TimeSpan.Zero);

        Assert.Equal("b", snapshot.Revision);
        Assert.Equal(7, samples.Taken);
    }

    [Fact]
    public async Task Gives_up_after_the_attempt_budget_with_the_latest_sample()
    {
        var revisions = Enumerable.Range(0, 100).Select(index => $"r{index}").ToArray();
        var samples = new SampleSequence(revisions);

        var settled = await WorkspaceSnapshotSettler.SettleAsync(samples.NextAsync, TestContext.Current.CancellationToken, TimeSpan.Zero);

        Assert.Equal(1 + WorkspaceSnapshotSettler.MaximumAttempts, samples.Taken);
        Assert.Equal($"r{WorkspaceSnapshotSettler.MaximumAttempts}", settled.Snapshot.Revision);
        // The host logs a revision published without settling.
        Assert.False(settled.Settled);
        Assert.Equal(1 + WorkspaceSnapshotSettler.MaximumAttempts, settled.Samples);
    }

    [Fact]
    public async Task A_revision_that_settles_on_the_last_attempt_counts_as_settled()
    {
        var total = 1 + WorkspaceSnapshotSettler.MaximumAttempts;
        var quietFrom = total - WorkspaceSnapshotSettler.RequiredQuietSamples - 1;
        var revisions = Enumerable.Range(0, total)
            .Select(index => index < quietFrom ? $"r{index}" : "final")
            .ToArray();
        var samples = new SampleSequence(revisions);

        var settled = await WorkspaceSnapshotSettler.SettleAsync(samples.NextAsync, TestContext.Current.CancellationToken, TimeSpan.Zero);

        Assert.True(settled.Settled);
        Assert.Equal("final", settled.Snapshot.Revision);
        Assert.Equal(total, settled.Samples);
    }

    [Fact]
    public void The_settle_budget_matches_the_add_in_defaults()
    {
        Assert.Equal(60, WorkspaceSnapshotSettler.MaximumAttempts);
        Assert.Equal(3, WorkspaceSnapshotSettler.RequiredQuietSamples);
        Assert.Equal(TimeSpan.FromMilliseconds(50), WorkspaceSnapshotSettler.DefaultInterval);
    }

    [Fact]
    public async Task Cancellation_stops_sampling()
    {
        using var cancellation = new CancellationTokenSource();
        var samples = new SampleSequence("a", "b", "c", "d") { OnSample = count => { if (count == 2) cancellation.Cancel(); } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WorkspaceSnapshotSettler.WaitForSettledSnapshotAsync(samples.NextAsync, cancellation.Token, TimeSpan.Zero));
        Assert.Equal(2, samples.Taken);
    }

    [Fact]
    public async Task Budgeted_settle_drains_once_then_settles()
    {
        var drains = 0;
        var samples = new SampleSequence("a", "b", "b", "b", "b");

        var settled = await WorkspaceSnapshotSettler.SettleWithinBudgetAsync(
            _ => { drains++; return Task.CompletedTask; },
            samples.NextAsync,
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken,
            TimeSpan.Zero);

        Assert.Equal(1, drains);
        Assert.True(settled.Settled);
        Assert.False(settled.TimedOut);
        Assert.Equal("b", settled.Snapshot.Revision);
    }

    [Fact]
    public async Task A_drain_that_never_completes_falls_back_to_one_plain_sample_when_the_budget_runs_out()
    {
        var never = new TaskCompletionSource();
        var samples = new SampleSequence("fallback");

        var settled = await WorkspaceSnapshotSettler.SettleWithinBudgetAsync(
            _ => never.Task,
            samples.NextAsync,
            TimeSpan.FromMilliseconds(50),
            TestContext.Current.CancellationToken,
            TimeSpan.Zero);

        Assert.True(settled.TimedOut);
        Assert.False(settled.Settled);
        Assert.Equal("fallback", settled.Snapshot.Revision);
        Assert.Equal(1, samples.Taken);
    }

    [Fact]
    public async Task A_revision_that_keeps_changing_is_cut_off_by_the_budget()
    {
        var revisions = Enumerable.Range(0, 10_000).Select(index => $"r{index}").ToArray();
        var samples = new SampleSequence(revisions);

        var settled = await WorkspaceSnapshotSettler.SettleWithinBudgetAsync(
            _ => Task.CompletedTask,
            samples.NextAsync,
            TimeSpan.FromMilliseconds(100),
            TestContext.Current.CancellationToken,
            TimeSpan.FromMilliseconds(40));

        Assert.True(settled.TimedOut);
        Assert.False(settled.Settled);
        Assert.True(samples.Taken < 1 + WorkspaceSnapshotSettler.MaximumAttempts);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_turned_into_a_fallback()
    {
        using var cancellation = new CancellationTokenSource();
        var never = new TaskCompletionSource();
        var samples = new SampleSequence("a");

        var settle = WorkspaceSnapshotSettler.SettleWithinBudgetAsync(
            _ => never.Task, samples.NextAsync, TimeSpan.FromMinutes(5), cancellation.Token, TimeSpan.Zero);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => settle);
        Assert.Equal(0, samples.Taken);
    }

    private sealed class SampleSequence(params string[] revisions)
    {
        public int Taken { get; private set; }

        public Action<int>? OnSample { get; init; }

        public Task<WorkspaceSnapshot> NextAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var revision = revisions[Math.Min(Taken, revisions.Length - 1)];
            Taken++;
            OnSample?.Invoke(Taken);
            return Task.FromResult(new WorkspaceSnapshot(
                revision,
                DateTimeOffset.UtcNow,
                new ProjectState("Fixture", null, false, true),
                ImmutableArray<MapState>.Empty,
                ImmutableArray<LayoutState>.Empty,
                null,
                null,
                ImmutableArray<CapabilityState>.Empty));
        }
    }
}
