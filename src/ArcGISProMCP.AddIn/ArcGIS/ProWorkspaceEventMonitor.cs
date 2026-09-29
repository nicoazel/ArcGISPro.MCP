using ArcGIS.Core.Events;
using ArcGIS.Desktop.Editing.Events;
using ArcGIS.Desktop.Layouts.Events;
using ArcGIS.Desktop.Mapping.Events;
using ArcGISProMCP.Operations;

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
            var hints = args.EventHints?.Select(static hint => hint.ToString()).ToArray() ?? [];
            // A data source (re)connecting or a Contents node expanding is not a project edit, and
            // ArcGIS raises these asynchronously after layer.add and similar writes return.
            if (WorkspaceEventHints.IsNonContentOnly(hints))
                workspace.NoteIgnoredEvent(() => "MapMemberPropertiesChanged:" + string.Join(',', hints));
            else
                workspace.AdvanceRevision(() => "MapMemberPropertiesChanged:" + string.Join(',', hints));
        }, true);
        _added = LayersAddedEvent.Subscribe(_ => workspace.AdvanceRevision(static () => "LayersAdded"), true);
        _removed = LayersRemovedEvent.Subscribe(_ => workspace.AdvanceRevision(static () => "LayersRemoved"), true);
        _edits = EditCompletedEvent.Subscribe(_ => { workspace.AdvanceRevision(static () => "EditCompleted"); return Task.CompletedTask; }, true);
        _elements = ElementEvent.Subscribe(args =>
        {
            var hint = args.Hint;
            if (hint != ElementEventHint.SelectionChanged) workspace.AdvanceRevision(() => "ElementEvent:" + hint);
        }, true);
    }

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
