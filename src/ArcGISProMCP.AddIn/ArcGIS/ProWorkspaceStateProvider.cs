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
    public void AdvanceRevision() => AdvanceRevision("operation");

    /// <summary>Advances the revision; <paramref name="reason"/> is recorded when revision logging is on.</summary>
    public void AdvanceRevision(string reason)
    {
        var sequence = Interlocked.Increment(ref _mutationSequence);
        LogLine($"advance	{sequence}	{reason}");
    }

    /// <summary>Records a host event that deliberately does not advance the revision.</summary>
    public void NoteIgnoredEvent(string reason) => LogLine($"ignored	{Interlocked.Read(ref _mutationSequence)}	{reason}");

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
        if (result.TimedOut)
            LogLine($"settle-timeout\t{Interlocked.Read(ref _mutationSequence)}\t{result.Snapshot.Revision}\tbudget {SettleBudget.TotalMilliseconds:0} ms elapsed; published a plain sample");
        else if (!result.Settled)
            LogLine($"settle-unsettled\t{Interlocked.Read(ref _mutationSequence)}\t{result.Snapshot.Revision}\t{result.Samples} samples without {WorkspaceSnapshotSettler.RequiredQuietSamples} quiet in a row");
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

    /// <summary>Opt-in diagnostics: <c>ARCGIS_PRO_MCP_REVISION_LOG=1</c> records every revision change and the state behind it.</summary>
    private static readonly bool RevisionLogEnabled =
        string.Equals(Environment.GetEnvironmentVariable("ARCGIS_PRO_MCP_REVISION_LOG")?.Trim(), "1", StringComparison.Ordinal) ||
        string.Equals(Environment.GetEnvironmentVariable("ARCGIS_PRO_MCP_REVISION_LOG")?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private string? _lastLoggedRevision;

    private static void LogLine(string line)
    {
        if (!RevisionLogEnabled) return;
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArcGISProMCP", "diagnostics");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, $"revisions-{Environment.ProcessId}.log"), $"{DateTimeOffset.UtcNow:O}	{line}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void LogRevisionChange(string revision, string material)
    {
        if (!RevisionLogEnabled || string.Equals(revision, _lastLoggedRevision, StringComparison.Ordinal)) return;
        _lastLoggedRevision = revision;
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArcGISProMCP", "diagnostics");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, $"revisions-{Environment.ProcessId}.log"),
                $"{DateTimeOffset.UtcNow:O}	{revision}	{material}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
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
        LogRevisionChange(revision, revisionMaterial);

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
