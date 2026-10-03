using System.Text.Json;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Operations.Services;
using ArcGISProMCP.Operations.Tests.Fakes;

namespace ArcGISProMCP.Operations.Tests;

/// <summary>
/// The typed feature surface against a fake feature service: schema-validated attributes,
/// stable single-feature identity, editability, geometry checks, bounded queries and selection.
/// </summary>
public sealed class FeatureOperationTests
{
    private static readonly string[] FeatureIds =
        ["feature.layer.describe", "feature.query", "feature.select", "feature.create", "feature.update", "feature.delete"];

    [Fact]
    public void Feature_surface_uses_the_typed_schemas()
    {
        using var pro = new FakePro();
        var schemas = new Dictionary<string, (JsonElement Input, JsonElement Output)>
        {
            ["feature.layer.describe"] = (FeatureOperationSchemas.LayerDescribeInput, FeatureOperationSchemas.LayerDescribeOutput),
            ["feature.query"] = (FeatureOperationSchemas.QueryInput, FeatureOperationSchemas.QueryOutput),
            ["feature.select"] = (FeatureOperationSchemas.SelectInput, FeatureOperationSchemas.SelectOutput),
            ["feature.create"] = (FeatureOperationSchemas.CreateInput, FeatureOperationSchemas.CreateOutput),
            ["feature.update"] = (FeatureOperationSchemas.UpdateInput, FeatureOperationSchemas.UpdateOutput),
            ["feature.delete"] = (FeatureOperationSchemas.DeleteInput, FeatureOperationSchemas.DeleteOutput),
        };

        foreach (var id in FeatureIds)
        {
            var descriptor = pro.Operation(id).Descriptor;
            Assert.Equal(schemas[id].Input.GetRawText(), descriptor.InputSchema.GetRawText());
            Assert.Equal(schemas[id].Output.GetRawText(), descriptor.OutputSchema!.Value.GetRawText());
            Assert.DoesNotContain("\"oneOf\"", descriptor.InputSchema.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("\"format\"", descriptor.InputSchema.GetRawText(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Delete_is_single_target_destructive_and_confirmation_gated()
    {
        using var pro = new FakePro();
        var descriptor = pro.Operation("feature.delete").Descriptor;

        Assert.Equal(OperationRisk.Destructive, descriptor.Risk);
        Assert.True(descriptor.RequiresConfirmation);
        Assert.Contains("Deletes exactly one editable-layer feature", descriptor.Summary, StringComparison.Ordinal);
        Assert.Contains("This is deliberately not a where-clause or bulk-delete operation", descriptor.Summary, StringComparison.Ordinal);
        Assert.False(descriptor.InputSchema.GetProperty("properties").TryGetProperty("where", out _));
    }

    [Fact]
    public void Update_is_single_target_and_confirmation_gated()
    {
        using var pro = new FakePro();
        var descriptor = pro.Operation("feature.update").Descriptor;

        Assert.Equal(OperationRisk.SafeWrite, descriptor.Risk);
        Assert.True(descriptor.RequiresConfirmation);
        Assert.Contains("Updates exactly one feature", descriptor.Summary, StringComparison.Ordinal);
        var target = descriptor.InputSchema.GetProperty("properties").GetProperty("target");
        Assert.Equal(1, target.GetProperty("minProperties").GetInt32());
        Assert.Equal(1, target.GetProperty("maxProperties").GetInt32());
    }

    [Fact]
    public void Create_and_read_operations_do_not_require_confirmation()
    {
        using var pro = new FakePro();

        foreach (var id in new[] { "feature.layer.describe", "feature.query", "feature.select", "feature.create" })
            Assert.False(pro.Operation(id).Descriptor.RequiresConfirmation, id);
        Assert.Equal(OperationRisk.ReadOnly, pro.Operation("feature.query").Descriptor.Risk);
        Assert.Equal(OperationRisk.SafeWrite, pro.Operation("feature.select").Descriptor.Risk);
    }

    [Fact]
    public async Task Describe_reports_schema_identity_and_editability()
    {
        using var pro = Parcels(out var map, out var layer, out _);

        var result = await pro.RunAsync("feature.layer.describe", """{"layer": "Parcels"}""");

        var data = result.Data!.Value;
        Assert.Equal(FakeProState.MapHandle(map), data.GetProperty("map").GetString());
        Assert.Equal(FakeProState.LayerHandle(layer), data.GetProperty("layer").GetString());
        Assert.True(data.GetProperty("editable").GetBoolean());
        Assert.Equal("OBJECTID", data.GetProperty("objectIdField").GetString());
        Assert.Equal("GlobalID", data.GetProperty("globalIdField").GetString());
        Assert.Equal("Point", data.GetProperty("geometryType").GetString());
        Assert.Equal(FakeFeatureTable.ParcelsWkid, data.GetProperty("spatialReference").GetProperty("wkid").GetInt32());
        var fields = data.GetProperty("fields").EnumerateArray().ToArray();
        Assert.True(fields[0].GetProperty("isObjectId").GetBoolean());
        Assert.True(fields[1].GetProperty("isGlobalId").GetBoolean());
        Assert.True(fields[2].GetProperty("isGeometry").GetBoolean());
        Assert.Equal(8, fields[3].GetProperty("length").GetInt32());
        AssertMatchesOutputSchema(pro, "feature.layer.describe", data);
    }

    [Fact]
    public async Task Describe_rejects_layers_without_a_feature_table()
    {
        using var pro = new FakePro();
        pro.State.AddMap("City", "Map", FakeLayer.Other("Imagery", "RasterLayer"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => pro.RunAsync("feature.layer.describe", """{"layer": "Imagery"}"""));

        Assert.Equal("Feature operations require a feature layer.", exception.Message);
    }

    [Fact]
    public async Task Query_is_bounded_projects_plain_values_and_always_returns_identity()
    {
        using var pro = Parcels(out _, out _, out var table);
        var first = table.AddRow(10, 10, "R1");
        table.AddRow(20, 20, "C2");
        table.AddRow(30, 30, "R1");

        var result = await pro.RunAsync("feature.query", """{"layer": "Parcels", "limit": 1}""");

        var data = result.Data!.Value;
        Assert.Equal("1=1", data.GetProperty("where").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("spatialRelationship").ValueKind);
        // Geometry fields are never readable; blob, raster and XML fields are skipped by default.
        Assert.Equal(["OBJECTID", "GlobalID", "ZONE", "FLOORS", "HEIGHT", "SURVEYED", "PARCEL_ID"],
            data.GetProperty("fields").EnumerateArray().Select(field => field.GetString()));
        Assert.Equal(1, data.GetProperty("returned").GetInt32());
        var row = Assert.Single(data.GetProperty("rows").EnumerateArray());
        Assert.Equal(first.ObjectId, row.GetProperty("objectId").GetInt64());
        Assert.Equal(first.GlobalId.ToString("D"), row.GetProperty("globalId").GetString());
        Assert.Equal("R1", row.GetProperty("ZONE").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("HEIGHT").ValueKind);
        Assert.Equal("2024-05-01T00:00:00.0000000Z", row.GetProperty("SURVEYED").GetString());
        AssertMatchesOutputSchema(pro, "feature.query", data);
    }

    [Theory]
    [InlineData(1000, 500)]
    [InlineData(0, 1)]
    public async Task Query_limits_are_clamped(int requested, int expected)
    {
        using var pro = Parcels(out _, out _, out _);

        var result = await pro.RunAsync("feature.query", JsonSerializer.Serialize(new { layer = "Parcels", limit = requested }));

        Assert.Equal(expected, result.Data!.Value.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task Query_defaults_to_one_hundred_rows()
    {
        using var pro = Parcels(out _, out _, out var table);
        for (var index = 0; index < 120; index++) table.AddRow(index, index, "R1");

        var result = await pro.RunAsync("feature.query", """{"layer": "Parcels", "fields": ["zone"]}""");

        Assert.Equal(100, result.Data!.Value.GetProperty("returned").GetInt32());
        Assert.Equal(["ZONE"], result.Data!.Value.GetProperty("fields").EnumerateArray().Select(field => field.GetString()));
    }

    [Theory]
    [InlineData("ZONE = 'R1'; DROP TABLE parcels")]
    [InlineData("ZONE = 'R1' -- comment")]
    [InlineData("/* comment */ 1=1")]
    public async Task Query_and_select_reject_statement_and_comment_tokens(string where)
    {
        using var pro = Parcels(out _, out _, out _);
        var arguments = JsonSerializer.Serialize(new { layer = "Parcels", where });

        var query = await Assert.ThrowsAsync<ArgumentException>(() => pro.RunAsync("feature.query", arguments));
        var select = await Assert.ThrowsAsync<ArgumentException>(() => pro.RunAsync("feature.select", arguments));

        Assert.StartsWith("where clause contains a forbidden statement or comment token.", query.Message, StringComparison.Ordinal);
        Assert.StartsWith("where clause contains a forbidden statement or comment token.", select.Message, StringComparison.Ordinal);
        Assert.Equal(0, pro.Dispatcher.MainCimCalls);
    }

    [Fact]
    public async Task Query_rejects_unknown_and_geometry_fields()
    {
        using var pro = Parcels(out _, out _, out _);

        var unknown = await Assert.ThrowsAsync<OperationException>(() => pro.RunAsync("feature.query", """{"layer": "Parcels", "fields": ["NOPE"]}"""));
        var shape = await Assert.ThrowsAsync<OperationException>(() => pro.RunAsync("feature.query", """{"layer": "Parcels", "fields": ["Shape"]}"""));

        Assert.Equal(OperationErrorCodes.InvalidArguments, unknown.Code);
        Assert.Equal("Unknown or unsupported field 'NOPE'.", unknown.Message);
        Assert.Equal("Unknown or unsupported field 'Shape'.", shape.Message);
    }

    [Fact]
    public async Task An_envelope_makes_a_spatial_query_that_defaults_to_intersects()
    {
        using var pro = Parcels(out _, out _, out var table);
        table.AddRow(10, 10, "R1");
        table.AddRow(50, 50, "R1");

        var result = await pro.RunAsync("feature.query", """{"layer": "Parcels", "envelope": {"xmin": 0, "ymin": 0, "xmax": 20, "ymax": 20}}""");

        Assert.Equal(1, result.Data!.Value.GetProperty("returned").GetInt32());
        // The reported relationship echoes the request; the filter applies the default.
        Assert.Equal(JsonValueKind.Null, result.Data!.Value.GetProperty("spatialRelationship").ValueKind);
        var filter = Assert.Single(table.Filters);
        Assert.Equal("Intersects", filter.SpatialRelationship);
        Assert.Equal(new FeatureEnvelope(0, 0, 20, 20), filter.Envelope);
    }

    [Theory]
    [InlineData("within", "Within")]
    [InlineData("envelopeIntersects", "EnvelopeIntersects")]
    [InlineData("CROSSES", "Crosses")]
    public async Task Spatial_relationships_map_to_ArcGIS_names(string requested, string expected)
    {
        using var pro = Parcels(out _, out _, out var table);

        var result = await pro.RunAsync("feature.query", JsonSerializer.Serialize(new
        {
            layer = "Parcels",
            spatialRelationship = requested,
            envelope = new { xmin = 0, ymin = 0, xmax = 1, ymax = 1 }
        }));

        Assert.Equal(expected, result.Data!.Value.GetProperty("spatialRelationship").GetString());
        Assert.Equal(expected, Assert.Single(table.Filters).SpatialRelationship);
    }

    [Fact]
    public async Task Envelope_and_relationship_errors_keep_their_messages()
    {
        using var pro = Parcels(out _, out _, out _);

        var inverted = await Assert.ThrowsAsync<OperationException>(() => pro.RunAsync("feature.query",
            """{"layer": "Parcels", "envelope": {"xmin": 5, "ymin": 0, "xmax": 1, "ymax": 1}}"""));
        var relationship = await Assert.ThrowsAsync<OperationException>(() => pro.RunAsync("feature.query",
            """{"layer": "Parcels", "spatialRelationship": "near"}"""));

        Assert.Equal(OperationErrorCodes.InvalidArguments, inverted.Code);
        Assert.Equal(OperationErrorCodes.InvalidArguments, relationship.Code);
        Assert.Equal("envelope minimum coordinates must not exceed maximum coordinates.", inverted.Message);
        Assert.Equal(
            "spatialRelationship must be intersects, envelopeIntersects, contains, within, touches, crosses, or overlaps.",
            relationship.Message);
    }

    [Fact]
    public async Task Spatial_filters_need_a_feature_class()
    {
        using var pro = new FakePro();
        pro.State.AddMap("City", "Map", FakeLayer.Feature("Owners", table: FakeFeatureTable.Parcels(geometryType: null!)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => pro.RunAsync("feature.query",
            """{"layer": "Owners", "envelope": {"xmin": 0, "ymin": 0, "xmax": 1, "ymax": 1}}"""));

        Assert.Equal("The layer does not expose a feature-class definition.", exception.Message);
    }

    [Fact]
    public async Task Select_replaces_or_adds_and_a_new_empty_match_clears_the_selection()
    {
        using var pro = Parcels(out _, out _, out var table);
        table.AddRow(1, 1, "R1");
        table.AddRow(2, 2, "C2");
        table.AddRow(3, 3, "R1");

        var replaced = await pro.RunAsync("feature.select", """{"layer": "Parcels", "where": "ZONE = 'R1'"}""");
        var added = await pro.RunAsync("feature.select", """{"layer": "Parcels", "where": "ZONE = 'C2'", "mode": "add"}""");
        var cleared = await pro.RunAsync("feature.select", """{"layer": "Parcels", "where": "ZONE = 'X9'"}""");

        Assert.Equal(2, replaced.Data!.Value.GetProperty("matched").GetInt32());
        Assert.Equal(3, added.Data!.Value.GetProperty("selectionCount").GetInt64());
        Assert.Equal(0, cleared.Data!.Value.GetProperty("selectionCount").GetInt64());
        Assert.All(table.Filters, filter => Assert.Equal(["OBJECTID"], filter.SubFields));
        AssertMatchesOutputSchema(pro, "feature.select", added.Data!.Value);
    }

    [Fact]
    public async Task Query_and_select_read_the_light_schema_not_the_editable_description()
    {
        using var pro = Parcels(out _, out _, out var table);
        table.AddRow(1, 1, "R1");
        var calls = new List<string>();
        pro.Features.Observer = (action, _) => calls.Add(action);

        await pro.RunAsync("feature.query", """{"layer": "Parcels"}""");
        await pro.RunAsync("feature.select", """{"layer": "Parcels"}""");

        Assert.Equal(["schema", "query", "schema", "select"], calls);
    }

    [Fact]
    public async Task Select_mode_must_be_new_or_add()
    {
        using var pro = Parcels(out _, out _, out _);

        var exception = await Assert.ThrowsAsync<OperationException>(() => pro.RunAsync("feature.select", """{"layer": "Parcels", "mode": "toggle"}"""));

        Assert.Equal(OperationErrorCodes.InvalidArguments, exception.Code);
        Assert.Equal("mode must be 'new' or 'add'.", exception.Message);
    }

    [Fact]
    public async Task Create_converts_attributes_to_field_types_and_reports_the_new_identity()
    {
        using var pro = Parcels(out _, out _, out var table);

        var result = await pro.RunAsync("feature.create", """
            {"layer": "Parcels", "geometry": {"type": "point", "x": 5, "y": 6, "z": 7},
             "attributes": {"zone": "R2", "FLOORS": 3, "HEIGHT": null, "SURVEYED": "2025-01-02T03:04:05Z"}}
            """);

        var row = Assert.Single(table.Rows);
        Assert.Equal("R2", row.Values["ZONE"]);
        Assert.Equal((short)3, row.Values["FLOORS"]);
        Assert.Equal(DBNull.Value, row.Values["HEIGHT"]);
        Assert.Equal(new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc), row.Values["SURVEYED"]);
        Assert.Equal(new FeaturePoint(5, 6, 7), Assert.Single(row.Geometry!.Points));
        Assert.Equal(row.ObjectId, result.Data!.Value.GetProperty("objectId").GetInt64());
        Assert.Equal(row.GlobalId.ToString("D"), result.Data!.Value.GetProperty("globalId").GetString());
        Assert.Equal("rev-1", result.WorkspaceRevision);
        AssertMatchesOutputSchema(pro, "feature.create", result.Data!.Value);
    }

    [Theory]
    [InlineData("""{"PARCEL_ID": "P-9"}""", "Field 'PARCEL_ID' is system-managed or read-only.")]
    [InlineData("""{"OBJECTID": 9}""", "Field 'OBJECTID' is system-managed or read-only.")]
    [InlineData("""{"NOPE": 1}""", "Unknown field 'NOPE'.")]
    [InlineData("""{"FLOORS": null}""", "Field 'FLOORS' is not nullable.")]
    [InlineData("""{"FLOORS": 40000}""", "Value for field 'FLOORS' is not a valid SmallInteger.")]
    [InlineData("""{"FLOORS": 2.5}""", "Value for field 'FLOORS' must be an integer.")]
    [InlineData("""{"ZONE": "TOO-LONG-ZONE"}""", "Value for field 'ZONE' exceeds its maximum length of 8.")]
    [InlineData("""{"ZONE": 7}""", "Field 'ZONE' of type 'String' is not supported by typed feature edits.")]
    [InlineData("""{"PHOTO": "AAEC"}""", "Field 'PHOTO' of type 'Blob' is not supported by typed feature edits.")]
    [InlineData("""{"SURVEYED": "yesterday"}""", "Value for field 'SURVEYED' is not a valid Date.")]
    public async Task Attributes_are_validated_against_the_layer_schema(string attributes, string message)
    {
        using var pro = Parcels(out _, out _, out var table);

        var exception = await Assert.ThrowsAsync<OperationException>(() => pro.RunAsync("feature.create",
            $$"""{"layer": "Parcels", "geometry": {"type": "point", "x": 1, "y": 1}, "attributes": {{attributes}}}"""));

        Assert.Equal(OperationErrorCodes.InvalidArguments, exception.Code);
        Assert.Equal(message, exception.Message);
        Assert.Empty(table.Rows);
    }

    [Theory]
    [InlineData("""{"type": "polygon", "coordinates": [[0, 0], [1, 0], [1, 1]]}""", "Geometry type 'Polygon' does not match layer shape type 'Point'.")]
    [InlineData("""{"type": "circle"}""", "geometry.type must be point, polyline, or polygon.")]
    [InlineData("""{"type": "point", "y": 1}""", "'x' must be numeric.")]
    [InlineData("""{"type": "polyline", "coordinates": [[0, 0]]}""", "geometry.coordinates must contain at least 2 positions.")]
    [InlineData("""{"type": "polyline", "coordinates": [[0, 0], [1]]}""", "Each coordinate must be an [x, y] or [x, y, z] numeric array.")]
    public async Task Geometry_is_parsed_and_checked_against_the_layer_shape_type(string geometry, string message)
    {
        using var pro = Parcels(out _, out _, out var table);

        var exception = await Assert.ThrowsAsync<OperationException>(() => pro.RunAsync("feature.create",
            $$"""{"layer": "Parcels", "geometry": {{geometry}}}"""));

        Assert.Equal(OperationErrorCodes.InvalidArguments, exception.Code);
        Assert.Equal(message, exception.Message);
        Assert.Empty(table.Rows);
    }

    [Fact]
    public async Task Polyline_and_polygon_coordinates_keep_optional_z()
    {
        using var pro = new FakePro();
        var lines = FakeFeatureTable.Parcels(geometryType: "Polyline");
        pro.State.AddMap("City", "Map", FakeLayer.Feature("Streets", table: lines));

        await pro.RunAsync("feature.create", """{"layer": "Streets", "geometry": {"type": "polyline", "coordinates": [[0, 0, 5], [10, 0]]}}""");

        var geometry = Assert.Single(lines.Rows).Geometry!;
        Assert.Equal(FeatureGeometryKind.Polyline, geometry.Kind);
        Assert.Equal([new FeaturePoint(0, 0, 5), new FeaturePoint(10, 0, null)], geometry.Points);
    }

    [Fact]
    public async Task Edits_require_an_editable_layer()
    {
        using var pro = Parcels(out _, out _, out var table);
        table.AddRow(1, 1, "R1");
        table.Editable = false;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => pro.RunAsync("feature.delete", """{"layer": "Parcels", "target": {"objectId": 1}}"""));

        Assert.Equal("Layer 'Parcels' is not editable. Check layer editability, data-source permissions, and active edit constraints.", exception.Message);
        Assert.Single(table.Rows);
    }

    [Fact]
    public async Task Update_targets_exactly_one_feature_by_GlobalID()
    {
        using var pro = Parcels(out _, out _, out var table);
        table.AddRow(1, 1, "R1");
        var target = table.AddRow(2, 2, "R1");

        var result = await pro.RunAsync("feature.update", JsonSerializer.Serialize(new
        {
            layer = "Parcels",
            target = new { globalId = target.GlobalId.ToString("B") },
            attributes = new { ZONE = "C1", floors = 4 },
            geometry = new { type = "point", x = 9, y = 9 }
        }));

        Assert.Equal("C1", target.Values["ZONE"]);
        Assert.Equal(new FeaturePoint(9, 9, null), Assert.Single(target.Geometry!.Points));
        Assert.Equal("R1", table.Rows[0].Values["ZONE"]);
        var data = result.Data!.Value;
        Assert.Equal(target.ObjectId, data.GetProperty("objectId").GetInt64());
        Assert.Equal(["FLOORS", "ZONE"], data.GetProperty("updatedAttributes").EnumerateArray().Select(value => value.GetString()));
        Assert.True(data.GetProperty("geometryUpdated").GetBoolean());
        AssertMatchesOutputSchema(pro, "feature.update", data);
    }

    [Fact]
    public async Task Update_needs_attributes_or_geometry()
    {
        using var pro = Parcels(out _, out _, out var table);
        table.AddRow(1, 1, "R1");

        var exception = await Assert.ThrowsAsync<OperationException>(() => pro.RunAsync("feature.update", """{"layer": "Parcels", "target": {"objectId": 1}}"""));

        Assert.Equal(OperationErrorCodes.InvalidArguments, exception.Code);
        Assert.Equal("Specify attributes and/or geometry.", exception.Message);
        Assert.Empty(pro.Features.Edits);
    }

    [Fact]
    public async Task Target_identity_is_validated_and_must_resolve_uniquely()
    {
        using var pro = Parcels(out _, out _, out var table);
        var duplicate = Guid.NewGuid();
        table.AddRow(1, 1, "R1", globalId: duplicate);
        table.AddRow(2, 2, "R1", globalId: duplicate);
        var missing = Guid.NewGuid();

        var malformed = await Assert.ThrowsAsync<OperationException>(() => pro.RunAsync("feature.delete", """{"layer": "Parcels", "target": {"globalId": "not-a-guid"}}"""));
        var none = await Assert.ThrowsAsync<InvalidOperationException>(() => pro.RunAsync("feature.delete",
            JsonSerializer.Serialize(new { layer = "Parcels", target = new { globalId = missing.ToString("N") } })));
        var ambiguous = await Assert.ThrowsAsync<InvalidOperationException>(() => pro.RunAsync("feature.delete",
            JsonSerializer.Serialize(new { layer = "Parcels", target = new { globalId = duplicate.ToString() } })));

        Assert.Equal(OperationErrorCodes.InvalidArguments, malformed.Code);
        Assert.Equal("target must contain objectId or a valid globalId UUID.", malformed.Message);
        Assert.Equal($"No feature matches GlobalID '{missing:D}'.", none.Message);
        Assert.Equal($"GlobalID '{duplicate:D}' did not resolve uniquely.", ambiguous.Message);
        Assert.Equal(2, table.Rows.Count);
    }

    [Fact]
    public async Task GlobalID_targets_need_a_GlobalID_field()
    {
        using var pro = new FakePro();
        var table = FakeFeatureTable.Parcels(withGlobalId: false);
        table.AddRow(1, 1, "R1");
        pro.State.AddMap("City", "Map", FakeLayer.Feature("Parcels", table: table));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => pro.RunAsync("feature.delete",
            JsonSerializer.Serialize(new { layer = "Parcels", target = new { globalId = Guid.NewGuid() } })));

        Assert.Equal("This layer has no GlobalID field; target it by ObjectID.", exception.Message);
    }

    [Fact]
    public async Task Delete_removes_exactly_one_feature_and_is_audited_through_the_executor()
    {
        using var pro = Parcels(out _, out _, out var table);
        table.AddRow(1, 1, "R1");
        var doomed = table.AddRow(2, 2, "R1");

        var result = await pro.InvokeAsync("feature.delete", JsonSerializer.Serialize(new { layer = "Parcels", target = new { objectId = doomed.ObjectId } }));

        Assert.True(result.Success, result.Message);
        Assert.Equal([$"delete Parcels {doomed.ObjectId}"], pro.Features.Edits);
        Assert.DoesNotContain(doomed, table.Rows);
        Assert.Single(table.Rows);
        Assert.True(result.Data!.Value.GetProperty("deleted").GetBoolean());
        Assert.Equal("feature.delete", Assert.Single(pro.Audit.Events).OperationId);
    }

    [Fact]
    public async Task Host_edit_rejections_surface_with_the_ArcGIS_message()
    {
        using var pro = Parcels(out _, out _, out var table);
        table.AddRow(1, 1, "R1");
        table.RejectEditsWith = "Edit session is read-only.";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => pro.RunAsync("feature.update",
            """{"layer": "Parcels", "target": {"objectId": 1}, "attributes": {"ZONE": "C1"}}"""));

        Assert.Equal("ArcGIS could not update ObjectID 1: Edit session is read-only.", exception.Message);
        Assert.Equal("rev-0", pro.Workspace.Revision);
    }

    [Theory]
    [InlineData("feature.layer.describe", """{"layer": "Parcels"}""")]
    [InlineData("feature.query", """{"layer": "Parcels"}""")]
    [InlineData("feature.select", """{"layer": "Parcels"}""")]
    [InlineData("feature.create", """{"layer": "Parcels", "geometry": {"type": "point", "x": 1, "y": 1}}""")]
    [InlineData("feature.update", """{"layer": "Parcels", "target": {"objectId": 1}, "attributes": {"ZONE": "C1"}}""")]
    [InlineData("feature.delete", """{"layer": "Parcels", "target": {"objectId": 1}}""")]
    public async Task A_broken_data_source_reports_layer_data_source_unavailable(string id, string argumentsJson)
    {
        using var pro = Parcels(out _, out _, out var table);
        table.AddRow(1, 1, "R1");
        table.DataSourceBroken = true;

        var result = await pro.InvokeAsync(id, argumentsJson);

        Assert.False(result.Success);
        Assert.Equal(OperationErrorCodes.LayerDataSourceUnavailable, result.ErrorCode);
        Assert.Equal("layer_data_source_unavailable", result.ErrorCode);
        Assert.Contains("'Parcels'", result.Message, StringComparison.Ordinal);
        Assert.Contains("layer.add", result.Message, StringComparison.Ordinal);
        Assert.Equal("R1", Assert.Single(table.Rows).Values["ZONE"]);
    }

    private static FakePro Parcels(out FakeMap map, out FakeLayer layer, out FakeFeatureTable table)
    {
        var pro = new FakePro();
        layer = FakeLayer.Feature("Parcels");
        table = layer.Table!;
        map = pro.State.AddMap("City", "Map", layer);
        return pro;
    }

    private static void AssertMatchesOutputSchema(FakePro pro, string id, JsonElement data) =>
        Assert.Empty(OperationArgumentValidator.Validate(data, pro.Operation(id).Descriptor.OutputSchema!.Value));
}
