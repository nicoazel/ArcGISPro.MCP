using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArcGISProMCP.Core.Workflows;
using ArcGISProMCP.Operations.Services;

namespace ArcGISProMCP.Testing;

/// <summary>
/// A fake ArcGIS Pro project described in JSON: the project, its maps and layers (feature layers
/// carry a schema and rows), layouts, and workflows to seed into the host's library. Loaded by the
/// end-to-end tests, the golden trajectories and tools/ArcGISProMCP.FakeHost.
/// </summary>
internal sealed record FakeProScenario(
    string Name,
    ScenarioProject Project,
    IReadOnlyList<ScenarioMap> Maps,
    string? Description = null,
    string? ActiveMap = null,
    IReadOnlyList<ScenarioLayout>? Layouts = null,
    string? ActiveLayout = null,
    IReadOnlyList<WorkflowDefinition>? Workflows = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static FakeProScenario Load(string path) =>
        Parse(File.ReadAllText(path));

    public static FakeProScenario Parse(string json) =>
        JsonSerializer.Deserialize<FakeProScenario>(json, JsonOptions)
        ?? throw new InvalidDataException("The scenario JSON is empty.");

    /// <summary>Builds a fresh in-memory project; every call returns independent state.</summary>
    public FakeProState CreateState()
    {
        var state = new FakeProState
        {
            ProjectName = Project.Name,
            ProjectUri = Project.Uri,
            IsDirty = Project.IsDirty,
            TrackDirty = true,
            ActiveMapName = ActiveMap,
            ActiveLayoutName = ActiveLayout
        };
        foreach (var map in Maps)
        {
            var fake = state.AddMap(map.Name, map.Type ?? "Map", [.. map.Layers.Select(CreateLayer)]);
            fake.HasOpenView = string.Equals(map.Name, ActiveMap, StringComparison.Ordinal);
        }
        foreach (var layout in Layouts ?? [])
            state.AddLayout(layout.Name, layout.PageWidthInches ?? 11, layout.PageHeightInches ?? 8.5);
        return state;
    }

    private static FakeLayer CreateLayer(ScenarioLayer layer)
    {
        var uri = $"CIMPATH=layer/{layer.Name.ToLowerInvariant().Replace(' ', '_')}.xml";
        if (layer.Fields is null)
            return new FakeLayer(layer.Name, uri, layer.Type ?? "FeatureLayer", isFeatureLayer: false) { IsVisible = layer.Visible ?? true };

        var table = new FakeFeatureTable
        {
            GeometryType = layer.GeometryType,
            Editable = layer.Editable ?? true,
            SpatialReference = layer.SpatialReference is { } reference
                ? new FeatureSpatialReference(reference.Wkid, reference.Name)
                : new FeatureSpatialReference(FakeFeatureTable.ParcelsWkid, "NAD_1983_StatePlane_Pennsylvania_South_FIPS_3702_Feet")
        };
        foreach (var field in layer.Fields)
            table.Fields.Add(new FeatureFieldInfo(field.Name, field.Alias ?? field.Name, field.Type, field.Nullable ?? true, field.Editable ?? true, field.Length ?? 0));
        foreach (var row in layer.Rows ?? [])
        {
            var values = new List<KeyValuePair<string, object?>>();
            foreach (var field in table.Fields.Where(field => field.Type is not ("OID" or "GlobalID" or "Geometry")))
            {
                var value = row.Attributes is not null && row.Attributes.TryGetValue(field.Name, out var element)
                    ? ConvertValue(field, element)
                    : DBNull.Value;
                values.Add(KeyValuePair.Create<string, object?>(field.Name, value));
            }
            var objectId = row.ObjectId ?? (table.Rows.Count == 0 ? 1 : table.Rows.Max(existing => existing.ObjectId) + 1);
            // Deterministic GlobalIDs keep trajectories stable across runs.
            table.AddRow(ToGeometry(row.Geometry), values, objectId, row.GlobalId ?? StableGuid($"{layer.Name}/{objectId}"));
        }

        return new FakeLayer(layer.Name, uri, layer.Type ?? "FeatureLayer")
        {
            Table = table,
            IsVisible = layer.Visible ?? true,
            Elevation = new LayerElevation("on-ground", 0, 1)
        };
    }

    public static Guid StableGuid(string seed) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(seed)).AsSpan(0, 16));

    private static FeatureGeometry? ToGeometry(ScenarioGeometry? geometry)
    {
        if (geometry is null) return null;
        var kind = Enum.Parse<FeatureGeometryKind>(geometry.Type, ignoreCase: true);
        return new FeatureGeometry(kind, [.. geometry.Points.Select(point => point.Count switch
        {
            2 => new FeaturePoint(point[0], point[1], null),
            3 => new FeaturePoint(point[0], point[1], point[2]),
            _ => throw new InvalidDataException("Scenario points are [x, y] or [x, y, z].")
        })]);
    }

    private static object ConvertValue(FeatureFieldInfo field, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return DBNull.Value;
        return field.Type switch
        {
            "SmallInteger" => value.GetInt16(),
            "Integer" => value.GetInt32(),
            "BigInteger" => value.GetInt64(),
            "Single" => value.GetSingle(),
            "Double" => value.GetDouble(),
            "Date" => DateTime.Parse(value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            _ => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText()
        };
    }
}

internal sealed record ScenarioProject(string Name, string Uri, bool IsDirty = false);

internal sealed record ScenarioMap(string Name, IReadOnlyList<ScenarioLayer> Layers, string? Type = null);

/// <summary>A layer; it is a feature layer when <see cref="Fields"/> is present.</summary>
internal sealed record ScenarioLayer(
    string Name,
    string? Type = null,
    string? GeometryType = null,
    bool? Editable = null,
    bool? Visible = null,
    ScenarioSpatialReference? SpatialReference = null,
    IReadOnlyList<ScenarioField>? Fields = null,
    IReadOnlyList<ScenarioRow>? Rows = null);

internal sealed record ScenarioSpatialReference(int Wkid, string Name);

internal sealed record ScenarioField(string Name, string Type, string? Alias = null, bool? Nullable = null, bool? Editable = null, int? Length = null);

internal sealed record ScenarioRow(
    ScenarioGeometry? Geometry = null,
    IReadOnlyDictionary<string, JsonElement>? Attributes = null,
    long? ObjectId = null,
    Guid? GlobalId = null);

internal sealed record ScenarioGeometry(string Type, IReadOnlyList<IReadOnlyList<double>> Points);

internal sealed record ScenarioLayout(string Name, double? PageWidthInches = null, double? PageHeightInches = null);
