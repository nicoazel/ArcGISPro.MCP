using System.Globalization;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Operations.Services;

namespace ArcGISProMCP.Testing;

internal sealed class FakeFeatureRow(long objectId, Guid globalId, FeatureGeometry? geometry)
{
    public long ObjectId { get; } = objectId;

    public Guid GlobalId { get; } = globalId;

    public FeatureGeometry? Geometry { get; set; } = geometry;

    public Dictionary<string, object?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// An in-memory feature table. Where clauses support "1=1" and "FIELD = literal" only; envelope
/// filters keep features whose first vertex lies inside the envelope.
/// </summary>
internal sealed class FakeFeatureTable
{
    public const int ParcelsWkid = 2272;

    public string? GeometryType { get; init; } = "Point";

    public bool IsFeatureClass => GeometryType is not null;

    public bool Editable { get; set; } = true;

    public FeatureSpatialReference SpatialReference { get; init; } = new(ParcelsWkid, "NAD_1983_StatePlane_Pennsylvania_South_FIPS_3702_Feet");

    public List<FeatureFieldInfo> Fields { get; } = [];

    public List<FakeFeatureRow> Rows { get; } = [];

    public HashSet<long> Selection { get; } = [];

    public List<FeatureQueryFilter> Filters { get; } = [];

    /// <summary>
    /// Simulates a layer whose data source is broken (for example relative paths after a project
    /// was copied): every service member fails the way ProFeatureService does, with
    /// <c>layer_data_source_unavailable</c>.
    /// </summary>
    public bool DataSourceBroken { get; set; }

    /// <summary>Edits ArcGIS "rejects" with this message instead of applying them.</summary>
    public string? RejectEditsWith { get; set; }

    public string ObjectIdField => Fields.First(candidate => candidate.Type == "OID").Name;

    public string? GlobalIdField => Fields.FirstOrDefault(candidate => candidate.Type == "GlobalID")?.Name;

    /// <summary>A point parcel table with a GlobalID, typed attributes, a read-only field and a blob.</summary>
    public static FakeFeatureTable Parcels(string geometryType = "Point", bool withGlobalId = true)
    {
        var table = new FakeFeatureTable { GeometryType = geometryType };
        table.Fields.Add(new FeatureFieldInfo("OBJECTID", "Object ID", "OID", false, false, 4));
        if (withGlobalId) table.Fields.Add(new FeatureFieldInfo("GlobalID", "GlobalID", "GlobalID", false, false, 38));
        table.Fields.Add(new FeatureFieldInfo("Shape", "Shape", "Geometry", true, true, 0));
        table.Fields.Add(new FeatureFieldInfo("ZONE", "Zone", "String", true, true, 8));
        table.Fields.Add(new FeatureFieldInfo("FLOORS", "Floors", "SmallInteger", false, true, 2));
        table.Fields.Add(new FeatureFieldInfo("HEIGHT", "Height", "Double", true, true, 8));
        table.Fields.Add(new FeatureFieldInfo("SURVEYED", "Surveyed", "Date", true, true, 8));
        table.Fields.Add(new FeatureFieldInfo("PARCEL_ID", "Parcel ID", "String", false, false, 16));
        table.Fields.Add(new FeatureFieldInfo("PHOTO", "Photo", "Blob", true, true, 0));
        return table;
    }

    public FakeFeatureRow AddRow(double x, double y, string zone, short floors = 1, Guid? globalId = null)
    {
        var objectId = Rows.Count == 0 ? 1 : Rows.Max(row => row.ObjectId) + 1;
        var row = new FakeFeatureRow(
            objectId,
            globalId ?? Guid.NewGuid(),
            new FeatureGeometry(FeatureGeometryKind.Point, [new FeaturePoint(x, y, null)]));
        row.Values["ZONE"] = zone;
        row.Values["FLOORS"] = floors;
        row.Values["HEIGHT"] = DBNull.Value;
        row.Values["SURVEYED"] = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        row.Values["PARCEL_ID"] = $"P-{objectId}";
        row.Values["PHOTO"] = new byte[] { 1, 2, 3 };
        Rows.Add(row);
        return row;
    }

    /// <summary>Adds a row with explicit values; the ObjectID defaults to the next free one.</summary>
    public FakeFeatureRow AddRow(FeatureGeometry? geometry, IEnumerable<KeyValuePair<string, object?>> values, long? objectId = null, Guid? globalId = null)
    {
        var id = objectId ?? (Rows.Count == 0 ? 1 : Rows.Max(row => row.ObjectId) + 1);
        if (Rows.Any(row => row.ObjectId == id)) throw new ArgumentException($"ObjectID {id} is already in the table.", nameof(objectId));
        var row = new FakeFeatureRow(id, globalId ?? Guid.NewGuid(), geometry);
        foreach (var (name, value) in values) row.Values[name] = value;
        Rows.Add(row);
        return row;
    }

    public IEnumerable<FakeFeatureRow> Matching(FeatureQueryFilter filter)
    {
        Filters.Add(filter);
        return Rows.Where(row => MatchesWhere(row, filter.Where) && MatchesEnvelope(row, filter));
    }

    private static bool MatchesWhere(FakeFeatureRow row, string where)
    {
        if (where == "1=1") return true;
        var parts = where.Split('=', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2) throw new NotSupportedException($"The fake table cannot evaluate '{where}'.");
        var literal = parts[1].Trim('\'');
        return row.Values.TryGetValue(parts[0], out var value) &&
               string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), literal, StringComparison.Ordinal);
    }

    private static bool MatchesEnvelope(FakeFeatureRow row, FeatureQueryFilter filter)
    {
        if (filter.Envelope is not { } envelope) return true;
        var point = row.Geometry?.Points[0];
        return point is not null &&
               point.X >= envelope.XMin && point.X <= envelope.XMax &&
               point.Y >= envelope.YMin && point.Y <= envelope.YMax;
    }
}

internal sealed class FakeFeatureService(FakeProState state) : IFeatureService
{
    public List<string> Edits { get; } = [];

    /// <summary>
    /// Called after each service member ("describe", "schema", "query", "select", "create", "update",
    /// "delete") with the layer name, so a test can stage a concurrent change at that moment.
    /// </summary>
    public Action<string, string>? Observer { get; set; }

    public FeatureLayerInfo Describe(FeatureLayerTarget target)
    {
        var (map, layer, table) = Resolve(target);
        Observer?.Invoke("describe", layer.Name);
        return new FeatureLayerInfo(
            FakeProState.MapHandle(map),
            FakeProState.LayerHandle(layer),
            layer.Name,
            table.Editable,
            table.IsFeatureClass,
            table.ObjectIdField,
            table.GlobalIdField,
            table.IsFeatureClass ? "Shape" : null,
            table.GeometryType,
            table.IsFeatureClass ? table.SpatialReference : null,
            table.Fields.ToArray());
    }

    public FeatureLayerSchema Schema(FeatureLayerTarget target)
    {
        var (map, layer, table) = Resolve(target);
        Observer?.Invoke("schema", layer.Name);
        return new FeatureLayerSchema(
            FakeProState.MapHandle(map),
            FakeProState.LayerHandle(layer),
            layer.Name,
            table.IsFeatureClass,
            table.ObjectIdField,
            table.GlobalIdField,
            table.Fields.Select(field => new FeatureSchemaField(field.Name, field.Type)).ToArray());
    }

    public IReadOnlyList<FeatureRow> Query(FeatureLayerTarget target, FeatureQueryFilter filter, int limit)
    {
        var (_, layer, table) = Resolve(target);
        var rows = table.Matching(filter)
            .Take(limit)
            .Select(row => new FeatureRow(
                row.ObjectId,
                table.GlobalIdField is null ? null : row.GlobalId,
                filter.SubFields.Select(field => field.Equals(table.ObjectIdField, StringComparison.OrdinalIgnoreCase)
                    ? row.ObjectId
                    : row.Values.GetValueOrDefault(field)).ToArray()))
            .ToArray();
        Observer?.Invoke("query", layer.Name);
        return rows;
    }

    public FeatureSelectionResult Select(FeatureLayerTarget target, FeatureQueryFilter filter, int limit, bool add)
    {
        var (_, layer, table) = Resolve(target);
        var ids = table.Matching(filter).Take(limit).Select(row => row.ObjectId).ToArray();
        if (!add) table.Selection.Clear();
        table.Selection.UnionWith(ids);
        Observer?.Invoke("select", layer.Name);
        return new FeatureSelectionResult(ids.Length, table.Selection.Count);
    }

    public IReadOnlyList<long> FindObjectIdsByGlobalId(FeatureLayerTarget target, Guid globalId, int maximum)
    {
        var (_, _, table) = Resolve(target);
        return table.Rows.Where(row => row.GlobalId == globalId).Take(maximum).Select(row => row.ObjectId).ToArray();
    }

    public FeatureCreateResult Create(FeatureLayerTarget target, FeatureGeometry geometry, IReadOnlyDictionary<string, object> attributes)
    {
        var (_, layer, table) = Resolve(target);
        if (table.RejectEditsWith is { } reason) throw new InvalidOperationException($"ArcGIS could not create the feature: {reason}");
        var row = table.AddRow(0, 0, string.Empty);
        row.Geometry = geometry;
        foreach (var (name, value) in attributes) row.Values[name] = value;
        Edits.Add($"create {layer.Name} {row.ObjectId}");
        Edited("create", layer);
        return new FeatureCreateResult(row.ObjectId, row.GlobalId);
    }

    public void Update(FeatureLayerTarget target, long objectId, FeatureGeometry? geometry, IReadOnlyDictionary<string, object> attributes)
    {
        var (_, layer, table) = Resolve(target);
        if (table.RejectEditsWith is { } reason) throw new InvalidOperationException($"ArcGIS could not update ObjectID {objectId}: {reason}");
        var row = table.Rows.Single(candidate => candidate.ObjectId == objectId);
        if (geometry is not null) row.Geometry = geometry;
        foreach (var (name, value) in attributes) row.Values[name] = value;
        Edits.Add($"update {layer.Name} {objectId}");
        Edited("update", layer);
    }

    public void Delete(FeatureLayerTarget target, long objectId)
    {
        var (_, layer, table) = Resolve(target);
        if (table.RejectEditsWith is { } reason) throw new InvalidOperationException($"ArcGIS could not delete ObjectID {objectId}: {reason}");
        table.Rows.RemoveAll(row => row.ObjectId == objectId);
        Edits.Add($"delete {layer.Name} {objectId}");
        Edited("delete", layer);
    }

    private void Edited(string action, FakeLayer layer)
    {
        if (state.TrackDirty) state.IsDirty = true;
        Observer?.Invoke(action, layer.Name);
    }

    private (FakeMap Map, FakeLayer Layer, FakeFeatureTable Table) Resolve(FeatureLayerTarget target)
    {
        FakeDispatcher.Require(FakeThread.MainCim, "IFeatureService");
        var maps = new FakeMapService(state);
        var map = FakeMapService.Host(maps.Resolve(target.Map));
        const string prefix = "pro://layer/";
        var value = target.Layer.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? Uri.UnescapeDataString(target.Layer[prefix.Length..])
            : target.Layer;
        var layer = map.Layers.FirstOrDefault(candidate =>
                        string.Equals(candidate.Uri, value, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(candidate.Name, value, StringComparison.OrdinalIgnoreCase))
                    ?? throw OperationException.LayerNotFound(target.Layer, map.Name);
        var table = layer.Table ?? throw new InvalidOperationException("Feature operations require a feature layer.");
        if (table.DataSourceBroken) throw OperationException.LayerDataSourceUnavailable(layer.Name);
        return (map, layer, table);
    }
}
