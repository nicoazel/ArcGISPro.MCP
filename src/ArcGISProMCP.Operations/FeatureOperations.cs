using System.Globalization;
using System.Text.Json;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Operations.Services;

namespace ArcGISProMCP.Operations;

// These are intentionally small, typed operations.  They are not a general ArcPy or
// SQL execution surface: field names are resolved against the layer schema and delete
// accepts exactly one stable feature identity.
internal sealed class FeatureLayerDescribeOperation(IFeatureService features) : ProOperationBase(OperationDescriptor.Create(
    "feature.layer.describe", "Describe editable feature layer",
    "Returns the feature layer schema, geometry type, stable identifier fields, and editability needed before a feature edit.",
    FeatureOperationSchemas.LayerDescribeInput,
    outputSchema: FeatureOperationSchemas.LayerDescribeOutput,
    capabilities: ["maps"], tags: ["feature", "layer", "schema", "inspect", "metadata"],
    related: ["feature.query", "feature.create", "feature.update", "feature.delete"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var target = FeatureOperationSupport.Target(arguments);
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var layer = features.Describe(target);
            return new
            {
                map = layer.MapId,
                layer = layer.LayerId,
                layer.Name,
                editable = layer.Editable,
                objectIdField = layer.ObjectIdField,
                globalIdField = layer.GlobalIdField,
                shapeField = layer.IsFeatureClass ? layer.ShapeField : null,
                geometryType = layer.IsFeatureClass ? layer.GeometryType : null,
                spatialReference = layer.IsFeatureClass && layer.SpatialReference is { } spatialReference
                    ? new { wkid = spatialReference.Wkid, name = spatialReference.Name }
                    : null,
                fields = layer.Fields.Select(field => new
                {
                    field.Name,
                    field.AliasName,
                    type = field.Type,
                    field.IsNullable,
                    field.IsEditable,
                    field.Length,
                    isObjectId = field.Type == FeatureFieldTypes.ObjectId,
                    isGlobalId = field.Type == FeatureFieldTypes.GlobalId,
                    isGeometry = field.Type == FeatureFieldTypes.Geometry
                }).ToArray()
            };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }
}

internal sealed class FeatureQueryOperation(IFeatureService features) : ProOperationBase(OperationDescriptor.Create(
    "feature.query", "Query features by attributes and extent",
    "Runs a bounded feature query with a safe where clause and optional layer-coordinate envelope/spatial relationship. Results always include ObjectID and GlobalID when available.",
    FeatureOperationSchemas.QueryInput,
    outputSchema: FeatureOperationSchemas.QueryOutput,
    capabilities: ["maps"], tags: ["feature", "query", "spatial", "selection", "attributes"],
    related: ["feature.layer.describe", "feature.select", "table.query"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var target = FeatureOperationSupport.Target(arguments);
        var where = OptionalString(arguments, "where") ?? "1=1";
        QuerySafety.ValidateWhereClause(where);
        var limit = FeatureOperationSupport.ReadLimit(arguments, 100, 500);
        var requestedFields = FeatureOperationSupport.ReadFields(arguments);
        var relationship = FeatureOperationSupport.ReadSpatialRelationship(arguments);

        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var layer = features.Schema(target);
            var fields = FeatureOperationSupport.ResolveReadableFields(layer.Fields, requestedFields);
            var filter = FeatureOperationSupport.CreateFilter(arguments, where, layer, fields, relationship);
            var rows = FeatureOperationSupport.ToRows(features.Query(target, filter, limit), fields, limit);
            return new
            {
                map = layer.MapId,
                layer = layer.LayerId,
                where,
                spatialRelationship = relationship,
                fields,
                returned = rows.Count,
                limit,
                rows
            };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }
}

internal sealed class FeatureSelectOperation(IFeatureService features) : ProOperationBase(OperationDescriptor.Create(
    "feature.select", "Select bounded features",
    "Replaces or adds to a layer selection using a bounded attribute/spatial query. This changes selection only; it does not edit feature data.",
    FeatureOperationSchemas.SelectInput,
    outputSchema: FeatureOperationSchemas.SelectOutput,
    risk: OperationRisk.SafeWrite, capabilities: ["maps"], tags: ["feature", "select", "spatial", "selection"],
    related: ["feature.query", "map.clear-selection"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var target = FeatureOperationSupport.Target(arguments);
        var where = OptionalString(arguments, "where") ?? "1=1";
        QuerySafety.ValidateWhereClause(where);
        var limit = FeatureOperationSupport.ReadLimit(arguments, 100, 500);
        var relationship = FeatureOperationSupport.ReadSpatialRelationship(arguments);
        var mode = OptionalString(arguments, "mode") ?? "new";
        if (mode is not ("new" or "add"))
            throw new ArgumentException("mode must be 'new' or 'add'.", nameof(arguments));
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var layer = features.Schema(target);
            var filter = FeatureOperationSupport.CreateFilter(arguments, where, layer, [layer.ObjectIdField], relationship);
            var selection = features.Select(target, filter, limit, add: mode == "add");
            return new { map = layer.MapId, layer = layer.LayerId, mode, matched = selection.Matched, selectionCount = selection.SelectionCount, limit };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }
}

internal sealed class FeatureCreateOperation(IFeatureService features) : ProOperationBase(OperationDescriptor.Create(
    "feature.create", "Create one feature",
    "Creates one point, single-part polyline, or single-part polygon in an editable layer using schema-validated attributes. Coordinates must use the layer spatial reference.",
    FeatureOperationSchemas.CreateInput,
    outputSchema: FeatureOperationSchemas.CreateOutput,
    risk: OperationRisk.SafeWrite, capabilities: ["maps"], tags: ["feature", "create", "edit", "geometry", "attributes"],
    related: ["feature.layer.describe", "feature.update", "feature.delete"], undoable: true))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var target = FeatureOperationSupport.Target(arguments);
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var layer = features.Describe(target);
            FeatureOperationSupport.EnsureEditable(layer);
            FeatureOperationSupport.EnsureFeatureClass(layer);
            var geometry = FeatureOperationSupport.ReadGeometry(arguments.GetProperty("geometry"), layer);
            var attributes = FeatureOperationSupport.ReadWritableAttributes(arguments, layer.Fields);
            var created = features.Create(target, geometry, attributes);
            return new { map = layer.MapId, layer = layer.LayerId, objectId = created.ObjectId, globalId = created.GlobalId };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }
}

internal sealed class FeatureUpdateOperation(IFeatureService features) : ProOperationBase(OperationDescriptor.Create(
    "feature.update", "Update one feature's attributes or geometry",
    "Updates exactly one feature selected by ObjectID or GlobalID. Attribute names and values are validated against the layer schema; coordinates use the layer spatial reference.",
    FeatureOperationSchemas.UpdateInput,
    outputSchema: FeatureOperationSchemas.UpdateOutput,
    risk: OperationRisk.SafeWrite, requiresConfirmation: true, capabilities: ["maps"], tags: ["feature", "update", "edit", "geometry", "attributes"],
    related: ["feature.layer.describe", "feature.create", "feature.delete"], undoable: true))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var target = FeatureOperationSupport.Target(arguments);
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var layer = features.Describe(target);
            FeatureOperationSupport.EnsureEditable(layer);
            var objectId = FeatureOperationSupport.ResolveObjectId(features, target, layer, arguments.GetProperty("target"));
            var attributes = FeatureOperationSupport.ReadWritableAttributes(arguments, layer.Fields);
            var hasGeometry = arguments.TryGetProperty("geometry", out var geometryElement) && geometryElement.ValueKind == JsonValueKind.Object;
            if (attributes.Count == 0 && !hasGeometry) throw new ArgumentException("Specify attributes and/or geometry.", nameof(arguments));
            FeatureGeometry? geometry = null;
            if (hasGeometry)
            {
                FeatureOperationSupport.EnsureFeatureClass(layer);
                geometry = FeatureOperationSupport.ReadGeometry(geometryElement, layer);
            }
            features.Update(target, objectId, geometry, attributes);
            return new { map = layer.MapId, layer = layer.LayerId, objectId, updatedAttributes = attributes.Keys.OrderBy(key => key).ToArray(), geometryUpdated = hasGeometry };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }
}

internal sealed class FeatureDeleteOperation(IFeatureService features) : ProOperationBase(OperationDescriptor.Create(
    "feature.delete", "Delete one feature",
    "Deletes exactly one editable-layer feature selected by stable ObjectID or GlobalID. This is deliberately not a where-clause or bulk-delete operation.",
    FeatureOperationSchemas.DeleteInput,
    outputSchema: FeatureOperationSchemas.DeleteOutput,
    risk: OperationRisk.Destructive, requiresConfirmation: true, capabilities: ["maps"], tags: ["feature", "delete", "edit", "destructive"],
    related: ["feature.query", "feature.update"], undoable: true))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var target = FeatureOperationSupport.Target(arguments);
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var layer = features.Describe(target);
            FeatureOperationSupport.EnsureEditable(layer);
            var objectId = FeatureOperationSupport.ResolveObjectId(features, target, layer, arguments.GetProperty("target"));
            features.Delete(target, objectId);
            return new { map = layer.MapId, layer = layer.LayerId, objectId, deleted = true };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }
}

/// <summary>ArcGIS field type names the feature operations treat specially.</summary>
internal static class FeatureFieldTypes
{
    public const string ObjectId = "OID";
    public const string GlobalId = "GlobalID";
    public const string Geometry = "Geometry";
}

/// <summary>Argument parsing, schema validation and result shaping for the feature operations.</summary>
internal static class FeatureOperationSupport
{
    private const int MaximumAttributeCount = 64;

    /// <summary>ArcGIS spatial relationship names by lower-case argument value.</summary>
    private static readonly Dictionary<string, string> SpatialRelationships = new(StringComparer.Ordinal)
    {
        ["intersects"] = "Intersects",
        ["envelopeintersects"] = "EnvelopeIntersects",
        ["contains"] = "Contains",
        ["within"] = "Within",
        ["touches"] = "Touches",
        ["crosses"] = "Crosses",
        ["overlaps"] = "Overlaps",
    };

    /// <summary>Reads the layer and map references. Must run before any dispatch, as before the seam.</summary>
    public static FeatureLayerTarget Target(JsonElement arguments)
    {
        var layer = RequiredArgument(arguments, "layer");
        var map = arguments.TryGetProperty("map", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return new FeatureLayerTarget(map, layer);
    }

    public static void EnsureEditable(FeatureLayerInfo layer)
    {
        if (!layer.Editable)
            throw new InvalidOperationException($"Layer '{layer.Name}' is not editable. Check layer editability, data-source permissions, and active edit constraints.");
    }

    public static void EnsureFeatureClass(FeatureLayerInfo layer) => EnsureFeatureClass(layer.IsFeatureClass);

    private static void EnsureFeatureClass(bool isFeatureClass)
    {
        if (!isFeatureClass)
            throw new InvalidOperationException("The layer does not expose a feature-class definition.");
    }

    public static int ReadLimit(JsonElement arguments, int defaultValue, int maximum) =>
        arguments.TryGetProperty("limit", out var limit) && limit.TryGetInt32(out var requested)
            ? Math.Clamp(requested, 1, maximum)
            : defaultValue;

    public static string[] ReadFields(JsonElement arguments) =>
        arguments.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array
            ? fields.EnumerateArray().Select(value => value.GetString()).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaximumAttributeCount).ToArray()
            : [];

    /// <summary>The ArcGIS spatial relationship name, or null when none was given.</summary>
    public static string? ReadSpatialRelationship(JsonElement arguments)
    {
        var value = Optional(arguments, "spatialRelationship");
        if (string.IsNullOrEmpty(value)) return null;
        return SpatialRelationships.TryGetValue(value.ToLowerInvariant(), out var relationship)
            ? relationship
            : throw new ArgumentException("spatialRelationship must be intersects, envelopeIntersects, contains, within, touches, crosses, or overlaps.", nameof(arguments));
    }

    public static string[] ResolveReadableFields(IReadOnlyList<FeatureSchemaField> fields, string[] requested)
    {
        var available = fields.Where(field => field.Type != FeatureFieldTypes.Geometry).ToArray();
        return requested.Length == 0
            ? available.Where(field => field.Type is not ("Blob" or "Raster" or "XML")).Select(field => field.Name).ToArray()
            : requested.Select(name => available.FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase))?.Name
                ?? throw new ArgumentException($"Unknown or unsupported field '{name}'.", nameof(requested))).ToArray();
    }

    /// <summary>
    /// An attribute filter, or a spatial one (default relationship Intersects) when an envelope
    /// is given. Spatial filters need a feature class; that is checked before the envelope.
    /// </summary>
    public static FeatureQueryFilter CreateFilter(JsonElement arguments, string where, FeatureLayerSchema layer, IReadOnlyList<string> fields, string? relationship)
    {
        if (!arguments.TryGetProperty("envelope", out var envelopeElement) || envelopeElement.ValueKind != JsonValueKind.Object)
            return new FeatureQueryFilter(where, fields, null, null);
        EnsureFeatureClass(layer.IsFeatureClass);
        return new FeatureQueryFilter(where, fields, ReadEnvelope(envelopeElement), relationship ?? "Intersects");
    }

    /// <summary>Rows as returned to the caller: objectId, globalId, then each field's plain value.</summary>
    public static List<Dictionary<string, object?>> ToRows(IReadOnlyList<FeatureRow> source, IReadOnlyList<string> fields, int limit)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (var row in source)
        {
            if (rows.Count >= limit) break;
            var values = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["objectId"] = row.ObjectId,
                ["globalId"] = row.GlobalId is { } globalId && globalId != Guid.Empty ? globalId.ToString("D") : null
            };
            for (var index = 0; index < fields.Count; index++) values[fields[index]] = PlainValue(row.Values[index]);
            rows.Add(values);
        }
        return rows;
    }

    public static long ResolveObjectId(IFeatureService features, FeatureLayerTarget layerTarget, FeatureLayerInfo layer, JsonElement target)
    {
        if (target.ValueKind != JsonValueKind.Object) throw new ArgumentException("target must be an ObjectID or GlobalID object.", nameof(target));
        if (target.TryGetProperty("objectId", out var objectId) && objectId.TryGetInt64(out var id)) return id;
        if (!target.TryGetProperty("globalId", out var globalId) || globalId.ValueKind != JsonValueKind.String || !Guid.TryParse(globalId.GetString(), out var guid))
            throw new ArgumentException("target must contain objectId or a valid globalId UUID.", nameof(target));
        if (layer.GlobalIdField is null) throw new InvalidOperationException("This layer has no GlobalID field; target it by ObjectID.");
        var matches = features.FindObjectIdsByGlobalId(layerTarget, guid, 2);
        if (matches.Count == 0) throw new InvalidOperationException($"No feature matches GlobalID '{guid:D}'.");
        if (matches.Count > 1) throw new InvalidOperationException($"GlobalID '{guid:D}' did not resolve uniquely.");
        return matches[0];
    }

    public static Dictionary<string, object> ReadWritableAttributes(JsonElement arguments, IReadOnlyList<FeatureFieldInfo> fields)
    {
        if (!arguments.TryGetProperty("attributes", out var attributes) || attributes.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return [];
        if (attributes.ValueKind != JsonValueKind.Object) throw new ArgumentException("attributes must be a JSON object.", nameof(arguments));
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in attributes.EnumerateObject())
        {
            if (result.Count >= MaximumAttributeCount) throw new ArgumentException($"attributes may contain at most {MaximumAttributeCount} fields.", nameof(arguments));
            var field = fields.FirstOrDefault(candidate => string.Equals(candidate.Name, property.Name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Unknown field '{property.Name}'.", nameof(arguments));
            if (!field.IsEditable || field.Type is FeatureFieldTypes.ObjectId or FeatureFieldTypes.GlobalId or FeatureFieldTypes.Geometry)
                throw new ArgumentException($"Field '{field.Name}' is system-managed or read-only.", nameof(arguments));
            result[field.Name] = ToFieldValue(property.Value, field);
        }
        return result;
    }

    /// <summary>Parses point/polyline/polygon JSON and checks it against the layer shape type.</summary>
    public static FeatureGeometry ReadGeometry(JsonElement geometry, FeatureLayerInfo layer)
    {
        if (geometry.ValueKind != JsonValueKind.Object) throw new ArgumentException("geometry must be a JSON object.", nameof(geometry));
        var type = Required(geometry, "type").ToLowerInvariant();
        var result = type switch
        {
            "point" => new FeatureGeometry(FeatureGeometryKind.Point, [ReadPoint(geometry)]),
            "polyline" => new FeatureGeometry(FeatureGeometryKind.Polyline, ReadPoints(geometry, 2)),
            "polygon" => new FeatureGeometry(FeatureGeometryKind.Polygon, ReadPoints(geometry, 3)),
            _ => throw new ArgumentException("geometry.type must be point, polyline, or polygon.", nameof(geometry))
        };
        if (!string.Equals(result.Kind.ToString(), layer.GeometryType, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Geometry type '{result.Kind}' does not match layer shape type '{layer.GeometryType}'.", nameof(geometry));
        return result;
    }

    private static FeatureEnvelope ReadEnvelope(JsonElement envelope)
    {
        var xmin = RequiredNumber(envelope, "xmin");
        var ymin = RequiredNumber(envelope, "ymin");
        var xmax = RequiredNumber(envelope, "xmax");
        var ymax = RequiredNumber(envelope, "ymax");
        if (xmin > xmax || ymin > ymax) throw new ArgumentException("envelope minimum coordinates must not exceed maximum coordinates.", nameof(envelope));
        return new FeatureEnvelope(xmin, ymin, xmax, ymax);
    }

    private static FeaturePoint ReadPoint(JsonElement geometry) =>
        geometry.TryGetProperty("z", out var z) && z.TryGetDouble(out var zValue)
            ? new FeaturePoint(RequiredNumber(geometry, "x"), RequiredNumber(geometry, "y"), zValue)
            : new FeaturePoint(RequiredNumber(geometry, "x"), RequiredNumber(geometry, "y"), null);

    private static FeaturePoint[] ReadPoints(JsonElement geometry, int minimum)
    {
        if (!geometry.TryGetProperty("coordinates", out var coordinates) || coordinates.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("polyline and polygon geometry require coordinates.", nameof(geometry));
        var points = coordinates.EnumerateArray().Select(coordinate =>
        {
            if (coordinate.ValueKind != JsonValueKind.Array) throw new ArgumentException("Each coordinate must be an [x, y] or [x, y, z] array.", nameof(geometry));
            var values = coordinate.EnumerateArray().ToArray();
            if (values.Length is < 2 or > 3 || values.Any(value => !value.TryGetDouble(out _)))
                throw new ArgumentException("Each coordinate must be an [x, y] or [x, y, z] numeric array.", nameof(geometry));
            return values.Length == 3
                ? new FeaturePoint(values[0].GetDouble(), values[1].GetDouble(), values[2].GetDouble())
                : new FeaturePoint(values[0].GetDouble(), values[1].GetDouble(), null);
        }).ToArray();
        if (points.Length < minimum) throw new ArgumentException($"geometry.coordinates must contain at least {minimum} positions.", nameof(geometry));
        return points;
    }

    private static object ToFieldValue(JsonElement value, FeatureFieldInfo field)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (!field.IsNullable) throw new ArgumentException($"Field '{field.Name}' is not nullable.");
            return DBNull.Value;
        }
        try
        {
            return field.Type switch
            {
                "String" when value.ValueKind == JsonValueKind.String => ValidateString(value.GetString() ?? string.Empty, field),
                "SmallInteger" => checked((short)RequiredInteger(value, field.Name)),
                "Integer" => checked((int)RequiredInteger(value, field.Name)),
                "BigInteger" => RequiredInteger(value, field.Name),
                "Single" => (float)RequiredFieldNumber(value, field.Name),
                "Double" => RequiredFieldNumber(value, field.Name),
                "Date" => DateTime.Parse(RequiredStringValue(value, field.Name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                "DateOnly" => DateOnly.Parse(RequiredStringValue(value, field.Name), CultureInfo.InvariantCulture),
                "TimeOnly" => TimeOnly.Parse(RequiredStringValue(value, field.Name), CultureInfo.InvariantCulture),
                "TimestampOffset" => DateTimeOffset.Parse(RequiredStringValue(value, field.Name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                "GUID" => Guid.Parse(RequiredStringValue(value, field.Name)),
                _ => throw new ArgumentException($"Field '{field.Name}' of type '{field.Type}' is not supported by typed feature edits.")
            };
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new ArgumentException($"Value for field '{field.Name}' is not a valid {field.Type}.", exception);
        }
    }

    private static string ValidateString(string value, FeatureFieldInfo field)
    {
        if (field.Length > 0 && value.Length > field.Length) throw new ArgumentException($"Value for field '{field.Name}' exceeds its maximum length of {field.Length}.");
        return value;
    }

    private static object? PlainValue(object? value) => value switch
    {
        null or DBNull => null,
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset offset => offset.ToString("O", CultureInfo.InvariantCulture),
        DateOnly dateOnly => dateOnly.ToString("O", CultureInfo.InvariantCulture),
        TimeOnly timeOnly => timeOnly.ToString("O", CultureInfo.InvariantCulture),
        Guid guid => guid.ToString("D"),
        byte[] bytes => new { byteLength = bytes.Length },
        string or bool or byte or short or int or long or float or double or decimal => value,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };

    private static string RequiredArgument(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ArgumentException($"Argument '{name}' is required.");
        }

        return value.GetString()!;
    }

    private static string? Optional(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static string Required(JsonElement value, string name) =>
        Optional(value, name) is { Length: > 0 } result ? result : throw new ArgumentException($"'{name}' is required.", nameof(value));

    private static double RequiredNumber(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.TryGetDouble(out var result) ? result : throw new ArgumentException($"'{name}' must be numeric.", nameof(value));

    private static double RequiredFieldNumber(JsonElement value, string field) =>
        value.TryGetDouble(out var result) ? result : throw new ArgumentException($"Value for field '{field}' must be numeric.");

    private static long RequiredInteger(JsonElement value, string field) =>
        value.TryGetInt64(out var result) ? result : throw new ArgumentException($"Value for field '{field}' must be an integer.");

    private static string RequiredStringValue(JsonElement value, string field) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : throw new ArgumentException($"Value for field '{field}' must be a string.");
}
