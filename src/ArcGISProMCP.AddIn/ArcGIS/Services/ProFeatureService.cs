using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Editing;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.Operations.Services;

namespace ArcGISProMCP.AddIn.ArcGIS.Services;

internal sealed class ProFeatureService : IFeatureService
{
    public FeatureLayerInfo Describe(FeatureLayerTarget target)
    {
        var (map, layer) = Resolve(target);
        using var table = LayerData.OpenTable(layer);
        var definition = table.GetDefinition();
        using var featureClassDefinition = definition as FeatureClassDefinition;
        var spatialReference = featureClassDefinition?.GetSpatialReference();
        return new FeatureLayerInfo(
            ProHandles.ForMap(map),
            ProHandles.ForLayer(layer),
            layer.Name,
            layer.IsEditable && layer.CanEditData(),
            featureClassDefinition is not null,
            definition.GetObjectIDField(),
            definition.HasGlobalID() ? definition.GetGlobalIDField() : null,
            featureClassDefinition?.GetShapeField(),
            featureClassDefinition?.GetShapeType().ToString(),
            spatialReference is null ? null : new FeatureSpatialReference(spatialReference.Wkid, spatialReference.Name),
            definition.GetFields().Select(field => new FeatureFieldInfo(
                field.Name,
                field.AliasName,
                field.FieldType.ToString(),
                field.IsNullable,
                field.IsEditable,
                field.Length)).ToArray());
    }

    public FeatureLayerSchema Schema(FeatureLayerTarget target)
    {
        var (map, layer) = Resolve(target);
        using var table = LayerData.OpenTable(layer);
        using var definition = table.GetDefinition();
        return new FeatureLayerSchema(
            ProHandles.ForMap(map),
            ProHandles.ForLayer(layer),
            layer.Name,
            definition is FeatureClassDefinition,
            definition.GetObjectIDField(),
            definition.HasGlobalID() ? definition.GetGlobalIDField() : null,
            definition.GetFields().Select(field => new FeatureSchemaField(field.Name, field.FieldType.ToString())).ToArray());
    }

    public IReadOnlyList<FeatureRow> Query(FeatureLayerTarget target, FeatureQueryFilter filter, int limit)
    {
        var (_, layer) = Resolve(target);
        using var table = LayerData.OpenTable(layer);
        using var definition = table.GetDefinition();
        var hasGlobalId = definition.HasGlobalID();
        var rows = new List<FeatureRow>();
        using var cursor = table.Search(CreateFilter(layer, filter), false);
        while (rows.Count < limit && cursor.MoveNext())
        {
            using var row = cursor.Current;
            rows.Add(new FeatureRow(
                row.GetObjectID(),
                hasGlobalId ? row.GetGlobalID() : null,
                filter.SubFields.Select(field => (object?)row[field]).ToArray()));
        }
        return rows;
    }

    public FeatureSelectionResult Select(FeatureLayerTarget target, FeatureQueryFilter filter, int limit, bool add)
    {
        var (_, layer) = Resolve(target);
        using var table = LayerData.OpenTable(layer);
        var ids = new List<long>();
        using (var cursor = table.Search(CreateFilter(layer, filter), false))
        {
            while (ids.Count < limit && cursor.MoveNext())
            {
                using var row = cursor.Current;
                ids.Add(row.GetObjectID());
            }
        }
        if (!add && ids.Count == 0) layer.Select(new QueryFilter { ObjectIDs = [] }, SelectionCombinationMethod.New);
        else if (ids.Count > 0)
            layer.Select(new QueryFilter { ObjectIDs = ids }, add ? SelectionCombinationMethod.Add : SelectionCombinationMethod.New);
        return new FeatureSelectionResult(ids.Count, layer.SelectionCount);
    }

    public IReadOnlyList<long> FindObjectIdsByGlobalId(FeatureLayerTarget target, Guid globalId, int maximum)
    {
        var (_, layer) = Resolve(target);
        using var table = LayerData.OpenTable(layer);
        var definition = table.GetDefinition();
        var filter = new QueryFilter { WhereClause = $"{definition.GetGlobalIDField()} = '{globalId:B}'", SubFields = definition.GetObjectIDField() };
        var ids = new List<long>();
        using var cursor = table.Search(filter, false);
        while (ids.Count < maximum && cursor.MoveNext())
        {
            using var row = cursor.Current;
            ids.Add(row.GetObjectID());
        }
        return ids;
    }

    public FeatureCreateResult Create(FeatureLayerTarget target, FeatureGeometry geometry, IReadOnlyDictionary<string, object> attributes)
    {
        var (_, layer) = Resolve(target);
        using var table = LayerData.OpenTable(layer);
        using var featureClassDefinition = FeatureClass(table);
        var edit = new EditOperation { Name = "MCP create feature", SelectNewFeatures = false };
        var token = edit.Create(layer, ToGeometry(geometry, featureClassDefinition.GetSpatialReference()), Values(attributes));
        if (!edit.Execute()) throw new InvalidOperationException($"ArcGIS could not create the feature: {edit.ErrorMessage}");
        return new FeatureCreateResult(token.ObjectID, token.GlobalID);
    }

    public void Update(FeatureLayerTarget target, long objectId, FeatureGeometry? geometry, IReadOnlyDictionary<string, object> attributes)
    {
        var (_, layer) = Resolve(target);
        var edit = new EditOperation { Name = "MCP update feature", SelectModifiedFeatures = false };
        if (geometry is not null)
        {
            using var table = LayerData.OpenTable(layer);
            using var featureClassDefinition = FeatureClass(table);
            edit.Modify(layer, objectId, ToGeometry(geometry, featureClassDefinition.GetSpatialReference()), Values(attributes));
        }
        else edit.Modify(layer, objectId, Values(attributes));
        if (!edit.Execute()) throw new InvalidOperationException($"ArcGIS could not update ObjectID {objectId}: {edit.ErrorMessage}");
    }

    public void Delete(FeatureLayerTarget target, long objectId)
    {
        var (_, layer) = Resolve(target);
        var edit = new EditOperation { Name = "MCP delete one feature", SelectModifiedFeatures = false };
        edit.Delete(layer, objectId);
        if (!edit.Execute()) throw new InvalidOperationException($"ArcGIS could not delete ObjectID {objectId}: {edit.ErrorMessage}");
    }

    private static (Map Map, BasicFeatureLayer Layer) Resolve(FeatureLayerTarget target)
    {
        var map = ProHandles.ResolveMap(target.Map);
        var layer = ProHandles.ResolveLayer(map, target.Layer) as BasicFeatureLayer
            ?? throw new InvalidOperationException("Feature operations require a feature layer.");
        LayerData.EnsureAvailable(layer);
        return (map, layer);
    }

    private static FeatureClassDefinition FeatureClass(Table table) =>
        table.GetDefinition() as FeatureClassDefinition
        ?? throw new InvalidOperationException("The layer does not expose a feature-class definition.");

    private static QueryFilter CreateFilter(BasicFeatureLayer layer, FeatureQueryFilter filter)
    {
        var subFields = string.Join(",", filter.SubFields);
        if (filter.Envelope is not { } envelope)
            return new QueryFilter { WhereClause = filter.Where, SubFields = subFields };
        using var table = LayerData.OpenTable(layer);
        using var definition = FeatureClass(table);
        return new SpatialQueryFilter
        {
            WhereClause = filter.Where,
            SubFields = subFields,
            FilterGeometry = EnvelopeBuilderEx.CreateEnvelope(envelope.XMin, envelope.YMin, envelope.XMax, envelope.YMax, definition.GetSpatialReference()),
            SpatialRelationship = Enum.Parse<SpatialRelationship>(filter.SpatialRelationship ?? nameof(SpatialRelationship.Intersects))
        };
    }

    private static Geometry ToGeometry(FeatureGeometry geometry, SpatialReference spatialReference) => geometry.Kind switch
    {
        FeatureGeometryKind.Point => ToMapPoint(geometry.Points[0], spatialReference),
        FeatureGeometryKind.Polyline => PolylineBuilderEx.CreatePolyline(geometry.Points.Select(point => ToMapPoint(point, spatialReference)).ToArray(), spatialReference),
        FeatureGeometryKind.Polygon => PolygonBuilderEx.CreatePolygon(geometry.Points.Select(point => ToMapPoint(point, spatialReference)).ToArray(), spatialReference),
        _ => throw new ArgumentOutOfRangeException(nameof(geometry), geometry.Kind, "Unsupported geometry kind.")
    };

    private static MapPoint ToMapPoint(FeaturePoint point, SpatialReference spatialReference) =>
        point.Z is { } z
            ? MapPointBuilderEx.CreateMapPoint(point.X, point.Y, z, spatialReference)
            : MapPointBuilderEx.CreateMapPoint(point.X, point.Y, spatialReference);

    private static Dictionary<string, object> Values(IReadOnlyDictionary<string, object> attributes) =>
        new(attributes, StringComparer.OrdinalIgnoreCase);
}
