namespace ArcGISProMCP.Operations.Services;

// Service contracts between host-neutral operations and the ArcGIS Pro SDK.
//
// Threading: services never marshal. An operation calls a service member from inside the
// context.Dispatcher callback that the ArcGIS SDK requires (OnMainCimThreadAsync for most
// members, OnUiThreadAsync where documented), so the operation keeps ownership of thread
// affinity and a fake service runs inline in tests.
//
// Errors: argument validation stays in the operations. Services throw
// InvalidOperationException for host-state failures (no project, map or layer not found,
// SDK rejected an edit) with the messages the operations surfaced before the seam.

/// <summary>Every ArcGIS-backed service the host-neutral operations need.</summary>
internal sealed record ArcGisServices(
    IProjectService Project,
    IMapService Maps,
    ILayerService Layers,
    IViewCaptureService Views,
    IFeatureService Features,
    IGeoprocessingService Geoprocessing);

/// <summary>Project lifecycle. Every member runs on the ArcGIS UI thread.</summary>
internal interface IProjectService
{
    /// <summary>True when the open project has unsaved feature (data) edits; false with no project.</summary>
    bool HasEdits { get; }

    /// <summary>Opens an existing .aprx, replacing the current project.</summary>
    Task OpenAsync(string path);

    /// <summary>Saves every pending feature edit; returns false when ArcGIS reports the save failed.</summary>
    Task<bool> SaveEditsAsync();

    /// <summary>Saves the current project; throws when no project is open.</summary>
    Task SaveAsync();
}

/// <summary>A map resolved in the current project.</summary>
/// <param name="Id">Stable handle (pro://map/...).</param>
/// <param name="Name">Map name.</param>
/// <param name="HostMap">The host's map object, opaque to operations; handed back to the service.</param>
internal sealed record ResolvedMap(string Id, string Name, object HostMap);

/// <summary>One project map as listed by map.list.</summary>
internal sealed record MapSummary(string Id, string Name, string Type, int LayerCount, bool IsActive);

/// <summary>The view a new map is created with.</summary>
internal enum MapViewKind
{
    Map,
    LocalScene,
    GlobalScene
}

/// <summary>Maps in the current project. Members run on the main CIM thread unless noted.</summary>
internal interface IMapService
{
    /// <summary>Every project map in project order.</summary>
    IReadOnlyList<MapSummary> List();

    /// <summary>
    /// Resolves a map handle or name (case-insensitive); null or blank means the active map, else
    /// the first project map. Throws when nothing matches.
    /// </summary>
    ResolvedMap Resolve(string? handleOrName);

    /// <summary>First project map whose name matches case-insensitively, or null.</summary>
    ResolvedMap? FindByName(string name);

    /// <summary>
    /// Maps a normalized basemap name (letters and digits only) to the host's canonical basemap
    /// name. False when the host does not know it.
    /// </summary>
    bool TryGetBasemap(string normalizedName, out string canonicalName);

    /// <summary>Creates a map with a canonical basemap name from <see cref="TryGetBasemap"/>.</summary>
    ResolvedMap Create(string name, MapViewKind kind, string basemap);

    /// <summary>Activates an open view of the map or opens a new one. Runs on the UI thread.</summary>
    Task ActivateViewAsync(ResolvedMap map);

    /// <summary>Clears the feature selection of a resolved map.</summary>
    void ClearSelection(ResolvedMap map);
}

/// <summary>Elevation placement of a feature layer as reported by layer.list.</summary>
internal sealed record LayerElevation(string Mode, double? Offset, double? VerticalExaggeration);

/// <summary>One layer of a flattened map layer tree, in drawing order.</summary>
internal sealed record LayerSummary(
    string Id,
    string Name,
    string Type,
    bool IsVisible,
    double Transparency,
    bool IsFeatureLayer,
    LayerElevation? Elevation);

/// <summary>Layers of a map. Runs on the main CIM thread.</summary>
internal interface ILayerService
{
    /// <summary>The flattened layer list of a resolved map, in drawing order.</summary>
    IReadOnlyList<LayerSummary> List(ResolvedMap map);
}

/// <summary>A PNG captured from the active map view.</summary>
internal sealed record MapViewCapture(
    byte[] Png,
    int Width,
    int Height,
    string MapName,
    string MapUri);

/// <summary>The layout a PNG export was written from.</summary>
internal sealed record LayoutExport(string Name, string Uri);

/// <summary>Visual evidence capture.</summary>
internal interface IViewCaptureService
{
    /// <summary>Captures the active map view as PNG. Runs on the UI thread.</summary>
    MapViewCapture CaptureActiveMap(int width, int height);

    /// <summary>
    /// Exports a layout (null means the active layout) to a PNG file at the requested pixel size.
    /// Runs on the main CIM thread.
    /// </summary>
    LayoutExport ExportLayout(string? layoutReference, int width, int height, string outputPath);

    /// <summary>Rescales PNG bytes to exactly the requested pixel size. Thread-agnostic.</summary>
    byte[] ResizePng(byte[] png, int width, int height);
}

/// <summary>Geoprocessing history and map flags; the tool always runs on the GP thread.</summary>
internal sealed record GeoprocessingExecutionFlags(bool AddOutputsToMap, bool AddToHistory, bool RefreshProjectItems)
{
    /// <summary>GP thread only: nothing added to the map, history or project items.</summary>
    public static GeoprocessingExecutionFlags None { get; } = new(false, false, false);
}

/// <summary>One geoprocessing message.</summary>
/// <param name="Type">The ArcGIS message type name (Informative, Warning, Error, ...).</param>
internal sealed record GeoprocessingMessage(string Type, string Text, int ErrorCode);

/// <summary>The complete result of one geoprocessing tool execution.</summary>
/// <param name="Values">Result values; null when ArcGIS reports none.</param>
/// <param name="ValueTypes">Result value types; null when ArcGIS reports none.</param>
/// <param name="FirstErrorMessage">The first error message text, if any.</param>
internal sealed record GeoprocessingExecutionResult(
    bool IsFailed,
    bool IsCanceled,
    int ErrorCode,
    string? ReturnValue,
    IReadOnlyList<string>? Values,
    IReadOnlyList<string>? ValueTypes,
    IReadOnlyList<GeoprocessingMessage> Messages,
    string? FirstErrorMessage);

/// <summary>Runs geoprocessing tools. Thread-agnostic: ArcGIS schedules the tool itself.</summary>
internal interface IGeoprocessingService
{
    Task<GeoprocessingExecutionResult> ExecuteAsync(
        string tool,
        IReadOnlyList<string> parameters,
        IReadOnlyList<KeyValuePair<string, string>> environments,
        GeoprocessingExecutionFlags flags,
        CancellationToken cancellationToken);
}

/// <summary>ArcGIS geoprocessing message type names the operations act on.</summary>
internal static class GeoprocessingMessageTypes
{
    public const string Warning = "Warning";
}
