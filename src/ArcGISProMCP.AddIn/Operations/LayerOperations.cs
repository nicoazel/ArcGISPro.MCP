using System.IO;
using System.Text.Json;
using ArcGIS.Core;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Data;
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

        var (data, notice) = await context.Dispatcher.OnMainCimThreadAsync(
            () => Ensure(ProHandles.ResolveMap(mapReference), uri, source, name),
            cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision, notice is null ? null : [notice]);
    }

    /// <summary>
    /// Adds the layer, or resolves a same-named layer by the rules in <see cref="LayerSources.DecideExisting"/>:
    /// a healthy layer is reused when it reads the requested data (or ArcGIS reports no path to compare),
    /// and is otherwise only ever repaired in place. Only a layer whose data connection is broken may be
    /// removed and re-added, as a last resort when an in-place repair is not possible.
    /// </summary>
    private static (LayerMutationResult Result, OperationNotice? Notice) Ensure(Map map, Uri uri, string requestedSource, string? name)
    {
        var existing = string.IsNullOrWhiteSpace(name)
            ? null
            : map.GetLayersAsFlattenedList().FirstOrDefault(layer =>
                string.Equals(layer.Name, name, StringComparison.OrdinalIgnoreCase));
        var requestIsLayerFile = IsLayerFile(uri);
        if (existing is null)
        {
            var created = LayerFactory.Instance.CreateLayer(uri, map, 0, name ?? string.Empty)
                ?? throw new InvalidOperationException($"ArcGIS could not create a layer from '{requestedSource}'.");
            return (Result(created, map, uri, requestedSource, requestIsLayerFile, created: true), null);
        }

        if (existing is ILayerContainer)
        {
            // Never delete a group layer to satisfy a same-named data request.
            if (!requestIsLayerFile)
            {
                throw new InvalidOperationException(
                    $"Layer '{existing.Name}' in map '{map.Name}' is a group layer, not a data layer; choose a different layer name.");
            }
            return (Result(existing, map, uri, requestedSource, requestIsLayerFile, created: false), null);
        }

        var broken = LayerData.IsBroken(existing);
        var actual = LayerData.TryGetPath(existing);
        var action = LayerSources.DecideExisting(broken, requestIsLayerFile, actual, uri, () => SameDataset(existing, uri));
        var previous = LayerData.Display(actual);
        switch (action)
        {
            case ExistingLayerAction.Reuse:
                return (Result(existing, map, uri, requestedSource, requestIsLayerFile, created: false), null);

            case ExistingLayerAction.ReuseUnverified:
                return (Result(existing, map, uri, requestedSource, requestIsLayerFile, created: false), new OperationNotice(
                    "source_unverified",
                    $"Layer '{existing.Name}' already existed and its data source is healthy, but ArcGIS reports no dataset path for it, " +
                    $"so it could not be confirmed to read '{requestedSource}'. It was reused unchanged; use a different name to add the requested source as a new layer.",
                    "warning"));

            case ExistingLayerAction.RepairInPlaceOnly:
            {
                var reason = $"it read '{previous}' instead of the requested source";
                if (TryReplaceDataSource(existing, uri))
                    return Repaired(existing, map, uri, requestedSource, requestIsLayerFile, reason);
                // A healthy layer is never removed: it may carry symbology, labels or joins the caller wants.
                return (Result(existing, map, uri, requestedSource, requestIsLayerFile, created: false), new OperationNotice(
                    "layer_source_mismatch",
                    $"Layer '{existing.Name}' already existed with a healthy data source '{previous}', not the requested '{requestedSource}'. " +
                    "ArcGIS could not swap its data source in place, so it was left unchanged (DataSourceStatus 'mismatch'); " +
                    "use a different name, or remove the layer first, to add the requested source.",
                    "warning"));
            }

            default:
            {
                var reason = previous is null ? "its data source was broken" : $"its data source '{previous}' was broken";
                if (!requestIsLayerFile && TryReplaceDataSource(existing, uri))
                    return Repaired(existing, map, uri, requestedSource, requestIsLayerFile, reason);

                // Last resort, and only for a broken layer: nothing usable is lost by replacing it.
                var replacement = Replace(map, existing, uri, requestedSource);
                return (Result(replacement, map, uri, requestedSource, requestIsLayerFile, created: true, replaced: true), new OperationNotice(
                    "layer_repaired",
                    $"Layer '{replacement.Name}' already existed but {reason}; it was removed and re-added from '{requestedSource}' at the same position. " +
                    "Its previous symbology and layer properties were not kept, so reapply them.",
                    "info"));
            }
        }
    }

    private static (LayerMutationResult Result, OperationNotice? Notice) Repaired(
        Layer layer, Map map, Uri uri, string requestedSource, bool requestIsLayerFile, string reason)
    {
        var result = Result(layer, map, uri, requestedSource, requestIsLayerFile, created: false, repaired: true);
        var message = $"Layer '{layer.Name}' already existed but {reason}; its data source was replaced and its symbology was kept.";
        // Report what the layer reads after the swap rather than assuming it is the request.
        var ok = string.Equals(result.DataSourceStatus, "ok", StringComparison.Ordinal);
        if (!ok)
            message += $" ArcGIS now reports its source as '{result.Source ?? "unknown"}' (DataSourceStatus '{result.DataSourceStatus}').";
        return (result, new OperationNotice("layer_repaired", message, ok ? "info" : "warning"));
    }

    /// <summary>Whether the layer's opened feature class is the requested local dataset (workspace plus name).</summary>
    private static bool SameDataset(Layer layer, Uri uri)
    {
        if (!uri.IsFile || !LayerSources.TrySplitLocalDataset(uri.LocalPath, out var workspace, out var dataset, out _)) return false;
        var (actualWorkspace, actualName) = LayerData.TryGetDatasetLocation(layer);
        return LayerSources.SameDataset(actualWorkspace, actualName, workspace, dataset);
    }

    /// <summary>
    /// Swaps the dataset of a feature layer in place, keeping its symbology. True when ArcGIS accepted
    /// the swap and the layer's connection is not broken afterwards; whether it now reads the requested
    /// path is reported separately in <c>Source</c> and <c>DataSourceStatus</c>.
    /// </summary>
    private static bool TryReplaceDataSource(Layer layer, Uri uri)
    {
        if (layer is not FeatureLayer || !uri.IsFile) return false;
        bool swapped;
        try
        {
            swapped = WithLocalFeatureClass(uri.LocalPath, dataset =>
            {
                if (!layer.CanReplaceDataSource(dataset)) return false;
                layer.ReplaceDataSource(dataset);
                return true;
            });
        }
        catch (Exception exception) when (exception is not (CalledOnWrongThreadException or OperationCanceledException))
        {
            // Opening or swapping failed; only a broken layer falls back to removing and re-adding it.
            return false;
        }
        return swapped && !LayerData.IsBroken(layer);
    }

    /// <summary>
    /// Opens a shapefile or file-geodatabase feature class and runs <paramref name="use"/> while its
    /// datastore is still open (the datastore must outlive the swap); false for other sources.
    /// </summary>
    private static bool WithLocalFeatureClass(string path, Func<FeatureClass, bool> use)
    {
        if (!LayerSources.TrySplitLocalDataset(path, out var workspace, out var datasetName, out var isShapefile)) return false;
        if (isShapefile)
        {
            using var shapefiles = new FileSystemDatastore(new FileSystemConnectionPath(new Uri(workspace), FileSystemDatastoreType.Shapefile));
            using var shapefile = shapefiles.OpenDataset<FeatureClass>(datasetName);
            return use(shapefile);
        }

        using var geodatabase = new Geodatabase(new FileGeodatabaseConnectionPath(new Uri(workspace)));
        using var featureClass = geodatabase.OpenDataset<FeatureClass>(datasetName);
        return use(featureClass);
    }

    /// <summary>Adds a new layer at the old layer's position in the same container, then removes the old one.</summary>
    private static Layer Replace(Map map, Layer existing, Uri uri, string requestedSource)
    {
        var container = existing.Parent as ILayerContainerEdit ?? map;
        var index = Math.Max((existing.Parent as ILayerContainer)?.Layers.IndexOf(existing) ?? 0, 0);
        var layerName = existing.Name;
        var visible = existing.IsVisible;
        // Create first: if ArcGIS rejects the source, the existing layer is left untouched.
        var created = LayerFactory.Instance.CreateLayer(uri, container, index, layerName)
            ?? throw new InvalidOperationException($"ArcGIS could not create a layer from '{requestedSource}'; layer '{layerName}' was left unchanged.");
        created.SetVisibility(visible);
        container.RemoveLayer(existing);
        return created;
    }

    private static LayerMutationResult Result(
        Layer layer, Map map, Uri uri, string requestedSource, bool requestIsLayerFile, bool created, bool repaired = false, bool replaced = false)
    {
        var actual = LayerData.TryGetPath(layer);
        return new LayerMutationResult(
            ProHandles.ForLayer(layer),
            layer.Name,
            ProHandles.ForMap(map),
            // Report what the layer actually reads. A freshly created layer reads the requested
            // source even when ArcGIS reports no dataset path for it (for example a layer file).
            LayerData.Display(actual) ?? (created ? requestedSource : null),
            requestedSource,
            created,
            repaired,
            replaced,
            LayerSources.DataSourceStatus(
                LayerData.IsBroken(layer), requestIsLayerFile, created, actual, uri, () => SameDataset(layer, uri)));
    }

    private static bool IsLayerFile(Uri uri) =>
        uri.IsFile && Path.GetExtension(uri.LocalPath).ToUpperInvariant() is ".LYRX" or ".LYR" or ".LPKX" or ".LPK";

    /// <param name="Source">The source the layer actually reads; null when ArcGIS reports none for a reused layer.</param>
    /// <param name="Created">A new layer object was added (a new handle).</param>
    /// <param name="Repaired">An existing layer's data source was swapped in place; its symbology was kept.</param>
    /// <param name="Replaced">An existing broken layer was removed and re-added at the same position.</param>
    /// <param name="DataSourceStatus">
    /// <c>ok</c>, <c>broken</c>, <c>unverified</c> (ArcGIS reports no dataset path to compare) or
    /// <c>mismatch</c> (the layer reads something other than the request).
    /// </param>
    private sealed record LayerMutationResult(
        string Id,
        string Name,
        string Map,
        string? Source,
        string RequestedSource,
        bool Created,
        bool Repaired,
        bool Replaced,
        string DataSourceStatus);
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
                throw OperationException.InvalidArgument($"Map '{map.Name}' is not a scene; layer elevation placement requires a scene map.");
            var layer = ProHandles.ResolveLayer(map, layerReference) as FeatureLayer
                ?? throw OperationException.InvalidArgument($"Layer '{layerReference}' is not a feature layer; layer elevation placement requires one.");
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
