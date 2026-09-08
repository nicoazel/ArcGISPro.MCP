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
        _properties = MapMemberPropertiesChangedEvent.Subscribe(_ => workspace.AdvanceRevision(), true);
        _added = LayersAddedEvent.Subscribe(_ => workspace.AdvanceRevision(), true);
        _removed = LayersRemovedEvent.Subscribe(_ => workspace.AdvanceRevision(), true);
        _edits = EditCompletedEvent.Subscribe(_ => { workspace.AdvanceRevision(); return Task.CompletedTask; }, true);
        _elements = ElementEvent.Subscribe(args =>
        {
            if (args.Hint != ElementEventHint.SelectionChanged) workspace.AdvanceRevision();
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
