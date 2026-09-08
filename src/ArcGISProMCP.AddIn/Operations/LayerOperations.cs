using System.Text.Json;
using System.IO;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.AddIn.ArcGIS;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.AddIn.Operations;

internal sealed class LayerListOperation() : ProOperationBase(OperationDescriptor.Create(
    "layer.list", "List layers",
    "Lists the flattened layer tree for a map with stable handles and appearance state.",
    JsonSchemas.ObjectSchema("\"map\": {\"type\": \"string\"}"),
    capabilities: ["maps"], tags: ["layer", "map", "browse"], aliases: ["table of contents", "toc"],
    related: ["layer.add", "layer.set-appearance", "symbology.set-simple"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var mapReference = OptionalString(arguments, "map");
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var map = ProHandles.ResolveMap(mapReference);
            return new
            {
                map = ProHandles.ForMap(map),
                layers = map.GetLayersAsFlattenedList().Select((layer, index) => new
                {
                    id = ProHandles.ForLayer(layer), layer.Name, type = layer.GetType().Name,
                    layer.IsVisible, layer.Transparency, drawingOrder = index,
                    isFeatureLayer = layer is FeatureLayer
                }).ToArray()
            };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }
}

internal sealed class LayerAddOperation() : ProOperationBase(OperationDescriptor.Create(
    "layer.add", "Add layer",
    "Adds a dataset, layer file, or service URL to a map and returns its stable layer handle.",
    JsonSchemas.ObjectSchema(
        "\"source\": {\"type\": \"string\", \"minLength\": 1}, \"map\": {\"type\": \"string\"}, \"name\": {\"type\": \"string\"}",
        "source"),
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
    JsonSchemas.ObjectSchema(
        "\"layer\": {\"type\": \"string\", \"minLength\": 1}, \"map\": {\"type\": \"string\"}, \"visible\": {\"type\": \"boolean\"}, \"transparency\": {\"type\": \"number\", \"minimum\": 0, \"maximum\": 100}",
        "layer"),
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

internal sealed class BasemapSetOperation() : ProOperationBase(OperationDescriptor.Create(
    "basemap.set", "Set basemap",
    "Sets a map's basemap using an ArcGIS Pro basemap name.",
    JsonSchemas.ObjectSchema("\"basemap\": {\"type\": \"string\", \"minLength\": 1}, \"map\": {\"type\": \"string\"}", "basemap"),
    risk: OperationRisk.SafeWrite, capabilities: ["maps"], tags: ["basemap", "map", "style"],
    aliases: ["change basemap", "set imagery", "set topo"], related: ["map.list", "map.ensure"], undoable: true))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var mapReference = OptionalString(arguments, "map");
        var basemap = MapEnsureOperation.ParseBasemap(RequiredString(arguments, "basemap"));
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
    JsonSchemas.ObjectSchema(
        "\"query\": {\"type\": \"string\"}, \"type\": {\"type\": \"string\", \"enum\": [\"point\", \"line\", \"polygon\", \"text\"]}, \"limit\": {\"type\": \"integer\", \"minimum\": 1, \"maximum\": 100}"),
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
