namespace ArcGISProMCP.Operations.Services;

/// <summary>The map and layer references a feature operation was given.</summary>
internal sealed record FeatureLayerTarget(string? Map, string Layer);

/// <summary>One field of a feature layer's table.</summary>
/// <param name="Type">The ArcGIS field type name (OID, GlobalID, Geometry, String, SmallInteger, Integer, BigInteger, Single, Double, Date, DateOnly, TimeOnly, TimestampOffset, GUID, Blob, Raster, XML).</param>
internal sealed record FeatureFieldInfo(
    string Name,
    string AliasName,
    string Type,
    bool IsNullable,
    bool IsEditable,
    int Length);

internal sealed record FeatureSpatialReference(int Wkid, string Name);

/// <summary>The schema and identity of a resolved feature layer.</summary>
/// <param name="Editable">The layer is editable and its data source can be edited.</param>
/// <param name="IsFeatureClass">The table has a feature-class definition (shape field, geometry type, spatial reference).</param>
/// <param name="GlobalIdField">Null when the table has no GlobalID.</param>
/// <param name="GeometryType">The ArcGIS shape type name (Point, Polyline, Polygon, ...); null without a feature class.</param>
internal sealed record FeatureLayerInfo(
    string MapId,
    string LayerId,
    string Name,
    bool Editable,
    bool IsFeatureClass,
    string ObjectIdField,
    string? GlobalIdField,
    string? ShapeField,
    string? GeometryType,
    FeatureSpatialReference? SpatialReference,
    IReadOnlyList<FeatureFieldInfo> Fields);

/// <summary>A field's name and ArcGIS type name (see <see cref="FeatureFieldInfo.Type"/>).</summary>
internal sealed record FeatureSchemaField(string Name, string Type);

/// <summary>
/// What a read or selection needs from a resolved feature layer: identity, identifier fields, whether
/// the table is a feature class, and field names and types. Unlike <see cref="FeatureLayerInfo"/> it
/// does not evaluate editability or read shape and spatial-reference details.
/// </summary>
/// <param name="GlobalIdField">Null when the table has no GlobalID.</param>
internal sealed record FeatureLayerSchema(
    string MapId,
    string LayerId,
    string Name,
    bool IsFeatureClass,
    string ObjectIdField,
    string? GlobalIdField,
    IReadOnlyList<FeatureSchemaField> Fields);

/// <summary>An envelope in the layer's spatial reference.</summary>
internal sealed record FeatureEnvelope(double XMin, double YMin, double XMax, double YMax);

/// <summary>A bounded attribute query, optionally constrained by an envelope.</summary>
/// <param name="SubFields">Fields the query reads.</param>
/// <param name="SpatialRelationship">ArcGIS spatial relationship name; set whenever <paramref name="Envelope"/> is.</param>
internal sealed record FeatureQueryFilter(
    string Where,
    IReadOnlyList<string> SubFields,
    FeatureEnvelope? Envelope,
    string? SpatialRelationship);

/// <summary>One queried row: identity plus raw values in the requested field order.</summary>
/// <param name="GlobalId">Null when the table has no GlobalID.</param>
internal sealed record FeatureRow(long ObjectId, Guid? GlobalId, IReadOnlyList<object?> Values);

internal sealed record FeatureSelectionResult(int Matched, long SelectionCount);

internal sealed record FeaturePoint(double X, double Y, double? Z);

internal enum FeatureGeometryKind
{
    Point,
    Polyline,
    Polygon
}

/// <summary>A point, single-part polyline or single-part polygon in the layer's spatial reference.</summary>
internal sealed record FeatureGeometry(FeatureGeometryKind Kind, IReadOnlyList<FeaturePoint> Points);

internal sealed record FeatureCreateResult(long? ObjectId, Guid? GlobalId);

/// <summary>
/// Typed feature reads and single-feature edits. Every member runs on the main CIM thread.
/// Members resolve the target again on each call and throw InvalidOperationException when the
/// map or layer cannot be found, the layer is not a feature layer, or ArcGIS rejects an edit.
/// </summary>
internal interface IFeatureService
{
    /// <summary>The full schema, editability and spatial reference; used by describe and the edit operations.</summary>
    FeatureLayerInfo Describe(FeatureLayerTarget target);

    /// <summary>The lighter schema reads and selections need; does not evaluate editability.</summary>
    FeatureLayerSchema Schema(FeatureLayerTarget target);

    IReadOnlyList<FeatureRow> Query(FeatureLayerTarget target, FeatureQueryFilter filter, int limit);

    /// <summary>Selects at most <paramref name="limit"/> matches, replacing (or adding to) the selection.</summary>
    /// <remarks>Replacing with no matches clears the selection; adding no matches leaves it unchanged.</remarks>
    FeatureSelectionResult Select(FeatureLayerTarget target, FeatureQueryFilter filter, int limit, bool add);

    /// <summary>ObjectIDs of at most <paramref name="maximum"/> features with this GlobalID.</summary>
    IReadOnlyList<long> FindObjectIdsByGlobalId(FeatureLayerTarget target, Guid globalId, int maximum);

    FeatureCreateResult Create(FeatureLayerTarget target, FeatureGeometry geometry, IReadOnlyDictionary<string, object> attributes);

    /// <summary>Updates attributes and, when given, the geometry of one feature.</summary>
    void Update(FeatureLayerTarget target, long objectId, FeatureGeometry? geometry, IReadOnlyDictionary<string, object> attributes);

    void Delete(FeatureLayerTarget target, long objectId);
}
