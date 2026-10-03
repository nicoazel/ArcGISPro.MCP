using System.Text.Json;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Operations.Services;

namespace ArcGISProMCP.Operations;

internal sealed class MapListOperation(IMapService maps) : ProOperationBase(OperationDescriptor.Create(
    "map.list", "List maps",
    "Lists every map and scene in the current project with stable handles, view state, type, and layer counts.",
    JsonSchemas.EmptyObject,
    outputSchema: MapOperationSchemas.ListOutput,
    tags: ["map", "project", "browse"], aliases: ["maps", "scenes", "open maps"],
    examples: ["List all maps before choosing which one to activate."],
    related: ["map.activate", "map.ensure", "layer.list"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
            maps.List()
                .Select(map => new
                {
                    id = map.Id,
                    map.Name,
                    type = map.Type,
                    layerCount = map.LayerCount,
                    isActive = map.IsActive
                })
                .OrderBy(map => map.Name, StringComparer.Ordinal)
                .ToArray(), cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }
}

internal sealed class MapEnsureOperation(IMapService maps, TimeSpan? structuralSettleDelay = null) : ProOperationBase(OperationDescriptor.Create(
    "map.ensure", "Ensure map exists",
    "Returns an existing named map or creates it with the requested 2D/3D type and basemap.",
    MapOperationSchemas.EnsureInput,
    risk: OperationRisk.SafeWrite, capabilities: ["maps"], tags: ["map", "create", "scene"],
    aliases: ["new map", "create map", "make map"],
    examples: ["Ensure maps named Zoning, Transit, and Buildings exist."],
    related: ["map.activate", "basemap.set", "layer.add"], undoable: true))
{
    /// <summary>
    /// ProjectItemsChanged can arrive well after a map is created. This one-time wait lets that
    /// structural notification publish before the base operation samples the next revision.
    /// </summary>
    internal static readonly TimeSpan DefaultStructuralSettleDelay = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _structuralSettleDelay = structuralSettleDelay ?? DefaultStructuralSettleDelay;

    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var name = RequiredString(arguments, "name");
        var type = OptionalString(arguments, "type") ?? "map";
        var basemap = Basemaps.Parse(maps, OptionalString(arguments, "basemap") ?? Basemaps.None);
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var existing = maps.FindByName(name);
            if (existing is not null)
                return new MapMutationResult(existing.Id, existing.Name, false);

            var created = maps.Create(name, ToViewKind(type), basemap);
            return new MapMutationResult(created.Id, created.Name, true);
        }, cancellationToken).ConfigureAwait(false);
        if (data.Created)
        {
            // ProjectItemsChanged can arrive well after CreateMap returns. Give that
            // one-time structural notification a chance to publish before the base
            // operation samples the revision handed to the next workflow step.
            await Task.Delay(_structuralSettleDelay, cancellationToken).ConfigureAwait(false);
        }
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }

    /// <summary>Any type containing "scene" is a scene; only "global-scene" is global.</summary>
    internal static MapViewKind ToViewKind(string type) =>
        !type.Contains("scene", StringComparison.OrdinalIgnoreCase)
            ? MapViewKind.Map
            : string.Equals(type, "global-scene", StringComparison.OrdinalIgnoreCase)
                ? MapViewKind.GlobalScene
                : MapViewKind.LocalScene;

    private sealed record MapMutationResult(string Id, string Name, bool Created);
}

internal sealed class MapActivateOperation(IMapService maps) : ProOperationBase(OperationDescriptor.Create(
    "map.activate", "Activate map",
    "Opens or activates a map view for a stable map handle or unambiguous name.",
    MapOperationSchemas.ActivateInput,
    risk: OperationRisk.SafeWrite, executionTarget: ExecutionTarget.ArcGISUiThread,
    capabilities: ["maps"], tags: ["map", "view", "open", "switch"],
    aliases: ["switch map", "open map", "show map"], examples: ["Activate the Transit map."],
    related: ["map.list", "view.capture"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var mapReference = RequiredString(arguments, "map");
        var map = await context.Dispatcher.OnMainCimThreadAsync(
            () => maps.Resolve(mapReference), cancellationToken).ConfigureAwait(false);
        await context.Dispatcher.OnUiThreadAsync(async () =>
        {
            await maps.ActivateViewAsync(map).ConfigureAwait(true);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(new { id = map.Id, map.Name }), snapshot.Revision);
    }
}

/// <summary>Basemap argument parsing shared by map.ensure and basemap.set.</summary>
internal static class Basemaps
{
    /// <summary>The basemap used when map.ensure is given none.</summary>
    public const string None = "None";

    /// <summary>Keeps letters and digits only and accepts "imagery" for the satellite basemap.</summary>
    public static string Normalize(string value)
    {
        var normalized = new string(value.Where(char.IsLetterOrDigit).ToArray());
        return string.Equals(normalized, "imagery", StringComparison.OrdinalIgnoreCase) ? "Satellite" : normalized;
    }

    public static string UnknownMessage(string value) =>
        $"Unknown basemap '{value}'. Use a Pro basemap name such as None, Topographic, Streets, Imagery, or OpenStreetMap.";

    /// <summary>The host's canonical basemap name; throws for an unknown basemap.</summary>
    public static string Parse(IMapService maps, string value) =>
        maps.TryGetBasemap(Normalize(value), out var canonical)
            ? canonical
            : throw OperationException.InvalidArgument(UnknownMessage(value));
}
