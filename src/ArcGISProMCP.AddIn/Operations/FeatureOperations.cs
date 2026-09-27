using System.Globalization;
using System.Text.Json;
using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Editing;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.AddIn.ArcGIS;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Operations;

namespace ArcGISProMCP.AddIn.Operations;

// These are intentionally small, typed operations.  They are not a general ArcPy or
// SQL execution surface: field names are resolved against the layer schema and delete
// accepts exactly one stable feature identity.
internal sealed class FeatureLayerDescribeOperation() : ProOperationBase(OperationDescriptor.Create(
    "feature.layer.describe", "Describe editable feature layer",
    "Returns the feature layer schema, geometry type, stable identifier fields, and editability needed before a feature edit.",
    FeatureOperationSchemas.LayerDescribeInput,
    outputSchema: FeatureOperationSchemas.LayerDescribeOutput,
    capabilities: ["maps"], tags: ["feature", "layer", "schema", "inspect", "metadata"],
    related: ["feature.query", "feature.create", "feature.update", "feature.delete"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var layerReference = RequiredString(arguments, "layer");
        var mapReference = OptionalString(arguments, "map");
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var map = ProHandles.ResolveMap(mapReference);
            var layer = FeatureOperationSupport.ResolveFeatureLayer(map, layerReference);
            using var table = layer.GetTable();
            var definition = table.GetDefinition();
            using var featureClassDefinition = definition as FeatureClassDefinition;
            return new
            {
                map = ProHandles.ForMap(map),
                layer = ProHandles.ForLayer(layer),
                layer.Name,
                editable = layer.IsEditable && layer.CanEditData(),
                objectIdField = definition.GetObjectIDField(),
                globalIdField = definition.HasGlobalID() ? definition.GetGlobalIDField() : null,
                shapeField = featureClassDefinition?.GetShapeField(),
                geometryType = featureClassDefinition?.GetShapeType().ToString(),
                spatialReference = featureClassDefinition is null ? null : new
                {
                    wkid = featureClassDefinition.GetSpatialReference().Wkid,
                    name = featureClassDefinition.GetSpatialReference().Name
                },
                fields = definition.GetFields().Select(field => new
                {
                    field.Name,
                    field.AliasName,
                    type = field.FieldType.ToString(),
                    field.IsNullable,
                    field.IsEditable,
                    field.Length,
                    isObjectId = field.FieldType == FieldType.OID,
                    isGlobalId = field.FieldType == FieldType.GlobalID,
                    isGeometry = field.FieldType == FieldType.Geometry
                }).ToArray()
            };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }
}

internal sealed class FeatureQueryOperation() : ProOperationBase(OperationDescriptor.Create(
    "feature.query", "Query features by attributes and extent",
    "Runs a bounded feature query with a safe where clause and optional layer-coordinate envelope/spatial relationship. Results always include ObjectID and GlobalID when available.",
    FeatureOperationSchemas.QueryInput,
    outputSchema: FeatureOperationSchemas.QueryOutput,
    capabilities: ["maps"], tags: ["feature", "query", "spatial", "selection", "attributes"],
    related: ["feature.layer.describe", "feature.select", "table.query"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var layerReference = RequiredString(arguments, "layer");
        var mapReference = OptionalString(arguments, "map");
        var where = OptionalString(arguments, "where") ?? "1=1";
        QuerySafety.ValidateWhereClause(where);
        var limit = FeatureOperationSupport.ReadLimit(arguments, 100, 500);
        var requestedFields = FeatureOperationSupport.ReadFields(arguments);
        var relationship = FeatureOperationSupport.ReadSpatialRelationship(arguments);

        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var map = ProHandles.ResolveMap(mapReference);
            var layer = FeatureOperationSupport.ResolveFeatureLayer(map, layerReference);
            using var table = layer.GetTable();
            var definition = table.GetDefinition();
            var fields = FeatureOperationSupport.ResolveReadableFields(definition, requestedFields);
            var filter = FeatureOperationSupport.CreateFilter(arguments, where, layer, fields, relationship);
            var rows = FeatureOperationSupport.ReadFeatures(table, filter, fields, limit);
            return new
            {
                map = ProHandles.ForMap(map),
                layer = ProHandles.ForLayer(layer),
                where,
                spatialRelationship = relationship?.ToString(),
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

internal sealed class FeatureSelectOperation() : ProOperationBase(OperationDescriptor.Create(
    "feature.select", "Select bounded features",
    "Replaces or adds to a layer selection using a bounded attribute/spatial query. This changes selection only; it does not edit feature data.",
    FeatureOperationSchemas.SelectInput,
    outputSchema: FeatureOperationSchemas.SelectOutput,
    risk: OperationRisk.SafeWrite, capabilities: ["maps"], tags: ["feature", "select", "spatial", "selection"],
    related: ["feature.query", "map.clear-selection"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var layerReference = RequiredString(arguments, "layer");
        var mapReference = OptionalString(arguments, "map");
        var where = OptionalString(arguments, "where") ?? "1=1";
        QuerySafety.ValidateWhereClause(where);
        var limit = FeatureOperationSupport.ReadLimit(arguments, 100, 500);
        var relationship = FeatureOperationSupport.ReadSpatialRelationship(arguments);
        var mode = OptionalString(arguments, "mode") ?? "new";
        if (mode is not ("new" or "add"))
            throw new ArgumentException("mode must be 'new' or 'add'.", nameof(arguments));
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var map = ProHandles.ResolveMap(mapReference);
            var layer = FeatureOperationSupport.ResolveFeatureLayer(map, layerReference);
            using var table = layer.GetTable();
            var ids = FeatureOperationSupport.ReadObjectIds(table,
                FeatureOperationSupport.CreateFilter(arguments, where, layer, [table.GetDefinition().GetObjectIDField()], relationship), limit);
            if (mode == "new" && ids.Count == 0) layer.Select(new QueryFilter { ObjectIDs = [] }, SelectionCombinationMethod.New);
            else if (ids.Count > 0)
                layer.Select(new QueryFilter { ObjectIDs = ids }, mode == "add" ? SelectionCombinationMethod.Add : SelectionCombinationMethod.New);
            return new { map = ProHandles.ForMap(map), layer = ProHandles.ForLayer(layer), mode, matched = ids.Count, selectionCount = layer.SelectionCount, limit };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }
}

internal sealed class FeatureCreateOperation() : ProOperationBase(OperationDescriptor.Create(
    "feature.create", "Create one feature",
    "Creates one point, single-part polyline, or single-part polygon in an editable layer using schema-validated attributes. Coordinates must use the layer spatial reference.",
    FeatureOperationSchemas.CreateInput,
    outputSchema: FeatureOperationSchemas.CreateOutput,
    risk: OperationRisk.SafeWrite, capabilities: ["maps"], tags: ["feature", "create", "edit", "geometry", "attributes"],
    related: ["feature.layer.describe", "feature.update", "feature.delete"], undoable: true))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var layerReference = RequiredString(arguments, "layer");
        var mapReference = OptionalString(arguments, "map");
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var map = ProHandles.ResolveMap(mapReference);
            var layer = FeatureOperationSupport.ResolveFeatureLayer(map, layerReference);
            FeatureOperationSupport.EnsureEditable(layer);
            using var table = layer.GetTable();
            using var featureClassDefinition = table.GetDefinition() as FeatureClassDefinition
                ?? throw new InvalidOperationException("The layer does not expose a feature-class definition.");
            var geometry = FeatureOperationSupport.ReadGeometry(arguments.GetProperty("geometry"), featureClassDefinition);
            var attributes = FeatureOperationSupport.ReadWritableAttributes(arguments, table.GetDefinition());
            var edit = new EditOperation { Name = "MCP create feature", SelectNewFeatures = false };
            var token = edit.Create(layer, geometry, attributes);
            if (!edit.Execute()) throw new InvalidOperationException($"ArcGIS could not create the feature: {edit.ErrorMessage}");
            return new { map = ProHandles.ForMap(map), layer = ProHandles.ForLayer(layer), objectId = token.ObjectID, globalId = token.GlobalID };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }
}

internal sealed class FeatureUpdateOperation() : ProOperationBase(OperationDescriptor.Create(
    "feature.update", "Update one feature's attributes or geometry",
    "Updates exactly one feature selected by ObjectID or GlobalID. Attribute names and values are validated against the layer schema; coordinates use the layer spatial reference.",
    FeatureOperationSchemas.UpdateInput,
    outputSchema: FeatureOperationSchemas.UpdateOutput,
    risk: OperationRisk.SafeWrite, requiresConfirmation: true, capabilities: ["maps"], tags: ["feature", "update", "edit", "geometry", "attributes"],
    related: ["feature.layer.describe", "feature.create", "feature.delete"], undoable: true))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var layerReference = RequiredString(arguments, "layer");
        var mapReference = OptionalString(arguments, "map");
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var map = ProHandles.ResolveMap(mapReference);
            var layer = FeatureOperationSupport.ResolveFeatureLayer(map, layerReference);
            FeatureOperationSupport.EnsureEditable(layer);
            using var table = layer.GetTable();
            var definition = table.GetDefinition();
            var objectId = FeatureOperationSupport.ResolveObjectId(table, definition, arguments.GetProperty("target"));
            var attributes = FeatureOperationSupport.ReadWritableAttributes(arguments, definition);
            var hasGeometry = arguments.TryGetProperty("geometry", out var geometryElement) && geometryElement.ValueKind == JsonValueKind.Object;
            if (attributes.Count == 0 && !hasGeometry) throw new ArgumentException("Specify attributes and/or geometry.", nameof(arguments));
            var edit = new EditOperation { Name = "MCP update feature", SelectModifiedFeatures = false };
            if (hasGeometry)
            {
                using var featureClassDefinition = definition as FeatureClassDefinition
                    ?? throw new InvalidOperationException("The layer does not expose a feature-class definition.");
                edit.Modify(layer, objectId, FeatureOperationSupport.ReadGeometry(geometryElement, featureClassDefinition), attributes);
            }
            else edit.Modify(layer, objectId, attributes);
            if (!edit.Execute()) throw new InvalidOperationException($"ArcGIS could not update ObjectID {objectId}: {edit.ErrorMessage}");
            return new { map = ProHandles.ForMap(map), layer = ProHandles.ForLayer(layer), objectId, updatedAttributes = attributes.Keys.OrderBy(key => key).ToArray(), geometryUpdated = hasGeometry };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }
}

internal sealed class FeatureDeleteOperation() : ProOperationBase(OperationDescriptor.Create(
    "feature.delete", "Delete one feature",
    "Deletes exactly one editable-layer feature selected by stable ObjectID or GlobalID. This is deliberately not a where-clause or bulk-delete operation.",
    FeatureOperationSchemas.DeleteInput,
    outputSchema: FeatureOperationSchemas.DeleteOutput,
    risk: OperationRisk.Destructive, requiresConfirmation: true, capabilities: ["maps"], tags: ["feature", "delete", "edit", "destructive"],
    related: ["feature.query", "feature.update"], undoable: true))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var layerReference = RequiredString(arguments, "layer");
        var mapReference = OptionalString(arguments, "map");
        var data = await context.Dispatcher.OnMainCimThreadAsync(() =>
        {
            var map = ProHandles.ResolveMap(mapReference);
            var layer = FeatureOperationSupport.ResolveFeatureLayer(map, layerReference);
            FeatureOperationSupport.EnsureEditable(layer);
            using var table = layer.GetTable();
            var objectId = FeatureOperationSupport.ResolveObjectId(table, table.GetDefinition(), arguments.GetProperty("target"));
            var edit = new EditOperation { Name = "MCP delete one feature", SelectModifiedFeatures = false };
            edit.Delete(layer, objectId);
            if (!edit.Execute()) throw new InvalidOperationException($"ArcGIS could not delete ObjectID {objectId}: {edit.ErrorMessage}");
            return new { map = ProHandles.ForMap(map), layer = ProHandles.ForLayer(layer), objectId, deleted = true };
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        return OperationResult.Ok(Json(data), snapshot.Revision);
    }
}

internal static class FeatureOperationSupport
{
    private const int MaximumAttributeCount = 64;

    public static BasicFeatureLayer ResolveFeatureLayer(Map map, string reference) =>
        ProHandles.ResolveLayer(map, reference) as BasicFeatureLayer
        ?? throw new InvalidOperationException("Feature operations require a feature layer.");

    public static void EnsureEditable(BasicFeatureLayer layer)
    {
        if (!layer.IsEditable || !layer.CanEditData())
            throw new InvalidOperationException($"Layer '{layer.Name}' is not editable. Check layer editability, data-source permissions, and active edit constraints.");
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

    public static SpatialRelationship? ReadSpatialRelationship(JsonElement arguments)
    {
        var value = Optional(arguments, "spatialRelationship");
        return value?.ToLowerInvariant() switch
        {
            null or "" => null,
            "intersects" => SpatialRelationship.Intersects,
            "envelopeintersects" => SpatialRelationship.EnvelopeIntersects,
            "contains" => SpatialRelationship.Contains,
            "within" => SpatialRelationship.Within,
            "touches" => SpatialRelationship.Touches,
            "crosses" => SpatialRelationship.Crosses,
            "overlaps" => SpatialRelationship.Overlaps,
            _ => throw new ArgumentException("spatialRelationship must be intersects, envelopeIntersects, contains, within, touches, crosses, or overlaps.", nameof(arguments))
        };
    }

    public static string[] ResolveReadableFields(TableDefinition definition, string[] requested)
    {
        var available = definition.GetFields().Where(field => field.FieldType != FieldType.Geometry).ToArray();
        return requested.Length == 0
            ? available.Where(field => field.FieldType is not (FieldType.Blob or FieldType.Raster or FieldType.XML)).Select(field => field.Name).ToArray()
            : requested.Select(name => available.FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase))?.Name
                ?? throw new ArgumentException($"Unknown or unsupported field '{name}'.", nameof(requested))).ToArray();
    }

    public static QueryFilter CreateFilter(JsonElement arguments, string where, BasicFeatureLayer layer, IReadOnlyCollection<string> fields, SpatialRelationship? relationship)
    {
        if (!arguments.TryGetProperty("envelope", out var envelopeElement) || envelopeElement.ValueKind != JsonValueKind.Object)
            return new QueryFilter { WhereClause = where, SubFields = string.Join(",", fields) };
        if (relationship is null) relationship = SpatialRelationship.Intersects;
        using var table = layer.GetTable();
        using var definition = table.GetDefinition() as FeatureClassDefinition
            ?? throw new InvalidOperationException("The layer does not expose a feature-class definition.");
        var envelope = ReadEnvelope(envelopeElement, definition.GetSpatialReference());
        return new SpatialQueryFilter
        {
            WhereClause = where,
            SubFields = string.Join(",", fields),
            FilterGeometry = envelope,
            SpatialRelationship = relationship.Value
        };
    }

    public static List<Dictionary<string, object?>> ReadFeatures(Table table, QueryFilter filter, IReadOnlyCollection<string> fields, int limit)
    {
        var rows = new List<Dictionary<string, object?>>();
        using var definition = table.GetDefinition();
        var hasGlobalId = definition.HasGlobalID();
        using var cursor = table.Search(filter, false);
        while (rows.Count < limit && cursor.MoveNext())
        {
            using var row = cursor.Current;
            var values = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["objectId"] = row.GetObjectID(),
                ["globalId"] = TryGetGlobalId(row, hasGlobalId)
            };
            foreach (var field in fields) values[field] = PlainValue(row[field]);
            rows.Add(values);
        }
        return rows;
    }

    public static List<long> ReadObjectIds(Table table, QueryFilter filter, int limit)
    {
        var ids = new List<long>();
        using var cursor = table.Search(filter, false);
        while (ids.Count < limit && cursor.MoveNext())
        {
            using var row = cursor.Current;
            ids.Add(row.GetObjectID());
        }
        return ids;
    }

    public static long ResolveObjectId(Table table, TableDefinition definition, JsonElement target)
    {
        if (target.ValueKind != JsonValueKind.Object) throw new ArgumentException("target must be an ObjectID or GlobalID object.", nameof(target));
        if (target.TryGetProperty("objectId", out var objectId) && objectId.TryGetInt64(out var id)) return id;
        if (!target.TryGetProperty("globalId", out var globalId) || globalId.ValueKind != JsonValueKind.String || !Guid.TryParse(globalId.GetString(), out var guid))
            throw new ArgumentException("target must contain objectId or a valid globalId UUID.", nameof(target));
        if (!definition.HasGlobalID()) throw new InvalidOperationException("This layer has no GlobalID field; target it by ObjectID.");
        var globalIdField = definition.GetGlobalIDField();
        var filter = new QueryFilter { WhereClause = $"{globalIdField} = '{guid:B}'", SubFields = definition.GetObjectIDField() };
        using var cursor = table.Search(filter, false);
        if (!cursor.MoveNext()) throw new InvalidOperationException($"No feature matches GlobalID '{guid:D}'.");
        using var row = cursor.Current;
        var objectIdValue = row.GetObjectID();
        if (cursor.MoveNext()) throw new InvalidOperationException($"GlobalID '{guid:D}' did not resolve uniquely.");
        return objectIdValue;
    }

    public static Dictionary<string, object> ReadWritableAttributes(JsonElement arguments, TableDefinition definition)
    {
        if (!arguments.TryGetProperty("attributes", out var attributes) || attributes.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return [];
        if (attributes.ValueKind != JsonValueKind.Object) throw new ArgumentException("attributes must be a JSON object.", nameof(arguments));
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in attributes.EnumerateObject())
        {
            if (result.Count >= MaximumAttributeCount) throw new ArgumentException($"attributes may contain at most {MaximumAttributeCount} fields.", nameof(arguments));
            var field = definition.GetFields().FirstOrDefault(candidate => string.Equals(candidate.Name, property.Name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Unknown field '{property.Name}'.", nameof(arguments));
            if (!field.IsEditable || field.FieldType is FieldType.OID or FieldType.GlobalID or FieldType.Geometry)
                throw new ArgumentException($"Field '{field.Name}' is system-managed or read-only.", nameof(arguments));
            result[field.Name] = ToFieldValue(property.Value, field);
        }
        return result;
    }

    public static Geometry ReadGeometry(JsonElement geometry, FeatureClassDefinition definition)
    {
        if (geometry.ValueKind != JsonValueKind.Object) throw new ArgumentException("geometry must be a JSON object.", nameof(geometry));
        var type = Required(geometry, "type").ToLowerInvariant();
        var spatialReference = definition.GetSpatialReference();
        Geometry result = type switch
        {
            "point" => BuildPoint(geometry, spatialReference),
            "polyline" => PolylineBuilderEx.CreatePolyline(ReadPoints(geometry, spatialReference, 2), spatialReference),
            "polygon" => PolygonBuilderEx.CreatePolygon(ReadPoints(geometry, spatialReference, 3), spatialReference),
            _ => throw new ArgumentException("geometry.type must be point, polyline, or polygon.", nameof(geometry))
        };
        if (!string.Equals(result.GeometryType.ToString(), definition.GetShapeType().ToString(), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Geometry type '{result.GeometryType}' does not match layer shape type '{definition.GetShapeType()}'.", nameof(geometry));
        return result;
    }

    private static Envelope ReadEnvelope(JsonElement envelope, SpatialReference spatialReference)
    {
        var xmin = RequiredNumber(envelope, "xmin");
        var ymin = RequiredNumber(envelope, "ymin");
        var xmax = RequiredNumber(envelope, "xmax");
        var ymax = RequiredNumber(envelope, "ymax");
        if (xmin > xmax || ymin > ymax) throw new ArgumentException("envelope minimum coordinates must not exceed maximum coordinates.", nameof(envelope));
        return EnvelopeBuilderEx.CreateEnvelope(xmin, ymin, xmax, ymax, spatialReference);
    }

    private static MapPoint BuildPoint(JsonElement geometry, SpatialReference spatialReference) =>
        geometry.TryGetProperty("z", out var z) && z.TryGetDouble(out var zValue)
            ? MapPointBuilderEx.CreateMapPoint(RequiredNumber(geometry, "x"), RequiredNumber(geometry, "y"), zValue, spatialReference)
            : MapPointBuilderEx.CreateMapPoint(RequiredNumber(geometry, "x"), RequiredNumber(geometry, "y"), spatialReference);

    private static MapPoint[] ReadPoints(JsonElement geometry, SpatialReference spatialReference, int minimum)
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
                ? MapPointBuilderEx.CreateMapPoint(values[0].GetDouble(), values[1].GetDouble(), values[2].GetDouble(), spatialReference)
                : MapPointBuilderEx.CreateMapPoint(values[0].GetDouble(), values[1].GetDouble(), spatialReference);
        }).ToArray();
        if (points.Length < minimum) throw new ArgumentException($"geometry.coordinates must contain at least {minimum} positions.", nameof(geometry));
        return points;
    }

    private static object ToFieldValue(JsonElement value, Field field)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (!field.IsNullable) throw new ArgumentException($"Field '{field.Name}' is not nullable.");
            return DBNull.Value;
        }
        try
        {
            return field.FieldType switch
            {
                FieldType.String when value.ValueKind == JsonValueKind.String => ValidateString(value.GetString() ?? string.Empty, field),
                FieldType.SmallInteger => checked((short)RequiredInteger(value, field.Name)),
                FieldType.Integer => checked((int)RequiredInteger(value, field.Name)),
                FieldType.BigInteger => RequiredInteger(value, field.Name),
                FieldType.Single => (float)RequiredFieldNumber(value, field.Name),
                FieldType.Double => RequiredFieldNumber(value, field.Name),
                FieldType.Date => DateTime.Parse(RequiredStringValue(value, field.Name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                FieldType.DateOnly => DateOnly.Parse(RequiredStringValue(value, field.Name), CultureInfo.InvariantCulture),
                FieldType.TimeOnly => TimeOnly.Parse(RequiredStringValue(value, field.Name), CultureInfo.InvariantCulture),
                FieldType.TimestampOffset => DateTimeOffset.Parse(RequiredStringValue(value, field.Name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                FieldType.GUID => Guid.Parse(RequiredStringValue(value, field.Name)),
                _ => throw new ArgumentException($"Field '{field.Name}' of type '{field.FieldType}' is not supported by typed feature edits.")
            };
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new ArgumentException($"Value for field '{field.Name}' is not a valid {field.FieldType}.", exception);
        }
    }

    private static string ValidateString(string value, Field field)
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

    private static string? TryGetGlobalId(Row row, bool hasGlobalId)
    {
        if (!hasGlobalId) return null;
        var globalId = row.GetGlobalID();
        return globalId == Guid.Empty ? null : globalId.ToString("D");
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
