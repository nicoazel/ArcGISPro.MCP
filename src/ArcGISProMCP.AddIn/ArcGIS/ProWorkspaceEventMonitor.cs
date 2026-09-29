using ArcGIS.Core.Events;
using ArcGIS.Desktop.Editing.Events;
using ArcGIS.Desktop.Layouts.Events;
using ArcGIS.Desktop.Mapping.Events;

namespace ArcGISProMCP.AddIn.ArcGIS;

/// <summary>
/// Advances optimistic revisions for common host edits even when project.IsDirty was already true.
/// Not a database lock: edits made outside Pro and unreported plugin changes remain outside this guard.
/// </summary>
internal sealed class ProWorkspaceEventMonitor : IDisposable
{
    private readonly SubscriptionToken _properties;
    private readonly SubscriptionToken _added;
    private readonly SubscriptionToken _removed;
    private readonly SubscriptionToken _edits;
    private readonly SubscriptionToken _elements;
    private int _disposed;

    public ProWorkspaceEventMonitor(ProWorkspaceStateProvider workspace)
    {
        _properties = MapMemberPropertiesChangedEvent.Subscribe(args =>
        {
            var hints = args.EventHints?.ToArray() ?? [];
            var reason = "MapMemberPropertiesChanged:" + string.Join(',', hints);
            // A data source (re)connecting or a Contents node expanding is not a project edit, and
            // ArcGIS raises these asynchronously after layer.add and similar writes return.
            if (hints.Length > 0 && hints.All(IsNonContentHint)) workspace.NoteIgnoredEvent(reason);
            else workspace.AdvanceRevision(reason);
        }, true);
        _added = LayersAddedEvent.Subscribe(_ => workspace.AdvanceRevision("LayersAdded"), true);
        _removed = LayersRemovedEvent.Subscribe(_ => workspace.AdvanceRevision("LayersRemoved"), true);
        _edits = EditCompletedEvent.Subscribe(_ => { workspace.AdvanceRevision("EditCompleted"); return Task.CompletedTask; }, true);
        _elements = ElementEvent.Subscribe(args =>
        {
            if (args.Hint != ElementEventHint.SelectionChanged) workspace.AdvanceRevision("ElementEvent:" + args.Hint);
        }, true);
    }

    internal static bool IsNonContentHint(MapMemberEventHint hint) =>
        hint is MapMemberEventHint.ConnectionStatus or MapMemberEventHint.Expansion;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        MapMemberPropertiesChangedEvent.Unsubscribe(_properties);
        LayersAddedEvent.Unsubscribe(_added);
        LayersRemovedEvent.Unsubscribe(_removed);
        EditCompletedEvent.Unsubscribe(_edits);
        ElementEvent.Unsubscribe(_elements);
    }
}
