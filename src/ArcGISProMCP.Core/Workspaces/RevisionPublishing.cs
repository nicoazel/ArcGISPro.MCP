namespace ArcGISProMCP.Core.Workspaces;

/// <summary>
/// A workspace whose revision is advanced by the host after each successful write and that can
/// wait for deferred host notifications to settle before the new revision is published.
/// </summary>
public interface IRevisionPublishingWorkspace : IWorkspaceStateProvider
{
    /// <summary>Marks the workspace as changed so the next snapshot carries a new revision.</summary>
    void AdvanceRevision();

    /// <summary>Returns a snapshot once consecutive samples report the same revision.</summary>
    Task<WorkspaceSnapshot> GetSettledSnapshotAsync(CancellationToken cancellationToken);
}

/// <summary>The outcome of <see cref="WorkspaceSnapshotSettler.SettleAsync"/>.</summary>
/// <param name="Snapshot">The settled snapshot, or the latest sample when the revision never settled.</param>
/// <param name="Settled">True when <see cref="WorkspaceSnapshotSettler.RequiredQuietSamples"/> consecutive samples repeated the revision.</param>
/// <param name="Samples">Number of samples taken, including the first (1 for a budget fallback).</param>
/// <param name="TimedOut">
/// The time budget of <see cref="WorkspaceSnapshotSettler.SettleWithinBudgetAsync"/> ran out, and
/// <paramref name="Snapshot"/> is a single plain sample taken afterwards.
/// </param>
public sealed record SettledSnapshot(WorkspaceSnapshot Snapshot, bool Settled, int Samples, bool TimedOut = false);

/// <summary>
/// Samples a workspace until its revision stops changing. Hosts publish some structural
/// notifications after their SDK mutators return (live ArcGIS Pro acceptance showed layer.add
/// notifications landing after a 50 ms window). The host drains its own queues once before
/// settling; this then waits for <see cref="RequiredQuietSamples"/> unchanged samples
/// <see cref="DefaultInterval"/> apart (a 150 ms quiet period), giving up after
/// <see cref="MaximumAttempts"/> samples. Hosts also bound the whole settle in time (the ArcGIS Pro
/// add-in allows at most 3 s), so the revision handed to the next serialized workflow step is the
/// one the host actually settles on.
/// </summary>
public static class WorkspaceSnapshotSettler
{
    /// <summary>Maximum number of samples taken after the first one.</summary>
    public const int MaximumAttempts = 60;

    /// <summary>
    /// Consecutive unchanged samples required before the revision counts as settled. Three samples
    /// 50 ms apart require the revision to stay unchanged for 150 ms after the host has drained its
    /// dispatcher and main CIM thread once: the drain publishes the event echoes the write queued,
    /// and the quiet period covers notifications raised by work those echoes queue in turn. Two
    /// samples (100 ms) would sit close to the 50 ms window that live acceptance showed was too
    /// short; four would add latency to every write without covering a different case.
    /// </summary>
    public const int RequiredQuietSamples = 3;

    /// <summary>Default delay between samples.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Returns the settled snapshot, or the latest sample when the revision never settled.</summary>
    public static async Task<WorkspaceSnapshot> WaitForSettledSnapshotAsync(
        Func<CancellationToken, Task<WorkspaceSnapshot>> sample,
        CancellationToken cancellationToken,
        TimeSpan? interval = null) =>
        (await SettleAsync(sample, cancellationToken, interval).ConfigureAwait(false)).Snapshot;

    /// <summary>
    /// Drains the host once, then settles, all within <paramref name="budget"/>. The drain and every
    /// sample are raced against the budget, so a host queue that never drains cannot hold a write's
    /// result indefinitely. When the budget runs out, one plain sample is taken with the caller's
    /// token and returned with <see cref="SettledSnapshot.TimedOut"/> set. Cancellation by the caller
    /// still throws.
    /// </summary>
    public static async Task<SettledSnapshot> SettleWithinBudgetAsync(
        Func<CancellationToken, Task> drain,
        Func<CancellationToken, Task<WorkspaceSnapshot>> sample,
        TimeSpan budget,
        CancellationToken cancellationToken,
        TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(drain);
        ArgumentNullException.ThrowIfNull(sample);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(budget);
        try
        {
            await drain(limit.Token).WaitAsync(limit.Token).ConfigureAwait(false);
            return await SettleAsync(token => sample(token).WaitAsync(token), limit.Token, interval).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var fallback = await sample(cancellationToken).ConfigureAwait(false);
            return new SettledSnapshot(fallback, Settled: false, Samples: 1, TimedOut: true);
        }
    }

    /// <summary>
    /// Samples until the revision settles or the attempt budget runs out, and reports which of the
    /// two happened so the host can record a revision that was published without settling.
    /// </summary>
    public static async Task<SettledSnapshot> SettleAsync(
        Func<CancellationToken, Task<WorkspaceSnapshot>> sample,
        CancellationToken cancellationToken,
        TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var delay = interval ?? DefaultInterval;
        var previous = await sample(cancellationToken).ConfigureAwait(false);
        var samples = 1;
        var quietSamples = 0;
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            var current = await sample(cancellationToken).ConfigureAwait(false);
            samples++;
            if (string.Equals(current.Revision, previous.Revision, StringComparison.Ordinal))
            {
                quietSamples++;
                if (quietSamples >= RequiredQuietSamples) return new SettledSnapshot(current, true, samples);
            }
            else
            {
                quietSamples = 0;
            }
            previous = current;
        }
        return new SettledSnapshot(previous, false, samples);
    }
}
