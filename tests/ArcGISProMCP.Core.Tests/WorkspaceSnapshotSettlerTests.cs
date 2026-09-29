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
    public async Task Returns_after_four_consecutive_samples_repeat_the_revision()
    {
        var samples = new SampleSequence("a", "b", "c", "c", "c", "c", "c", "d");

        var snapshot = await WorkspaceSnapshotSettler.WaitForSettledSnapshotAsync(samples.NextAsync, TestContext.Current.CancellationToken, TimeSpan.Zero);

        Assert.Equal("c", snapshot.Revision);
        Assert.Equal(7, samples.Taken);
    }

    [Fact]
    public async Task A_changing_revision_resets_the_quiet_count()
    {
        var samples = new SampleSequence("a", "a", "a", "b", "b", "b", "b", "b");

        var snapshot = await WorkspaceSnapshotSettler.WaitForSettledSnapshotAsync(samples.NextAsync, TestContext.Current.CancellationToken, TimeSpan.Zero);

        Assert.Equal("b", snapshot.Revision);
        Assert.Equal(8, samples.Taken);
    }

    [Fact]
    public async Task Gives_up_after_the_attempt_budget_with_the_latest_sample()
    {
        var revisions = Enumerable.Range(0, 100).Select(index => $"r{index}").ToArray();
        var samples = new SampleSequence(revisions);

        var snapshot = await WorkspaceSnapshotSettler.WaitForSettledSnapshotAsync(samples.NextAsync, TestContext.Current.CancellationToken, TimeSpan.Zero);

        Assert.Equal(1 + WorkspaceSnapshotSettler.MaximumAttempts, samples.Taken);
        Assert.Equal($"r{WorkspaceSnapshotSettler.MaximumAttempts}", snapshot.Revision);
    }

    [Fact]
    public void The_settle_budget_matches_the_add_in_defaults()
    {
        Assert.Equal(60, WorkspaceSnapshotSettler.MaximumAttempts);
        Assert.Equal(4, WorkspaceSnapshotSettler.RequiredQuietSamples);
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
