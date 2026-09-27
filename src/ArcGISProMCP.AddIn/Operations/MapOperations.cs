using System.Text.Json;
using ArcGIS.Core.CIM;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.AddIn.ArcGIS;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.AddIn.Operations;

internal sealed class MapListOperation() : ProOperationBase(OperationDescriptor.Create(
    "map.list", "List maps",
    "Lists every map and scene in the current project with stable handles, view state, type, and layer counts.",
    JsonSchemas.EmptyObject,
    tags: ["map", "project", "browse"], aliases: ["maps", "scenes", "open maps"],
    examples: ["List all maps before choosing which one to activate."],
    related: ["map.activate", "map.ensure", "layer.list"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var activeUri = MapView.Active?.Map?.URI;
            return (Project.Current?.GetItems<MapProjectItem>() ?? [])
                .Select(item => item.GetMap())
                .Select(map => new
                {
                    id = ProHandles.ForMap(map),
                    map.Name,
                    type = map.MapType.ToString(),
                    layerCount = map.GetLayersAsFlattenedList().Count,
                    isActive = string.Equals(map.URI, activeUri, StringComparison.OrdinalIgnoreCase)
                })
                .OrderBy(map => map.Name, StringComparer.Ordinal)
                .ToArray();
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }
}

internal sealed class MapEnsureOperation() : ProOperationBase(OperationDescriptor.Create(
    "map.ensure", "Ensure map exists",
    "Returns an existing named map or creates it with the requested 2D/3D type and basemap.",
    JsonSchemas.ObjectSchema(
        "\"name\": {\"type\": \"string\", \"minLength\": 1}, \"type\": {\"type\": \"string\", \"enum\": [\"map\", \"scene\", \"global-scene\"]}, \"basemap\": {\"type\": \"string\"}",
        "name"),
    risk: OperationRisk.SafeWrite, capabilities: ["maps"], tags: ["map", "create", "scene"],
    aliases: ["new map", "create map", "make map"],
    examples: ["Ensure maps named Zoning, Transit, and Buildings exist."],
    related: ["map.activate", "basemap.set", "layer.add"], undoable: true))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var name = RequiredString(arguments, "name");
        var type = OptionalString(arguments, "type") ?? "map";
        var basemap = ParseBasemap(OptionalString(arguments, "basemap") ?? nameof(Basemap.None));
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var existing = (Project.Current?.GetItems<MapProjectItem>() ?? [])
                .Select(item => item.GetMap())
                .FirstOrDefault(map => string.Equals(map.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                return new MapMutationResult(ProHandles.ForMap(existing), existing.Name, false);

            var mapType = type.Contains("scene", StringComparison.OrdinalIgnoreCase) ? MapType.Scene : MapType.Map;
            var viewingMode = string.Equals(type, "global-scene", StringComparison.OrdinalIgnoreCase)
                ? MapViewingMode.SceneGlobal
                : mapType == MapType.Scene ? MapViewingMode.SceneLocal : MapViewingMode.Map;
            var created = MapFactory.Instance.CreateMap(name, mapType, viewingMode, basemap);
            return new MapMutationResult(ProHandles.ForMap(created), created.Name, true);
        }, cancellationToken).ConfigureAwait(false);
        if (data.Created)
        {
            // ProjectItemsChanged can arrive well after CreateMap returns. Give that
            // one-time structural notification a chance to publish before the base
            // operation samples the revision handed to the next workflow step.
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }

    internal static Basemap ParseBasemap(string value)
    {
        var normalized = new string(value.Where(char.IsLetterOrDigit).ToArray());
        if (string.Equals(normalized, "imagery", StringComparison.OrdinalIgnoreCase))
            normalized = nameof(Basemap.Satellite);
        if (Enum.TryParse<Basemap>(normalized, true, out var basemap)) return basemap;
        throw new ArgumentException($"Unknown basemap '{value}'. Use a Pro basemap name such as None, Topographic, Streets, Imagery, or OpenStreetMap.");
    }

    private sealed record MapMutationResult(string Id, string Name, bool Created);
}

internal sealed class MapActivateOperation() : ProOperationBase(OperationDescriptor.Create(
    "map.activate", "Activate map",
    "Opens or activates a map view for a stable map handle or unambiguous name.",
    JsonSchemas.ObjectSchema("\"map\": {\"type\": \"string\", \"minLength\": 1}", "map"),
    risk: OperationRisk.SafeWrite, executionTarget: ExecutionTarget.ArcGISUiThread,
    capabilities: ["maps"], tags: ["map", "view", "open", "switch"],
    aliases: ["switch map", "open map", "show map"], examples: ["Activate the Transit map."],
    related: ["map.list", "view.capture"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var mapReference = RequiredString(arguments, "map");
        var map = await context.Dispatcher.OnMainCimThreadAsync(
            () => ProHandles.ResolveMap(mapReference), cancellationToken).ConfigureAwait(false);
        await context.Dispatcher.OnUiThreadAsync(async () =>
        {
            var existing = global::ArcGIS.Desktop.Framework.FrameworkApplication.Panes
                .OfType<IMapPane>()
                .FirstOrDefault(pane => string.Equals(pane.MapView.Map.URI, map.URI, StringComparison.OrdinalIgnoreCase));
            if (existing is global::ArcGIS.Desktop.Framework.Contracts.Pane pane)
                pane.Activate();
            else
                await map.OpenViewAsync().ConfigureAwait(true);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(new { id = ProHandles.ForMap(map), map.Name }), snapshot.Revision);
    }
}
