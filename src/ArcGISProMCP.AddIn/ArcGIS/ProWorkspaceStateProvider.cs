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
    public void AdvanceRevision() => AdvanceRevision(static () => "operation");

    /// <summary>
    /// Advances the revision. <paramref name="reason"/> is evaluated only when revision logging is
    /// on, so event handlers pay nothing for it otherwise.
    /// </summary>
    public void AdvanceRevision(Func<string> reason)
    {
        var sequence = Interlocked.Increment(ref _mutationSequence);
        if (RevisionLog.Enabled) RevisionLog.Append($"advance\t{sequence}\t{reason()}");
    }

    /// <summary>Records a host event that deliberately does not advance the revision.</summary>
    public void NoteIgnoredEvent(Func<string> reason)
    {
        if (RevisionLog.Enabled) RevisionLog.Append($"ignored\t{Interlocked.Read(ref _mutationSequence)}\t{reason()}");
    }

    public Task<WorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        dispatcher.OnMainCimThreadAsync(CreateSnapshot, cancellationToken);

    /// <summary>Upper bound for settling one write, drain included.</summary>
    internal static readonly TimeSpan SettleBudget = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Drains the host once (main CIM thread, then WPF dispatcher application-idle, then the main CIM
    /// thread again) so host events queued by the write are published, then samples until the
    /// revision is quiet. The whole settle is bounded by <see cref="SettleBudget"/>; when it runs
    /// out, a plain snapshot is returned and the revision log records it.
    /// </summary>
    public async Task<WorkspaceSnapshot> GetSettledSnapshotAsync(CancellationToken cancellationToken)
    {
        var result = await WorkspaceSnapshotSettler.SettleWithinBudgetAsync(
            DrainHostAsync, GetSnapshotAsync, SettleBudget, cancellationToken).ConfigureAwait(false);
        if (!RevisionLog.Enabled) return result.Snapshot;
        if (result.TimedOut)
            RevisionLog.Append($"settle-timeout\t{Interlocked.Read(ref _mutationSequence)}\t{result.Snapshot.Revision}\tbudget {SettleBudget.TotalMilliseconds:0} ms elapsed; published a plain sample");
        else if (!result.Settled)
            RevisionLog.Append($"settle-unsettled\t{Interlocked.Read(ref _mutationSequence)}\t{result.Snapshot.Revision}\t{result.Samples} samples without {WorkspaceSnapshotSettler.RequiredQuietSamples} quiet in a row");
        return result.Snapshot;
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
/// the host events behind them and settle outcomes to
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
