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
}
