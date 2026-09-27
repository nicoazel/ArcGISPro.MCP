using System.Text.Json;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.AddIn.ArcGIS;
using ArcGISProMCP.Core.Metadata;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.AddIn.Operations;

/// <summary>
/// Reads feature-layer metadata through MapMember's documented metadata API.
/// This is intentionally layer/dataset-source aware: a MapMember can either
/// store metadata in the APRX or expose metadata from its underlying source.
/// </summary>
internal sealed class MetadataGetOperation() : ProOperationBase(OperationDescriptor.Create(
    "metadata.get", "Get feature-layer metadata",
    "Reads title, summary, description, tags, credits, and use limitations from a feature layer. The response identifies whether metadata is stored by the map layer or supplied by its dataset source.",
    MetadataOperationSchemas.GetInput,
    outputSchema: MetadataOperationSchemas.GetOutput,
    capabilities: ["maps", "metadata"], tags: ["metadata", "layer", "feature", "dataset"],
    aliases: ["inspect layer metadata", "describe feature layer"], related: ["metadata.update", "layer.list"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var layerReference = RequiredString(arguments, "layer");
        var mapReference = OptionalString(arguments, "map");
        var includeXml = OptionalBoolean(arguments, "includeXml", false);
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var map = ProHandles.ResolveMap(mapReference);
            var layer = ProHandles.ResolveLayer(map, layerReference) as BasicFeatureLayer
                ?? throw new InvalidOperationException("Metadata operations currently require a feature layer. Standalone tables and non-feature sublayers are not supported.");
            return ReadMetadata(map, layer, includeXml);
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }

    internal static object ReadMetadata(Map map, BasicFeatureLayer layer, bool includeXml)
    {
        var xml = layer.GetMetadata();
        var values = MetadataXml.Read(xml);
        var usesSource = layer.GetUseSourceMetadata();
        var canEdit = layer.GetCanEditMetadata();
        var supports = layer.SupportsMetadata;
        var sourcePath = layer.GetPath();
        return new
        {
            map = ProHandles.ForMap(map),
            layer = ProHandles.ForLayer(layer),
            layerName = layer.Name,
            layerType = layer.GetType().Name,
            isFeatureLayer = true,
            datasetPath = sourcePath?.ToString(),
            metadataScope = usesSource ? "dataset-source" : "map-layer",
            dataset = new
            {
                path = sourcePath?.ToString(),
                directCatalogItemMetadata = false,
                note = "ArcGIS Pro's Layer metadata API is the supported path here; direct catalog-item metadata editing is intentionally not inferred from a dataset path."
            },
            supportsMetadata = supports,
            canEditMetadata = canEdit,
            usesSourceMetadata = usesSource,
            storage = usesSource ? "dataset-source" : "map-layer",
            persistence = usesSource
                ? "read-only-through-layer-unless-the-source-item-is-edited-directly"
                : "project-aprx-layer-metadata",
            metadata = values,
            xmlLength = xml.Length,
            xml = includeXml ? xml : null
        };
    }
}

internal sealed class MetadataUpdateOperation() : ProOperationBase(OperationDescriptor.Create(
    "metadata.update", "Update feature-layer metadata",
    "Updates the supported common metadata fields while preserving unrelated metadata XML. If the layer is using dataset-source metadata, the source XML is copied to editable map-layer metadata in the APRX before applying the patch; the dataset item itself is not changed.",
    MetadataOperationSchemas.UpdateInput,
    risk: OperationRisk.ExternalSideEffect, capabilities: ["maps", "metadata"], tags: ["metadata", "layer", "feature", "update"],
    aliases: ["edit layer metadata", "set feature layer description"], related: ["metadata.get"],
    requiresConfirmation: true, undoable: false))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var layerReference = RequiredString(arguments, "layer");
        var mapReference = OptionalString(arguments, "map");
        var patch = ParsePatch(arguments);
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var map = ProHandles.ResolveMap(mapReference);
            var layer = ProHandles.ResolveLayer(map, layerReference) as BasicFeatureLayer
                ?? throw new InvalidOperationException("Metadata operations currently require a feature layer. Standalone tables and non-feature sublayers are not supported.");
            if (!layer.SupportsMetadata)
                throw new NotSupportedException("This feature layer does not support metadata.");
            var before = layer.GetMetadata();
            var usedSourceMetadata = layer.GetUseSourceMetadata();
            if (!layer.GetCanEditMetadata())
            {
                if (!usedSourceMetadata)
                    throw new NotSupportedException("ArcGIS Pro reports this feature layer's map-layer metadata is not editable.");

                layer.SetUseSourceMetadata(false);
                if (!layer.GetCanEditMetadata())
                {
                    layer.SetUseSourceMetadata(true);
                    throw new NotSupportedException("ArcGIS Pro could not switch this source-backed layer to editable map-layer metadata.");
                }
            }

            var after = MetadataXml.Update(before, patch);
            try
            {
                layer.SetMetadata(after);
            }
            catch
            {
                if (usedSourceMetadata) layer.SetUseSourceMetadata(true);
                throw;
            }
            var values = MetadataXml.Read(layer.GetMetadata());
            return new
            {
                map = ProHandles.ForMap(map),
                layer = ProHandles.ForLayer(layer),
                layerName = layer.Name,
                changed = patch,
                detachedFromSourceMetadata = usedSourceMetadata,
                metadata = values,
                metadataScope = layer.GetUseSourceMetadata() ? "dataset-source" : "map-layer",
                storage = layer.GetUseSourceMetadata() ? "dataset-source" : "map-layer",
                persistedBy = "ArcGIS Pro Layer.SetMetadata",
                xmlLength = after.Length
            };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }

    private static MetadataPatch ParsePatch(JsonElement arguments)
    {
        static string? StringValue(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value)) return null;
            if (value.ValueKind != JsonValueKind.String)
                throw new ArgumentException($"Argument '{name}' must be a string.");
            return value.GetString() ?? string.Empty;
        }

        IReadOnlyList<string>? tags = null;
        if (arguments.TryGetProperty("tags", out var tagsElement))
        {
            if (tagsElement.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("Argument 'tags' must be an array of strings.");
            tags = tagsElement.EnumerateArray().Select(value =>
            {
                if (value.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("Argument 'tags' must be an array of strings.");
                return value.GetString() ?? string.Empty;
            }).ToArray();
        }

        var patch = new MetadataPatch(
            StringValue(arguments, "title"), StringValue(arguments, "summary"),
            StringValue(arguments, "description"), tags,
            StringValue(arguments, "credits"), StringValue(arguments, "useLimitations"));
        if (patch.IsEmpty)
            throw new ArgumentException("Specify at least one metadata field to update.");
        return patch;
    }
}
