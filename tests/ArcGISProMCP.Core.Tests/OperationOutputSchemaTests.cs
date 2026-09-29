using System.Text.Json;
using System.Text.Json.Nodes;
using ArcGISProMCP.Operations;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.Core.Tests;

/// <summary>
/// Output schemas describe OperationResult.data for operations with a stable payload shape.
/// Each schema is checked against a hand-written payload that mirrors the operation's result
/// object (serialized with default JsonSerializer options: declared member names, nulls written).
/// Operations.Tests also validates results that portable operations produce over fake services.
/// </summary>
public sealed class OperationOutputSchemaTests
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);
    private const string Map = "\"pro://map/CIMPATH%3Dmap.xml\"";
    private const string Layer = "\"pro://layer/CIMPATH%3Dmap%2Fparcels.xml\"";

    public static TheoryData<string> OperationIds => new(Declared.Keys.Order(StringComparer.Ordinal));

    /// <summary>Operation id to the production output schema object.</summary>
    internal static IReadOnlyDictionary<string, JsonElement> OutputSchemas =>
        Declared.ToDictionary(pair => pair.Key, pair => pair.Value.Schema, StringComparer.Ordinal);

    private static readonly Dictionary<string, (JsonElement Schema, string Reference, string Sample)> Declared = new(StringComparer.Ordinal)
    {
        ["feature.layer.describe"] = (FeatureOperationSchemas.LayerDescribeOutput, "FeatureOperationSchemas.LayerDescribeOutput", $$$"""
            {"map": {{{Map}}}, "layer": {{{Layer}}}, "Name": "Parcels", "editable": true, "objectIdField": "OBJECTID",
             "globalIdField": null, "shapeField": "Shape", "geometryType": "Polygon",
             "spatialReference": {"wkid": 2272, "name": "NAD_1983_StatePlane_Pennsylvania_South_FIPS_3702_Feet"},
             "fields": [{"Name": "OBJECTID", "AliasName": "OBJECTID", "type": "OID", "IsNullable": false, "IsEditable": false,
                         "Length": 4, "isObjectId": true, "isGlobalId": false, "isGeometry": false}]}
            """),
        ["feature.query"] = (FeatureOperationSchemas.QueryOutput, "FeatureOperationSchemas.QueryOutput", $$$"""
            {"map": {{{Map}}}, "layer": {{{Layer}}}, "where": "1=1", "spatialRelationship": null, "fields": ["ZONE"],
             "returned": 1, "limit": 100, "rows": [{"objectId": 1, "globalId": null, "ZONE": "R1"}]}
            """),
        ["feature.select"] = (FeatureOperationSchemas.SelectOutput, "FeatureOperationSchemas.SelectOutput", $$$"""
            {"map": {{{Map}}}, "layer": {{{Layer}}}, "mode": "new", "matched": 2, "selectionCount": 2, "limit": 100}
            """),
        ["feature.create"] = (FeatureOperationSchemas.CreateOutput, "FeatureOperationSchemas.CreateOutput", $$$"""
            {"map": {{{Map}}}, "layer": {{{Layer}}}, "objectId": 42, "globalId": "0f8fad5b-d9cb-469f-a165-70867728950e"}
            """),
        ["feature.update"] = (FeatureOperationSchemas.UpdateOutput, "FeatureOperationSchemas.UpdateOutput", $$$"""
            {"map": {{{Map}}}, "layer": {{{Layer}}}, "objectId": 42, "updatedAttributes": ["ZONE"], "geometryUpdated": false}
            """),
        ["feature.delete"] = (FeatureOperationSchemas.DeleteOutput, "FeatureOperationSchemas.DeleteOutput", $$$"""
            {"map": {{{Map}}}, "layer": {{{Layer}}}, "objectId": 42, "deleted": true}
            """),
        ["layer.list"] = (LayerOperationSchemas.ListOutput, "LayerOperationSchemas.ListOutput", $$$"""
            {"map": {{{Map}}}, "layers": [
              {"id": {{{Layer}}}, "Name": "Parcels", "type": "FeatureLayer", "IsVisible": true, "Transparency": 0,
               "drawingOrder": 0, "isFeatureLayer": true,
               "elevation": {"mode": "on-ground", "offset": 0, "verticalExaggeration": 1}},
              {"id": "pro://layer/x", "Name": "Imagery", "type": "BasemapLayer", "IsVisible": true, "Transparency": 12.5,
               "drawingOrder": 1, "isFeatureLayer": false, "elevation": null}]}
            """),
        ["map.list"] = (MapOperationSchemas.ListOutput, "MapOperationSchemas.ListOutput", $$$"""
            [{"id": {{{Map}}}, "Name": "Zoning", "type": "Map", "layerCount": 3, "isActive": true}]
            """),
        ["project.get"] = (ProjectOperationSchemas.GetOutput, "ProjectOperationSchemas.GetOutput", """
            {"Project": {"Name": null, "Uri": null, "IsDirty": false, "IsOpen": false},
             "Revision": "r-1", "CapturedAt": "2026-09-26T12:00:00+00:00"}
            """),
        ["view.capture"] = (ViewOperationSchemas.CaptureOutput, "ViewOperationSchemas.CaptureOutput", """
            {"resource": "arcgis://resource/abc", "width": 1280, "height": 800, "sourceKind": "map",
             "sourceName": "Zoning", "sourceUri": "CIMPATH=map.xml", "capturedAt": "2026-09-26T12:00:00+00:00"}
            """),
        ["table.query"] = (TableOperationSchemas.QueryOutput, "TableOperationSchemas.QueryOutput", $$$"""
            {"map": {{{Map}}}, "layer": {{{Layer}}}, "where": "1=1", "fields": ["OBJECTID", "ZONE"], "returned": 1, "limit": 100,
             "rows": [{"OBJECTID": 1, "ZONE": null, "BLOB": {"byteLength": 4}}]}
            """),
        ["table.statistics"] = (TableOperationSchemas.StatisticsOutput, "TableOperationSchemas.StatisticsOutput", $$$"""
            {"map": {{{Map}}}, "layer": {{{Layer}}}, "field": "AREA", "fieldType": "Double", "where": "1=1", "matched": 3,
             "inspected": 3, "sampled": false, "nullCount": 3, "valueCount": 0, "minimum": null, "maximum": null,
             "sum": 0, "mean": null}
            """),
        ["layout.list"] = (LayoutOperationSchemas.ListOutput, "LayoutOperationSchemas.ListOutput", $$$"""
            [{"id": "pro://layout/a", "Name": "Plan", "mapFrames": [{"Name": "Main", "map": {{{Map}}}}, {"Name": "Empty", "map": null}]}]
            """),
        ["layout.inspect"] = (LayoutOperationSchemas.InspectOutput, "LayoutOperationSchemas.InspectOutput", $$$"""
            {"id": "pro://layout/a", "Name": "Plan", "page": {"width": 11, "height": 8.5, "units": "Inch"},
             "elements": [
               {"Name": "Main", "type": "map-frame", "drawingOrder": 0,
                "bounds": {"x": 0.5, "y": 0.5, "width": 10, "height": 7.5, "xMax": 10.5, "yMax": 8},
                "mapFrame": {"map": {{{Map}}}, "mapName": "Zoning", "camera": {"x": 1, "y": 2, "z": null, "scale": 1200,
                  "heading": 0, "pitch": -90, "roll": 0, "viewpoint": "CameraValues", "viewportWidth": 10,
                  "viewportHeight": 7.5, "spatialReferenceWkid": 2272} } },
               {"Name": "Title", "type": "text", "drawingOrder": 1,
                "bounds": {"x": 0.5, "y": 8, "width": 3, "height": 0.4, "xMax": 3.5, "yMax": 8.4}, "mapFrame": null}]}
            """),
        ["metadata.get"] = (MetadataOperationSchemas.GetOutput, "MetadataOperationSchemas.GetOutput", $$$"""
            {"map": {{{Map}}}, "layer": {{{Layer}}}, "layerName": "Parcels", "layerType": "FeatureLayer", "isFeatureLayer": true,
             "datasetPath": null, "metadataScope": "map-layer",
             "dataset": {"path": null, "directCatalogItemMetadata": false, "note": "..."},
             "supportsMetadata": true, "canEditMetadata": true, "usesSourceMetadata": false, "storage": "map-layer",
             "persistence": "project-aprx-layer-metadata",
             "metadata": {"Title": "Parcels", "Summary": null, "Description": null, "Tags": ["zoning"], "Credits": null,
                          "UseLimitations": null},
             "xmlLength": 120, "xml": null}
            """),
    };

    [Theory]
    [MemberData(nameof(OperationIds))]
    public void Representative_payload_validates_against_the_declared_output_schema(string id)
    {
        var (schema, _, sample) = Declared[id];
        var payload = JsonSerializer.Deserialize<JsonElement>(sample);

        Assert.Empty(OperationArgumentValidator.Validate(payload, schema));
    }

    [Theory]
    [MemberData(nameof(OperationIds))]
    public void Result_envelope_accepts_a_successful_result_with_the_payload(string id)
    {
        var (schema, _, sample) = Declared[id];
        var result = OperationResult.Ok(JsonSerializer.Deserialize<JsonElement>(sample), "r-1");
        var wire = JsonSerializer.SerializeToElement(result, WireOptions);

        Assert.Empty(OperationArgumentValidator.Validate(wire, JsonSchemas.ResultEnvelope(schema)));
    }

    [Theory]
    [MemberData(nameof(OperationIds))]
    public void Removing_a_required_top_level_member_is_rejected(string id)
    {
        var (schema, _, sample) = Declared[id];
        var node = JsonNode.Parse(sample)!;
        var obj = node as JsonObject ?? (JsonObject)((JsonArray)node)[0]!;
        var member = obj.First().Key;
        obj.Remove(member);

        Assert.NotEmpty(OperationArgumentValidator.Validate(JsonSerializer.SerializeToElement(node), schema));
    }

    [Fact]
    public void Only_listed_operations_ship_an_output_schema()
    {
        // The descriptor dump is what the add-in's registry published; JsonSchemaBuilderTests
        // requires each listed schema object to equal its entry.
        var shipped = OperationSchemaMigrationTests.LoadDescriptorDump()
            .Where(pair => pair.Value.GetProperty("outputSchema").ValueKind != JsonValueKind.Null)
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal);

        Assert.Equal(Declared.Keys.Order(StringComparer.Ordinal), shipped);
    }

    [Fact]
    public void Add_in_descriptors_declare_their_output_schema()
    {
        // Portable operations are compared with the dump in Operations.Tests; the add-in-only
        // operations cannot be constructed here, so their source must name the schema object.
        var sources = OperationSchemaMigrationTests.ReadAddInOperationSources();
        var checkedIds = 0;
        foreach (var (id, (_, reference, _)) in Declared)
        {
            if (OperationSchemaMigrationTests.FindDescriptor(sources, id) is not { } descriptor) continue;
            Assert.Contains($"outputSchema: {reference},", descriptor, StringComparison.Ordinal);
            checkedIds++;
        }
        Assert.True(checkedIds > 0);
    }
}
