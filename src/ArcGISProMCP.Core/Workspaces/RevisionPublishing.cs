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

/// <summary>
/// Samples a workspace until its revision stops changing. Hosts publish some structural
/// notifications after their SDK mutators return; waiting for a quiet period makes the revision
/// handed to the next serialized workflow step the one the host actually settles on.
/// </summary>
public static class WorkspaceSnapshotSettler
{
    /// <summary>Maximum number of samples taken after the first one.</summary>
    public const int MaximumAttempts = 20;

    /// <summary>Consecutive unchanged samples required before the revision counts as settled.</summary>
    public const int RequiredQuietSamples = 2;

    /// <summary>Default delay between samples.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(25);

    public static async Task<WorkspaceSnapshot> WaitForSettledSnapshotAsync(
        Func<CancellationToken, Task<WorkspaceSnapshot>> sample,
        CancellationToken cancellationToken,
        TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var delay = interval ?? DefaultInterval;
        var previous = await sample(cancellationToken).ConfigureAwait(false);
        var quietSamples = 0;
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            var current = await sample(cancellationToken).ConfigureAwait(false);
            if (string.Equals(current.Revision, previous.Revision, StringComparison.Ordinal))
            {
                quietSamples++;
                if (quietSamples >= RequiredQuietSamples) return current;
            }
            else
            {
                quietSamples = 0;
            }
            previous = current;
        }
        return previous;
    }
}
