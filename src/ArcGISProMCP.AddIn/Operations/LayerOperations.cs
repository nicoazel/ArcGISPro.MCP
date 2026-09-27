using System.IO;
using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.AddIn.ArcGIS;
using ArcGISProMCP.AddIn.ArcGIS.Services;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Operations;

namespace ArcGISProMCP.AddIn.Operations;

internal sealed class LayerAddOperation() : ProOperationBase(OperationDescriptor.Create(
    "layer.add", "Add layer",
    "Adds a dataset, layer file, or service URL to a map and returns its stable layer handle.",
    LayerOperationSchemas.AddInput,
    risk: OperationRisk.SafeWrite, capabilities: ["maps"], tags: ["layer", "data", "service", "create"],
    aliases: ["add data", "load layer", "add service"], related: ["layer.list", "symbology.set-simple"], undoable: true))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var source = RequiredString(arguments, "source");
        var mapReference = OptionalString(arguments, "map");
        var name = OptionalString(arguments, "name");
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri))
            throw new ArgumentException("Layer source must be an absolute dataset path or service URL.", nameof(arguments));
        if (uri.IsFile) uri = new Uri(Path.GetFullPath(uri.LocalPath));

        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var map = ProHandles.ResolveMap(mapReference);
            var existing = string.IsNullOrWhiteSpace(name)
                ? null
                : map.GetLayersAsFlattenedList().FirstOrDefault(layer =>
                    string.Equals(layer.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                return new LayerMutationResult(ProHandles.ForLayer(existing), existing.Name, ProHandles.ForMap(map), source, false);
            var layer = LayerFactory.Instance.CreateLayer(uri, map, 0, name ?? string.Empty);
            return new LayerMutationResult(ProHandles.ForLayer(layer), layer.Name, ProHandles.ForMap(map), source, true);
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }

    private sealed record LayerMutationResult(string Id, string Name, string Map, string Source, bool Created);
}

internal sealed class LayerSetAppearanceOperation() : ProOperationBase(OperationDescriptor.Create(
    "layer.set-appearance", "Set layer appearance",
    "Sets visibility and/or transparency for one layer. Transparency is a percentage from 0 to 100.",
    LayerOperationSchemas.SetAppearanceInput,
    risk: OperationRisk.SafeWrite, capabilities: ["maps"], tags: ["layer", "visibility", "transparency", "appearance"],
    aliases: ["hide layer", "show layer", "fade layer"], related: ["layer.list", "symbology.set-simple"], undoable: true))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var layerReference = RequiredString(arguments, "layer");
        var mapReference = OptionalString(arguments, "map");
        var hasVisibility = arguments.TryGetProperty("visible", out var visibleElement) && visibleElement.ValueKind is JsonValueKind.True or JsonValueKind.False;
        var hasTransparency = arguments.TryGetProperty("transparency", out var transparencyElement) && transparencyElement.ValueKind == JsonValueKind.Number;
        if (!hasVisibility && !hasTransparency) throw new ArgumentException("Specify visible and/or transparency.", nameof(arguments));
        var visible = hasVisibility && visibleElement.GetBoolean();
        var transparency = hasTransparency ? transparencyElement.GetDouble() : 0;
        if (hasTransparency && (transparency < 0 || transparency > 100)) throw new ArgumentOutOfRangeException(nameof(arguments), "Transparency must be from 0 to 100.");

        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var map = ProHandles.ResolveMap(mapReference);
            var layer = ProHandles.ResolveLayer(map, layerReference);
            if (hasVisibility) layer.SetVisibility(visible);
            if (hasTransparency) layer.SetTransparency(transparency);
            return new { id = ProHandles.ForLayer(layer), layer.Name, layer.IsVisible, layer.Transparency };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }
}

internal sealed class LayerSetElevationOperation() : ProOperationBase(OperationDescriptor.Create(
    "layer.set-elevation", "Set layer elevation",
    "Sets how a feature layer is placed vertically in a scene, with optional cartographic offset and vertical exaggeration.",
    LayerOperationSchemas.SetElevationInput,
    risk: OperationRisk.SafeWrite, capabilities: ["maps"], tags: ["layer", "scene", "elevation", "ground", "3d"],
    aliases: ["put layer on ground", "set height mode", "place features relative to ground"],
    related: ["layer.list", "layer.set-appearance", "layout.set-frame-extent"], undoable: true))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(
        JsonElement arguments,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        var layerReference = RequiredString(arguments, "layer");
        var mapReference = OptionalString(arguments, "map");
        var mode = RequiredString(arguments, "mode").ToLowerInvariant() switch
        {
            "on-ground" => LayerElevationType.OnGround,
            "relative-to-ground" => LayerElevationType.RelativeToGround,
            "relative-to-scene" => LayerElevationType.RelativeToScene,
            "absolute-height" => LayerElevationType.AtAbsoluteHeight,
            _ => throw new ArgumentException("Unsupported elevation mode.", nameof(arguments))
        };
        var hasOffset = arguments.TryGetProperty("offset", out var offsetElement) && offsetElement.ValueKind == JsonValueKind.Number;
        var hasVerticalExaggeration = arguments.TryGetProperty("verticalExaggeration", out var exaggerationElement) && exaggerationElement.ValueKind == JsonValueKind.Number;
        var offset = hasOffset ? offsetElement.GetDouble() : 0d;
        var verticalExaggeration = hasVerticalExaggeration ? exaggerationElement.GetDouble() : 1d;
        if (!double.IsFinite(offset) || offset is < -1_000_000 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(arguments), "Offset must be finite and from -1,000,000 to 1,000,000.");
        if (!double.IsFinite(verticalExaggeration) || verticalExaggeration is < 0.01 or > 100)
            throw new ArgumentOutOfRangeException(nameof(arguments), "Vertical exaggeration must be finite and from 0.01 to 100.");

        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var map = ProHandles.ResolveMap(mapReference);
            if (map.MapType != MapType.Scene)
                throw new ArgumentException("Layer elevation placement requires a scene map.", nameof(arguments));
            var layer = ProHandles.ResolveLayer(map, layerReference) as FeatureLayer
                ?? throw new ArgumentException("Layer elevation placement requires a feature layer.", nameof(arguments));
            var definition = layer.GetElevationTypeDefinition();
            definition.ElevationType = mode;
            if (hasOffset) definition.CartographicOffset = offset;
            if (hasVerticalExaggeration) definition.VerticalExaggeration = verticalExaggeration;
            if (!layer.CanSetElevationTypeDefinition(definition))
                throw new InvalidOperationException($"Layer '{layer.Name}' cannot use elevation mode '{ProLayerService.ToMode(mode)}'.");
            layer.SetElevationTypeDefinition(definition);
            var actual = layer.GetElevationTypeDefinition();
            if (actual.ElevationType != mode)
                throw new InvalidOperationException($"Layer '{layer.Name}' did not accept the requested elevation mode.");
            return new
            {
                id = ProHandles.ForLayer(layer),
                layer.Name,
                map = ProHandles.ForMap(map),
                mode = ProLayerService.ToMode(actual.ElevationType),
                offset = actual.CartographicOffset,
                verticalExaggeration = actual.VerticalExaggeration
            };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }
}

internal sealed class BasemapSetOperation() : ProOperationBase(OperationDescriptor.Create(
    "basemap.set", "Set basemap",
    "Sets a map's basemap using an ArcGIS Pro basemap name.",
    LayerOperationSchemas.BasemapSetInput,
    risk: OperationRisk.SafeWrite, capabilities: ["maps"], tags: ["basemap", "map", "style"],
    aliases: ["change basemap", "set imagery", "set topo"], related: ["map.list", "map.ensure"], undoable: true))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var mapReference = OptionalString(arguments, "map");
        var basemap = ProMapService.ParseBasemap(RequiredString(arguments, "basemap"));
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var map = ProHandles.ResolveMap(mapReference);
            map.SetBasemapLayers(basemap);
            return new { map = ProHandles.ForMap(map), map.Name, basemap = basemap.ToString() };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }
}

internal sealed class StyleSearchOperation() : ProOperationBase(OperationDescriptor.Create(
    "style.search", "Search project styles",
    "Searches symbols in referenced project styles without loading the full style catalog into model context.",
    LayerOperationSchemas.StyleSearchInput,
    capabilities: ["maps"], tags: ["style", "symbol", "search"], aliases: ["find symbol", "search symbols"],
    related: ["symbology.set-simple"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var query = OptionalString(arguments, "query") ?? string.Empty;
        var type = (OptionalString(arguments, "type") ?? "point").ToLowerInvariant() switch
        {
            "line" => StyleItemType.LineSymbol,
            "polygon" => StyleItemType.PolygonSymbol,
            "text" => StyleItemType.TextSymbol,
            _ => StyleItemType.PointSymbol
        };
        var limit = arguments.TryGetProperty("limit", out var limitElement) && limitElement.TryGetInt32(out var requestedLimit)
            ? Math.Clamp(requestedLimit, 1, 100) : 30;
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var styles = Project.Current?.GetItems<StyleProjectItem>() ?? [];
            return styles.SelectMany(style => StyleHelper.SearchSymbols(style, type, query)
                    .Select(item => new { style = style.Name, item.Name, item.Category, item.Tags, type = type.ToString() }))
                .Take(limit)
                .ToArray();
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }
}
