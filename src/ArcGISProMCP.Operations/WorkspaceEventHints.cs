namespace ArcGISProMCP.Operations;

/// <summary>
/// Host-neutral classification of ArcGIS map member event hints (the names of
/// <c>ArcGIS.Desktop.Mapping.Events.MapMemberEventHint</c> values). The add-in maps the enum to its
/// names and asks this class whether an event is project content.
/// </summary>
internal static class WorkspaceEventHints
{
    /// <summary>
    /// Hints that describe UI or connection state rather than project content: a data source
    /// (re)connecting, or a Contents node expanding or collapsing. ArcGIS raises these
    /// asynchronously after <c>layer.add</c> and similar writes return.
    /// </summary>
    public static IReadOnlySet<string> NonContentHintNames { get; } =
        new HashSet<string>(["ConnectionStatus", "Expansion"], StringComparer.Ordinal);

    /// <summary>
    /// True when the event carries at least one hint and every hint is a non-content hint, so it
    /// must not advance the workspace revision. An event without hints is treated as content.
    /// </summary>
    public static bool IsNonContentOnly(IReadOnlyCollection<string> hintNames)
    {
        ArgumentNullException.ThrowIfNull(hintNames);
        return hintNames.Count > 0 && hintNames.All(NonContentHintNames.Contains);
    }

    /// <summary>
    /// Layout element hints (names of <c>ArcGIS.Desktop.Layouts.Events.ElementEventHint</c>) that
    /// describe map frame navigation or activation: view state, not layout content.
    /// </summary>
    public static IReadOnlySet<string> NonContentElementHintNames { get; } =
        new HashSet<string>(["SelectionChanged", "MapFrameNavigated", "MapFrameActivated", "MapFrameDeactivated"], StringComparer.Ordinal);

    /// <summary>
    /// True when a layout element event is an automatic consequence of a map change rather than a
    /// layout edit: view-state hints, or a property/placement change where every element is a map
    /// surround (legend, scale bar, north arrow), which ArcGIS redraws and resizes by itself when
    /// layers, symbology or frame extents change; or a property change of map frames only.
    /// Live acceptance showed these arriving up to ~1 s after symbology writes returned. Element
    /// kinds are <c>"surround"</c>, <c>"mapframe"</c> or anything else; an event without elements
    /// counts as content.
    /// </summary>
    public static bool IsAutomaticElementChange(string hintName, IReadOnlyCollection<string> elementKinds)
    {
        ArgumentNullException.ThrowIfNull(hintName);
        ArgumentNullException.ThrowIfNull(elementKinds);
        if (NonContentElementHintNames.Contains(hintName)) return true;
        if (elementKinds.Count == 0) return false;
        return hintName switch
        {
            "PropertyChanged" => elementKinds.All(static kind => kind is "surround" or "mapframe"),
            "PlacementChanged" => elementKinds.All(static kind => kind == "surround"),
            _ => false
        };
    }
}
