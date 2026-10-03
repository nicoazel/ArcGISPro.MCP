using System.Text;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Operations.Services;

namespace ArcGISProMCP.Testing;

internal sealed class FakeProjectService(FakeProState state) : IProjectService
{
    public bool HasEdits
    {
        get
        {
            FakeDispatcher.Require(FakeThread.Ui, nameof(HasEdits));
            return state.IsOpen && state.HasEdits;
        }
    }

    public Task OpenAsync(string path)
    {
        FakeDispatcher.Require(FakeThread.Ui, nameof(OpenAsync));
        // ArcGIS Pro would block on its modal "Save all edits?" prompt here; the operation must refuse first.
        // Unsaved project (.aprx) changes alone were not observed to prompt, so they do not block.
        if (state.HasEdits)
            throw new InvalidOperationException("ArcGIS Pro would prompt to save pending edits before opening another project.");
        state.Calls.Add($"project.open {path}");
        state.ProjectUri = path;
        state.ProjectName = Path.GetFileNameWithoutExtension(path);
        state.IsDirty = false;
        return Task.CompletedTask;
    }

    public Task<bool> SaveEditsAsync()
    {
        FakeDispatcher.Require(FakeThread.Ui, nameof(SaveEditsAsync));
        if (!state.IsOpen) throw new InvalidOperationException("No ArcGIS Pro project is open.");
        state.Calls.Add("project.save-edits");
        if (state.FailEditSave) return Task.FromResult(false);
        state.HasEdits = false;
        return Task.FromResult(true);
    }

    public Task SaveAsync()
    {
        FakeDispatcher.Require(FakeThread.Ui, nameof(SaveAsync));
        if (!state.IsOpen) throw new InvalidOperationException("No ArcGIS Pro project is open.");
        state.Calls.Add("project.save");
        if (state.TrackDirty) state.IsDirty = false;
        return Task.CompletedTask;
    }
}

internal sealed class FakeMapService(FakeProState state) : IMapService
{
    /// <summary>A few of ArcGIS Pro's basemap names; enough to exercise normalization.</summary>
    public static readonly IReadOnlyList<string> KnownBasemaps = ["None", "Topographic", "Streets", "Satellite", "OpenStreetMap", "Hybrid"];

    public IReadOnlyList<MapSummary> List()
    {
        FakeDispatcher.Require(FakeThread.MainCim, nameof(List));
        return state.Maps.Select(map => new MapSummary(
                FakeProState.MapHandle(map),
                map.Name,
                map.Type,
                map.Layers.Count,
                string.Equals(map.Name, state.ActiveMapName, StringComparison.Ordinal)))
            .ToArray();
    }

    public ResolvedMap Resolve(string? handleOrName)
    {
        FakeDispatcher.Require(FakeThread.MainCim, nameof(Resolve));
        return ToResolved(ResolveMap(handleOrName));
    }

    public ResolvedMap? FindByName(string name)
    {
        FakeDispatcher.Require(FakeThread.MainCim, nameof(FindByName));
        var map = state.Maps.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        return map is null ? null : ToResolved(map);
    }

    public bool TryGetBasemap(string normalizedName, out string canonicalName)
    {
        canonicalName = KnownBasemaps.FirstOrDefault(name => string.Equals(name, normalizedName, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        return canonicalName.Length > 0;
    }

    public ResolvedMap Create(string name, MapViewKind kind, string basemap)
    {
        FakeDispatcher.Require(FakeThread.MainCim, nameof(Create));
        var map = state.AddMap(name, kind == MapViewKind.Map ? "Map" : "Scene");
        map.CreatedAs = kind;
        map.Basemap = basemap;
        state.Calls.Add($"map.create {name} {kind} {basemap}");
        return ToResolved(map);
    }

    public Task ActivateViewAsync(ResolvedMap map)
    {
        FakeDispatcher.Require(FakeThread.Ui, nameof(ActivateViewAsync));
        var fake = Host(map);
        state.Calls.Add(fake.HasOpenView ? $"map.activate-pane {fake.Name}" : $"map.open-view {fake.Name}");
        fake.HasOpenView = true;
        state.ActiveMapName = fake.Name;
        return Task.CompletedTask;
    }

    public void ClearSelection(ResolvedMap map)
    {
        FakeDispatcher.Require(FakeThread.MainCim, nameof(ClearSelection));
        var fake = Host(map);
        state.Calls.Add($"map.clear-selection {fake.Name}");
        fake.SelectionCount = 0;
    }

    public static FakeMap Host(ResolvedMap map) =>
        map.HostMap as FakeMap ?? throw new ArgumentException("The resolved map is not a fake map.", nameof(map));

    private FakeMap ResolveMap(string? handleOrName)
    {
        if (string.IsNullOrWhiteSpace(handleOrName))
        {
            return state.Maps.FirstOrDefault(map => string.Equals(map.Name, state.ActiveMapName, StringComparison.Ordinal))
                ?? state.Maps.FirstOrDefault()
                ?? throw OperationException.MapNotFound(null);
        }

        const string prefix = "pro://map/";
        var value = handleOrName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? Uri.UnescapeDataString(handleOrName[prefix.Length..])
            : handleOrName;
        return state.Maps.FirstOrDefault(map =>
                   string.Equals(map.Uri, value, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(map.Name, value, StringComparison.OrdinalIgnoreCase))
               ?? throw OperationException.MapNotFound(handleOrName);
    }

    private static ResolvedMap ToResolved(FakeMap map) => new(FakeProState.MapHandle(map), map.Name, map);
}

internal sealed class FakeLayerService : ILayerService
{
    public IReadOnlyList<LayerSummary> List(ResolvedMap map)
    {
        FakeDispatcher.Require(FakeThread.MainCim, nameof(List));
        return FakeMapService.Host(map).Layers
            .Select(layer => new LayerSummary(
                FakeProState.LayerHandle(layer),
                layer.Name,
                layer.Type,
                layer.IsVisible,
                layer.Transparency,
                layer.IsFeatureLayer,
                layer.IsFeatureLayer ? layer.Elevation : null))
            .ToArray();
    }
}

/// <summary>
/// Produces marker "PNG" bytes. Layout exports are written larger than requested, the way a
/// rounded-up export resolution overshoots, so tests can see the resize step.
/// </summary>
internal sealed class FakeViewCaptureService(FakeProState state) : IViewCaptureService
{
    public const int LayoutOvershootPixels = 7;

    public List<string> ExportedPaths { get; } = [];

    public MapViewCapture CaptureActiveMap(int width, int height)
    {
        FakeDispatcher.Require(FakeThread.Ui, nameof(CaptureActiveMap));
        var map = state.Maps.FirstOrDefault(candidate => string.Equals(candidate.Name, state.ActiveMapName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("An active map view is required for capture.");
        state.Calls.Add($"view.capture-map {map.Name} {width}x{height}");
        return new MapViewCapture(Png($"map:{map.Name}", width, height), width, height, map.Name, map.Uri);
    }

    public LayoutExport ExportLayout(string? layoutReference, int width, int height, string outputPath)
    {
        FakeDispatcher.Require(FakeThread.MainCim, nameof(ExportLayout));
        var layout = string.IsNullOrWhiteSpace(layoutReference)
            ? state.Layouts.FirstOrDefault(candidate => string.Equals(candidate.Name, state.ActiveLayoutName, StringComparison.Ordinal))
              ?? throw new InvalidOperationException("Specify a layout or activate one before capture.")
            : state.Layouts.FirstOrDefault(candidate =>
                  string.Equals(candidate.Name, layoutReference, StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(FakeProState.LayoutHandle(candidate), layoutReference, StringComparison.OrdinalIgnoreCase))
              ?? throw OperationException.LayoutNotFound(layoutReference);
        ExportedPaths.Add(outputPath);
        File.WriteAllBytes(outputPath, Png($"layout:{layout.Name}", width + LayoutOvershootPixels, height + LayoutOvershootPixels));
        state.Calls.Add($"view.export-layout {layout.Name} {width}x{height}");
        return new LayoutExport(layout.Name, layout.Uri);
    }

    public byte[] ResizePng(byte[] png, int width, int height)
    {
        state.Calls.Add($"view.resize {width}x{height}");
        var source = Encoding.UTF8.GetString(png);
        var content = source[..source.LastIndexOf('@')];
        return Png(content, width, height);
    }

    /// <summary>Marker bytes "content@WxH".</summary>
    public static byte[] Png(string content, int width, int height) => Encoding.UTF8.GetBytes($"{content}@{width}x{height}");
}
