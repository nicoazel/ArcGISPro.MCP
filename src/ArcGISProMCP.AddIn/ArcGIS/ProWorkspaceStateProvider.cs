using System.Diagnostics;
using System.IO;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.AddIn.Services;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Workspaces;

namespace ArcGISProMCP.AddIn.ArcGIS;

internal sealed class ProWorkspaceStateProvider(IOperationDispatcher dispatcher) : IRevisionPublishingWorkspace
{
    private long _mutationSequence;
    private long _lastHostEventTimestamp;

    /// <summary>Advances the revision for an MCP write.</summary>
    public void AdvanceRevision()
    {
        var sequence = Interlocked.Increment(ref _mutationSequence);
        if (RevisionLog.Enabled) RevisionLog.Append($"advance\t{sequence}\toperation");
    }

    /// <summary>
    /// Advances the revision for an ArcGIS host event and records when it arrived, so a settle can
    /// wait for the host to stop raising events. <paramref name="reason"/> is evaluated only when
    /// revision logging is on, so event handlers pay nothing for it otherwise.
    /// </summary>
    public void AdvanceRevision(Func<string> reason)
    {
        Interlocked.Exchange(ref _lastHostEventTimestamp, Stopwatch.GetTimestamp());
        var sequence = Interlocked.Increment(ref _mutationSequence);
        if (RevisionLog.Enabled) RevisionLog.Append($"advance\t{sequence}\t{reason()}");
    }

    /// <summary>Records a host event that deliberately does not advance the revision.</summary>
    public void NoteIgnoredEvent(Func<string> reason)
    {
        Interlocked.Exchange(ref _lastHostEventTimestamp, Stopwatch.GetTimestamp());
        if (RevisionLog.Enabled) RevisionLog.Append($"ignored\t{Interlocked.Read(ref _mutationSequence)}\t{reason()}");
    }

    /// <summary>
    /// How long ArcGIS must raise no workspace events before a settle samples the revision. Live
    /// acceptance showed a layout element echo arriving a few milliseconds after a 150 ms
    /// revision-quiet window following a data-source swap; waiting on event timestamps costs no
    /// extra snapshots.
    /// </summary>
    internal static readonly TimeSpan HostEventQuietWindow = TimeSpan.FromMilliseconds(300);

    private async Task WaitForHostEventQuietAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var last = Interlocked.Read(ref _lastHostEventTimestamp);
            if (last == 0) return;
            var since = Stopwatch.GetElapsedTime(last);
            if (since >= HostEventQuietWindow) return;
            await Task.Delay(HostEventQuietWindow - since, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<WorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        dispatcher.OnMainCimThreadAsync(CreateSnapshot, cancellationToken);

    /// <summary>Upper bound for settling one write, drain included.</summary>
    internal static readonly TimeSpan SettleBudget = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Drains the host once (main CIM thread, then WPF dispatcher application-idle, then the main CIM
    /// thread again) so host events queued by the write are published, then samples until the
    /// revision is quiet. The whole settle is bounded by <see cref="SettleBudget"/>; when it runs
    /// out, a plain snapshot is returned. With revision logging on, every settle is timed and logged
    /// (see <see cref="GetSettledSnapshotLoggedAsync"/>); with it off, nothing is timed or formatted.
    /// </summary>
    public async Task<WorkspaceSnapshot> GetSettledSnapshotAsync(CancellationToken cancellationToken)
    {
        if (RevisionLog.Enabled) return await GetSettledSnapshotLoggedAsync(cancellationToken).ConfigureAwait(false);
        var result = await WorkspaceSnapshotSettler.SettleWithinBudgetAsync(
            DrainAndWaitForQuietAsync, GetSnapshotAsync, SettleBudget, cancellationToken).ConfigureAwait(false);
        return result.Snapshot;
    }

    /// <summary>
    /// The same settle, timed for tuning: appends
    /// <c>settle-ok|settle-timeout|settle-unsettled \t drainMs \t sampleMs \t samples \t revision</c>.
    /// <c>drainMs</c> runs from the start of the settle to the end of the host drain and
    /// <c>sampleMs</c> from there to the end of the settle (the quiet samples, or the budget fallback
    /// sample). When the drain did not finish within the budget, <c>drainMs</c> covers the whole
    /// settle and <c>sampleMs</c> is 0. Measurement only: the settle itself is unchanged.
    /// </summary>
    private async Task<WorkspaceSnapshot> GetSettledSnapshotLoggedAsync(CancellationToken cancellationToken)
    {
        var timing = new SettleTiming(DrainAndWaitForQuietAsync);
        var started = Stopwatch.GetTimestamp();
        var result = await WorkspaceSnapshotSettler.SettleWithinBudgetAsync(
            timing.DrainAsync, GetSnapshotAsync, SettleBudget, cancellationToken).ConfigureAwait(false);
        var ended = Stopwatch.GetTimestamp();
        // A drain abandoned by the budget may still finish later; only a drain that ended inside
        // this settle splits it.
        var drained = timing.DrainedAt;
        var drainEnd = drained != 0 && drained <= ended ? drained : ended;
        var drainMs = (long)Stopwatch.GetElapsedTime(started, drainEnd).TotalMilliseconds;
        var sampleMs = (long)Stopwatch.GetElapsedTime(drainEnd, ended).TotalMilliseconds;
        var outcome = result.TimedOut ? "settle-timeout" : result.Settled ? "settle-ok" : "settle-unsettled";
        RevisionLog.Append($"{outcome}\t{drainMs}\t{sampleMs}\t{result.Samples}\t{result.Snapshot.Revision}");
        return result.Snapshot;
    }

    /// <summary>Records when the host drain of one settle finished (a <see cref="Stopwatch"/> timestamp).</summary>
    private sealed class SettleTiming(Func<CancellationToken, Task> drain)
    {
        private long _drainedAt;

        public long DrainedAt => Interlocked.Read(ref _drainedAt);

        public async Task DrainAsync(CancellationToken cancellationToken)
        {
            await drain(cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _drainedAt, Stopwatch.GetTimestamp());
        }
    }

    /// <summary>The host drain followed by the host-event quiet window; both count against the budget.</summary>
    private async Task DrainAndWaitForQuietAsync(CancellationToken cancellationToken)
    {
        await DrainHostAsync(cancellationToken).ConfigureAwait(false);
        await WaitForHostEventQuietAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DrainHostAsync(CancellationToken cancellationToken)
    {
        // The write's own MCT work, then UI-thread event handlers (application-idle runs after every
        // higher-priority dispatcher item), then MCT work those handlers queued.
        await QueuedTask.Run(static () => { }).WaitAsync(cancellationToken).ConfigureAwait(false);
        var application = System.Windows.Application.Current;
        if (application is not null)
        {
            await application.Dispatcher
                .InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle, cancellationToken)
                .Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        await QueuedTask.Run(static () => { }).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private string? _lastLoggedRevision;

    private void LogRevisionChange(string revision, Func<string> material)
    {
        if (!RevisionLog.Enabled || string.Equals(revision, _lastLoggedRevision, StringComparison.Ordinal)) return;
        _lastLoggedRevision = revision;
        RevisionLog.Append($"{revision}\t{material()}");
    }

    private WorkspaceSnapshot CreateSnapshot()
    {
        var project = Project.Current;
        if (project is null)
        {
            global::ArcGISProMCP.AddIn.ArcGISProMcpModule.Instance?.RefreshHostDiscovery(null, null);
            return new WorkspaceSnapshot(
                "closed",
                DateTimeOffset.UtcNow,
                new ProjectState(null, null, false, false),
                [],
                [],
                null,
                null,
                [new CapabilityState("arcgis-pro", true, typeof(Map).Assembly.GetName().Version?.ToString())]);
        }

        global::ArcGISProMCP.AddIn.ArcGISProMcpModule.Instance?.RefreshHostDiscovery(project.Name, project.URI);

        var activeMap = MapView.Active?.Map;
        var maps = project.GetItems<MapProjectItem>()
            .Select(item => item.GetMap())
            .Select(map => new MapState(
                ProHandles.ForMap(map),
                map.Name,
                map.MapType.ToString(),
                map.GetLayersAsFlattenedList().Count,
                string.Equals(map.URI, activeMap?.URI, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(map => map.Name, StringComparer.Ordinal)
            .ToImmutableArray();

        var activeLayout = LayoutView.Active?.Layout;
        var layouts = project.GetItems<LayoutProjectItem>()
            .Select(item => item.GetLayout())
            .Select(layout => new LayoutState(
                ProHandles.ForLayout(layout),
                layout.Name,
                layout.GetElementsAsFlattenedList().OfType<MapFrame>().Count(),
                string.Equals(layout.URI, activeLayout?.URI, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(layout => layout.Name, StringComparer.Ordinal)
            .ToImmutableArray();

        var projectState = new ProjectState(project.Name, project.URI, project.IsDirty, true);
        var revisionMaterial = string.Join('|',
            Interlocked.Read(ref _mutationSequence),
            project.URI,
            project.Name,
            project.IsDirty,
            // Which view has focus is UI state, not project content: it can change without any edit
            // (live acceptance saw spurious workspace_revision_mismatch). Activations made through
            // map.activate/layout.activate still advance the revision via the mutation sequence.
            string.Join(';', maps.Select(map => $"{map.Id}:{map.Name}:{map.LayerCount}")),
            string.Join(';', layouts.Select(layout => $"{layout.Id}:{layout.Name}:{layout.MapFrameCount}")));
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revisionMaterial)))[..16].ToLowerInvariant();
        LogRevisionChange(revision, () => revisionMaterial);

        return new WorkspaceSnapshot(
            revision,
            DateTimeOffset.UtcNow,
            projectState,
            maps,
            layouts,
            activeMap is null ? null : ProHandles.ForMap(activeMap),
            activeLayout is null ? activeMap is null ? null : ProHandles.ForMap(activeMap) : ProHandles.ForLayout(activeLayout),
            [
                new CapabilityState("arcgis-pro", true, typeof(Map).Assembly.GetName().Version?.ToString()),
                new CapabilityState("maps", true),
                new CapabilityState("feature-editing", true),
                new CapabilityState("metadata", true),
                new CapabilityState("layouts", true),
                new CapabilityState("geoprocessing", true),
                new CapabilityState(
                    "arcpy",
                    ArcPyCapabilityState.Enabled,
                    ArcPyCapabilityState.Enabled
                        ? "Explicitly enabled; the configured ArcPy runtime is validated before script inspection or execution."
                        : "Disabled by local configuration."),
                new CapabilityState(
                    "autonomous-control",
                    AutonomousControlState.Enabled,
                    AutonomousControlState.Enabled
                        ? "Explicitly enabled; risky operations bypass local review but still require current workspace revisions."
                        : "Disabled; risky operations require local review."),
                new CapabilityState("visual-observations", true)
            ]);
    }
}

/// <summary>
/// Opt-in diagnostics: <c>ARCGIS_PRO_MCP_REVISION_LOG=1</c> (or <c>true</c>) appends revision changes,
/// the host events behind them and the outcome and timing of every post-write settle to
/// <c>%LOCALAPPDATA%\ArcGISProMCP\diagnostics\revisions-&lt;pid&gt;.log</c>, one file per ArcGIS Pro
/// process. Lines include the project URI and map and layout names. Writing stops once the file
/// reaches <see cref="MaximumBytes"/>. Callers check <see cref="Enabled"/> before building a line.
/// </summary>
internal static class RevisionLog
{
    /// <summary>The log stops growing at this size; delete the file to resume logging.</summary>
    internal const long MaximumBytes = 50L * 1024 * 1024;

    public static bool Enabled { get; } = IsEnabled(Environment.GetEnvironmentVariable("ARCGIS_PRO_MCP_REVISION_LOG"));

    private static readonly Lock Gate = new();
    private static bool _capped;

    internal static bool IsEnabled(string? value)
    {
        var trimmed = value?.Trim();
        return string.Equals(trimmed, "1", StringComparison.Ordinal) ||
               string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase);
    }

    public static void Append(string line)
    {
        if (!Enabled) return;
        lock (Gate)
        {
            if (_capped) return;
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArcGISProMCP", "diagnostics");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"revisions-{Environment.ProcessId}.log");
                var file = new FileInfo(path);
                if (file.Exists && file.Length >= MaximumBytes)
                {
                    _capped = true;
                    File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O}\tcapped\tlog reached {MaximumBytes / (1024 * 1024)} MB; no further lines are written{Environment.NewLine}");
                    return;
                }
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O}\t{line}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
