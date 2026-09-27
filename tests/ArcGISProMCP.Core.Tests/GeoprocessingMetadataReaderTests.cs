using ArcGISProMCP.Core.Geoprocessing;

namespace ArcGISProMCP.Core.Tests;

public sealed class GeoprocessingMetadataReaderTests
{
    private static ToolboxReadResult ReadFixture() =>
        ToolboxMetadataReader.Read(
            new DirectoryToolboxSource(GeoprocessingFixtures.FixtureToolbox),
            isSystem: true,
            GpDeprecationList.Load(Path.Combine(GeoprocessingFixtures.Root, "deprecated.list")));

    private static GpToolDescription Tool(string name) =>
        ReadFixture().Tools.Single(tool => tool.Tool.Name == name);

    [Fact]
    public void Toolbox_resolves_alias_title_and_toolset_names()
    {
        var result = ReadFixture();

        Assert.Empty(result.Warnings);
        Assert.Equal("fixture", result.Toolbox.Alias);
        Assert.Equal("Fixture Tools", result.Toolbox.DisplayName);
        Assert.Equal("Synthetic toolbox used by unit tests.", result.Toolbox.Description);
        Assert.Equal(GpToolboxKind.Directory, result.Toolbox.Kind);
        Assert.True(result.Toolbox.Indexed);
        Assert.Equal(10, result.Toolbox.ToolCount);
        Assert.Null(Tool("OldBuffer").Tool.Toolset);
        Assert.Equal("Proximity", Tool("BufferZones").Tool.Toolset);
        Assert.Equal("Scripted", Tool("ScriptSummary").Tool.Toolset);
    }

    [Fact]
    public void Name_colon_folder_entries_map_the_tool_name_to_another_folder()
    {
        var erase = Tool("EraseRows");

        Assert.Equal("fixture.EraseRows", erase.Tool.ExecutionName);
        Assert.Equal("Erase Rows", erase.Tool.DisplayName);
    }

    [Fact]
    public void Rc_references_resolve_case_insensitively_and_fall_back_to_the_raw_key()
    {
        var buffer = Tool("BufferZones");
        var side = buffer.Parameters.Single(parameter => parameter.Name == "side");
        var ratio = buffer.Parameters.Single(parameter => parameter.Name == "ratio");

        Assert.Equal("Buffer Zones", buffer.Tool.DisplayName);
        Assert.Equal("Side Type", side.DisplayName);
        Assert.Equal("Left", side.Domain!.CodedValues!.Single(value => value.Value == "LEFT").Label);
        Assert.Equal("missing.title", ratio.DisplayName);
    }

    [Fact]
    public void Resolve_passes_through_literals_and_null()
    {
        var map = ToolboxMetadataReader.ReadResourceMap("{\"map\": {\"a.title\": \"A\"}}");

        Assert.Equal("A", ToolboxMetadataReader.Resolve("$rc:A.TITLE", map));
        Assert.Equal("b.title", ToolboxMetadataReader.Resolve("$rc:b.title", map));
        Assert.Equal("literal", ToolboxMetadataReader.Resolve("literal", map));
        Assert.Null(ToolboxMetadataReader.Resolve(null, map));
    }

    [Fact]
    public void Xdoc_html_is_stripped_and_entities_decoded()
    {
        var distance = Tool("BufferZones").Parameters.Single(parameter => parameter.Name == "distance");

        Assert.Equal(
            "The distance around the input features. Full — Both sides & ends. Units default to the input's spatial reference when a < b.",
            distance.Description);
        Assert.Equal("a < b and c > d", ToolboxMetadataReader.CleanText("a < b and c > d"));
        Assert.Null(ToolboxMetadataReader.CleanText("<xdoc> </xdoc>"));
    }

    [Fact]
    public void Parameters_are_ordered_by_display_order_and_carry_execution_positions()
    {
        var parameters = Tool("BufferZones").Parameters;

        Assert.Equal(
            ["in_features", "out_feature_class", "distance", "side", "dissolve", "dissolve_fields", "segments", "classes", "ratio", "zone_count", "report_file"],
            parameters.Select(parameter => parameter.Name));
        // Positional order is definition order without derived parameters (ratio before classes).
        Assert.Equal(
            ["in_features", "out_feature_class", "distance", "side", "dissolve", "dissolve_fields", "segments", "ratio", "classes", "report_file"],
            Tool("BufferZones").PositionalParameters.Select(parameter => parameter.Name));
        Assert.Null(parameters.Single(parameter => parameter.Name == "zone_count").Position);
        Assert.Equal(9, parameters.Single(parameter => parameter.Name == "report_file").Position);
    }

    [Fact]
    public void Describe_fields_are_flattened()
    {
        var buffer = Tool("BufferZones");
        var byName = buffer.Parameters.ToDictionary(parameter => parameter.Name);

        Assert.Equal("FunctionTool", buffer.Tool.ToolType);
        Assert.Equal("Creates zone polygons around input features to a specified distance.", buffer.Description);
        Assert.Equal(["mCPU", "overwrite_on"], buffer.Attributes);
        Assert.Equal(["extent", "outputCoordinateSystem", "workspace"], buffer.Environments);
        Assert.Equal(["around", "distance", "ring", "surround"], buffer.Keywords);

        var input = byName["in_features"];
        Assert.True(input.Required);
        Assert.Equal(GpParameterDirection.Input, input.Direction);
        Assert.Equal("GPFeatureLayer", input.DataType);
        Assert.Equal(["Polygon", "Polyline", "Point"], input.Domain!.GeometryTypes!);
        Assert.Equal("The input features that will be zoned.", input.Description);

        Assert.Equal(GpParameterDirection.Output, byName["out_feature_class"].Direction);
        Assert.True(byName["out_feature_class"].Required);

        var distance = byName["distance"];
        Assert.Equal("GPComposite<GPLinearUnit|Field>", distance.DataType);
        Assert.Equal(["GPLinearUnit", "Field"], distance.DataTypes);
        Assert.Equal(["null", "GPFieldDomain"], distance.Domain!.Items!.Select(item => item.Type));
        Assert.Equal(["Short", "Double"], distance.Domain.Items![1].FieldTypes!);
        Assert.Equal(["in_features"], distance.DependsOn);

        var side = byName["side"];
        Assert.False(side.Required);
        Assert.Equal(GpParameterUsage.Optional, side.Usage);
        Assert.Equal("FULL", side.DefaultValue);
        Assert.Equal(["FULL", "LEFT", "RIGHT"], side.Domain!.CodedValues!.Select(value => value.Value));
        Assert.Equal("Full", side.Domain.CodedValues![0].Label);

        var dissolve = byName["dissolve"];
        Assert.Equal(new GpCodedValue("true", null, "DISSOLVE"), dissolve.Domain!.CodedValues![0]);

        var fields = byName["dissolve_fields"];
        Assert.True(fields.MultiValue);
        Assert.Equal("GPMultiValue<Field>", fields.DataType);
        Assert.Equal(["Field"], fields.DataTypes);

        var segments = byName["segments"];
        Assert.Equal("Advanced Options", segments.Category);
        Assert.Equal(new GpRange("1", "100"), segments.Domain!.Range);

        Assert.Equal(new GpRange("0", null, MinimumInclusive: false), byName["ratio"].Domain!.Range);

        var classes = byName["classes"];
        Assert.Equal(new GpCodedValue("6", null, null), classes.Domain!.CodedValues![2]);

        var derived = byName["zone_count"];
        Assert.Equal(GpParameterUsage.Derived, derived.Usage);
        Assert.False(derived.Required);
        Assert.Equal(GpParameterDirection.Output, derived.Direction);

        var report = byName["report_file"];
        Assert.False(report.Enabled);
        Assert.Equal(["csv", "txt"], report.Domain!.FileTypes!);
    }

    [Fact]
    public void Deprecated_list_matches_toolbox_file_name_or_display_name()
    {
        var list = GpDeprecationList.Parse("# comment\n- fixture/oldbuffer\n- data management tools/copyfeatures\n- standalone\n");

        Assert.Equal(3, list.Count);
        Assert.True(list.IsDeprecated("fixture", "Fixture Tools", "OldBuffer"));
        Assert.True(list.IsDeprecated("mgmt", "Data Management Tools", "CopyFeatures"));
        Assert.True(list.IsDeprecated("any", "Any", "Standalone"));
        Assert.False(list.IsDeprecated("fixture", "Fixture Tools", "BufferZones"));
        Assert.True(Tool("OldBuffer").Tool.Deprecated);
        Assert.False(Tool("BufferZones").Tool.Deprecated);
    }

    [Fact]
    public void Zip_source_reads_the_same_metadata_as_the_directory_source()
    {
        var directory = GeoprocessingFixtures.CreateTempDirectory();
        var atbx = GeoprocessingFixtures.CreateAtbx(directory);

        var fromZip = ToolboxMetadataReader.Read(new ZipToolboxSource(atbx), isSystem: false);
        var fromDirectory = ToolboxMetadataReader.Read(new DirectoryToolboxSource(GeoprocessingFixtures.FixtureToolbox), isSystem: false);

        Assert.Equal(GpToolboxKind.Archive, fromZip.Toolbox.Kind);
        Assert.Empty(fromZip.Warnings);
        Assert.Equal(
            fromDirectory.Tools.Select(tool => tool.Tool.ExecutionName),
            fromZip.Tools.Select(tool => tool.Tool.ExecutionName));
        var zipBuffer = fromZip.Tools.Single(tool => tool.Tool.Name == "BufferZones");
        var directoryBuffer = fromDirectory.Tools.Single(tool => tool.Tool.Name == "BufferZones");
        Assert.Equal(directoryBuffer.Parameters.Select(parameter => (parameter.Name, parameter.DisplayName, parameter.DataType)),
            zipBuffer.Parameters.Select(parameter => (parameter.Name, parameter.DisplayName, parameter.DataType)));
        Assert.Equal(directoryBuffer.Keywords, zipBuffer.Keywords);
        Assert.True(new ZipToolboxSource(atbx).FileExists("ScriptSummary.tool\\tool.script.execute.py"));
    }

    [Fact]
    public void Directory_source_refuses_paths_outside_the_toolbox()
    {
        var source = new DirectoryToolboxSource(GeoprocessingFixtures.FixtureToolbox);

        Assert.Null(source.ReadText("../deprecated.list"));
        Assert.False(source.FileExists("../deprecated.list"));
        Assert.NotNull(source.ReadText("toolbox.content"));
    }
}
