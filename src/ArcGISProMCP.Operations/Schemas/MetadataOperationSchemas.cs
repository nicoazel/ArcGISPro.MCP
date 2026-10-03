using System.Text.Json;
using S = ArcGISProMCP.Core.Operations.JsonSchemas;

namespace ArcGISProMCP.Operations;

internal static class MetadataOperationSchemas
{
    private static readonly JsonElement Layer = S.String(minLength: 1);
    private static readonly JsonElement Map = S.String();
    private static readonly JsonElement Text = S.String(maxLength: 32768);

    public static JsonElement GetInput { get; } = S.Object(
        [("layer", Layer), ("map", Map), ("includeXml", S.Boolean())],
        ["layer"]);

    public static JsonElement UpdateInput { get; } = S.Object(
        [
            ("layer", Layer),
            ("map", Map),
            ("title", Text),
            ("summary", Text),
            ("description", Text),
            ("tags", S.Array(S.String(maxLength: 512), maxItems: 100, uniqueItems: true)),
            ("credits", Text),
            ("useLimitations", Text),
        ],
        ["layer"]);

    // Describes OperationResult.data for metadata.get (MetadataGetOperation.ReadMetadata with
    // camelCase names; nulls written).
    private static readonly JsonElement Scope = S.Enum("dataset-source", "map-layer");

    public static JsonElement GetOutput { get; } = S.Object(
        [
            ("map", S.String()),
            ("layer", S.String()),
            ("layerName", S.String()),
            ("layerType", S.String()),
            ("isFeatureLayer", S.Boolean()),
            ("datasetPath", S.Any("Dataset path; null when unavailable.")),
            ("metadataScope", Scope),
            ("dataset", S.Object(
                [
                    ("path", S.Any("Dataset path; null when unavailable.")),
                    ("directCatalogItemMetadata", S.Boolean()),
                    ("note", S.String()),
                ],
                ["path", "directCatalogItemMetadata", "note"])),
            ("supportsMetadata", S.Boolean()),
            ("canEditMetadata", S.Boolean()),
            ("usesSourceMetadata", S.Boolean()),
            ("storage", Scope),
            ("persistence", S.Enum(
                "read-only-through-layer-unless-the-source-item-is-edited-directly",
                "project-aprx-layer-metadata")),
            ("metadata", S.Object(
                [
                    ("title", S.Any()),
                    ("summary", S.Any()),
                    ("description", S.Any()),
                    ("tags", S.Array(S.String())),
                    ("credits", S.Any()),
                    ("useLimitations", S.Any()),
                ],
                ["title", "summary", "description", "tags", "credits", "useLimitations"])),
            ("xmlLength", S.Integer(minimum: 0)),
            ("xml", S.Any("Raw metadata XML when includeXml is true; null otherwise.")),
        ],
        ["map", "layer", "layerName", "layerType", "isFeatureLayer", "datasetPath", "metadataScope", "dataset",
         "supportsMetadata", "canEditMetadata", "usesSourceMetadata", "storage", "persistence", "metadata", "xmlLength", "xml"]);
}
