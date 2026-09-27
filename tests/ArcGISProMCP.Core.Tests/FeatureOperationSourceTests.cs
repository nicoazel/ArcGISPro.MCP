using ArcGISProMCP.Operations;

namespace ArcGISProMCP.Core.Tests;

public sealed class FeatureOperationSourceTests
{
    [Fact]
    public void Feature_surface_uses_typed_sdk_operations_and_explicit_schemas()
    {
        var source = ReadSource();

        Assert.Contains("\"feature.layer.describe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"feature.query\"", source, StringComparison.Ordinal);
        Assert.Contains("\"feature.select\"", source, StringComparison.Ordinal);
        Assert.Contains("\"feature.create\"", source, StringComparison.Ordinal);
        Assert.Contains("\"feature.update\"", source, StringComparison.Ordinal);
        Assert.Contains("\"feature.delete\"", source, StringComparison.Ordinal);
        Assert.Contains("FeatureOperationSchemas.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"oneOf\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"format\"", source, StringComparison.Ordinal);
        Assert.Contains("new EditOperation", source, StringComparison.Ordinal);
        Assert.Contains("edit.Create(layer, geometry, attributes)", source, StringComparison.Ordinal);
        Assert.Contains("edit.Modify(layer, objectId", source, StringComparison.Ordinal);
        Assert.Contains("edit.Delete(layer, objectId)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Reflection", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Feature_edits_validate_schema_identity_editability_and_geometry()
    {
        var source = ReadSource();

        Assert.Contains("EnsureEditable(layer)", source, StringComparison.Ordinal);
        Assert.Contains("layer.IsEditable && layer.CanEditData()", source, StringComparison.Ordinal);
        Assert.Contains("definition.HasGlobalID()", source, StringComparison.Ordinal);
        Assert.Contains("target must contain objectId or a valid globalId UUID", source, StringComparison.Ordinal);
        Assert.Contains("guid:B", source, StringComparison.Ordinal);
        Assert.Contains("field.IsEditable", source, StringComparison.Ordinal);
        Assert.Contains("FieldType.OID or FieldType.GlobalID or FieldType.Geometry", source, StringComparison.Ordinal);
        Assert.Contains("ReadGeometry", source, StringComparison.Ordinal);
        Assert.Contains("MapPointBuilderEx.CreateMapPoint", source, StringComparison.Ordinal);
        Assert.Contains("PolylineBuilderEx.CreatePolyline", source, StringComparison.Ordinal);
        Assert.Contains("PolygonBuilderEx.CreatePolygon", source, StringComparison.Ordinal);
        Assert.Contains("does not match layer shape type", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Query_and_selection_are_bounded_and_support_spatial_filters()
    {
        var source = ReadSource();

        Assert.Contains("QuerySafety.ValidateWhereClause(where)", source, StringComparison.Ordinal);
        Assert.Contains("ReadLimit(arguments, 100, 500)", source, StringComparison.Ordinal);
        Assert.Contains("SpatialQueryFilter", source, StringComparison.Ordinal);
        Assert.Contains("EnvelopeBuilderEx.CreateEnvelope", source, StringComparison.Ordinal);
        Assert.Contains("SpatialRelationship.Intersects", source, StringComparison.Ordinal);
        Assert.Contains("ReadObjectIds", source, StringComparison.Ordinal);
        Assert.Contains("SelectionCombinationMethod.New", source, StringComparison.Ordinal);
        Assert.Contains("mode is not (\"new\" or \"add\")", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Delete_is_single_target_destructive_and_confirmation_gated()
    {
        var source = ReadSource();

        Assert.Contains("risk: OperationRisk.Destructive, requiresConfirmation: true", source, StringComparison.Ordinal);
        Assert.Contains("Deletes exactly one editable-layer feature", source, StringComparison.Ordinal);
        Assert.Contains("This is deliberately not a where-clause or bulk-delete operation", source, StringComparison.Ordinal);
        Assert.Contains("MCP delete one feature", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_is_single_target_and_confirmation_gated()
    {
        var descriptor = Descriptor(ReadSource(), "feature.update");

        Assert.Contains("risk: OperationRisk.SafeWrite, requiresConfirmation: true", descriptor, StringComparison.Ordinal);
        Assert.Contains("Updates exactly one feature", descriptor, StringComparison.Ordinal);
        Assert.Contains("FeatureOperationSchemas.UpdateInput", descriptor, StringComparison.Ordinal);
        var target = FeatureOperationSchemas.UpdateInput.GetProperty("properties").GetProperty("target");
        Assert.Equal(1, target.GetProperty("minProperties").GetInt32());
        Assert.Equal(1, target.GetProperty("maxProperties").GetInt32());
    }

    [Fact]
    public void Create_and_read_operations_do_not_require_confirmation()
    {
        var source = ReadSource();

        foreach (var id in new[] { "feature.layer.describe", "feature.query", "feature.select", "feature.create" })
            Assert.DoesNotContain("requiresConfirmation: true", Descriptor(source, id), StringComparison.Ordinal);
    }

    private static string Descriptor(string source, string id)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            source, $@"OperationDescriptor\.Create\(\s*""{System.Text.RegularExpressions.Regex.Escape(id)}""");
        Assert.True(match.Success, $"Descriptor {id} not found.");
        var start = match.Index;
        var end = source.IndexOf("protected override", start, StringComparison.Ordinal);
        return source[start..end];
    }

    private static string ReadSource()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "ArcGISProMCP.AddIn", "Operations", "FeatureOperations.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException("Could not locate FeatureOperations.cs from the test output tree.");
    }
}
