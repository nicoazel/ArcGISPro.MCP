using ArcGISProMCP.Core.Geoprocessing;

namespace ArcGISProMCP.Core.Tests;

public sealed class GeoprocessingStaticValidatorTests
{
    private static readonly ToolboxCatalog Catalog = new(GeoprocessingFixtures.Root);

    private static GpValidationResult Validate(string tool, params string?[] values) =>
        GpStaticValidator.Validate(Catalog, tool, values);

    private static string[] Codes(GpValidationResult result) => result.Issues.Select(issue => issue.Code).ToArray();

    [Fact]
    public void Valid_request_has_no_issues()
    {
        var result = Validate("fixture.BufferZones", "roads", "memory\\zones", "10 Meters", "left", "DISSOLVE", "a;b", "12", "0.5", "1;6", "#");

        Assert.True(result.Validated);
        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(issue => issue.Message)));
        Assert.Empty(result.Issues);
        Assert.Equal(GpRiskTier.Standard, result.Risk!.Tier);
        Assert.Equal("fixture.BufferZones", result.ToolSummary!.ExecutionName);
    }

    [Fact]
    public void Unknown_tool_is_an_error_with_suggestions()
    {
        var result = Validate("fixture.BuferZones", "x");

        Assert.False(result.Validated);
        Assert.False(result.IsValid);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("tool_not_found", issue.Code);
    }

    [Fact]
    public void Toolbox_paths_are_unvalidated_user_code()
    {
        var result = Validate("C:\\tools\\Custom.pyt\\DoThing", "x");

        Assert.False(result.Validated);
        Assert.True(result.IsValid);
        Assert.Equal(["tool_unindexed"], Codes(result));
        Assert.Equal(GpRiskTier.UserCode, result.Risk!.Tier);
    }

    [Fact]
    public void Deprecated_tool_is_a_warning()
    {
        var result = Validate("fixture.OldBuffer", "roads", "out");

        Assert.True(result.IsValid);
        Assert.Equal(["tool_deprecated"], Codes(result));
    }

    [Fact]
    public void Too_many_values_is_an_error()
    {
        var result = Validate("management.GetCount", "rows", "extra");

        Assert.Equal(["too_many_parameters"], Codes(result));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("#")]
    [InlineData(" ")]
    public void Required_values_cannot_be_unset(string? distance)
    {
        var result = Validate("fixture.BufferZones", "roads", "out", distance);

        var issue = Assert.Single(result.Issues);
        Assert.Equal("required_parameter_missing", issue.Code);
        Assert.Equal("distance", issue.Parameter);
        Assert.Equal(2, issue.Position);
    }

    [Fact]
    public void Missing_trailing_required_values_are_reported()
    {
        var result = Validate("fixture.BufferZones", "roads");

        Assert.Equal(["out_feature_class", "distance"], result.Issues.Select(issue => issue.Parameter));
    }

    [Fact]
    public void Coded_values_must_be_members()
    {
        var result = Validate("fixture.BufferZones", "roads", "out", "5 Meters", "MIDDLE");

        var issue = Assert.Single(result.Issues);
        Assert.Equal("invalid_coded_value", issue.Code);
        Assert.Contains("FULL, LEFT, RIGHT", issue.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("NO_DISSOLVE", true)]
    [InlineData("yes", false)]
    public void Booleans_accept_values_and_keywords(string value, bool valid)
    {
        var result = Validate("fixture.BufferZones", "roads", "out", "5 Meters", "#", value);

        Assert.Equal(valid, result.IsValid);
    }

    [Theory]
    [InlineData("8", null)]
    [InlineData("8.5", "invalid_long")]
    [InlineData("0", "value_out_of_range")]
    [InlineData("101", "value_out_of_range")]
    public void Longs_are_parsed_and_range_checked(string value, string? code)
    {
        var result = Validate("fixture.BufferZones", "roads", "out", "5 Meters", "#", "#", "#", value);

        Assert.Equal(code is null ? [] : [code], Codes(result));
    }

    [Theory]
    [InlineData("0.25", null)]
    [InlineData("1e3", null)]
    [InlineData("0,5", "invalid_double")]
    [InlineData("NaN", "invalid_double")]
    [InlineData("0", "value_out_of_range")]
    public void Doubles_use_invariant_culture_and_exclusive_bounds(string value, string? code)
    {
        var result = Validate("fixture.BufferZones", "roads", "out", "5 Meters", "#", "#", "#", "#", value);

        Assert.Equal(code is null ? [] : [code], Codes(result));
    }

    [Fact]
    public void Multivalues_are_split_and_each_element_checked()
    {
        var result = Validate("fixture.BufferZones", "roads", "out", "5 Meters", "#", "#", "#", "#", "#", "1;'2';3;x");

        Assert.Equal(["invalid_coded_value", "invalid_coded_value"], Codes(result));
        Assert.Contains("'3'", result.Issues[0].Message, StringComparison.Ordinal);
        Assert.Contains("'x'", result.Issues[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Composite_values_are_not_type_checked()
    {
        var result = Validate("fixture.BufferZones", "roads", "out", "BUFF_DIST");

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Multivalue_split_respects_quotes()
    {
        Assert.Equal(["a", "b c", "d;e"], GpStaticValidator.SplitMultiValue("a;'b c';\"d;e\";"));
        Assert.Empty(GpStaticValidator.SplitMultiValue(""));
    }

    [Fact]
    public void Risk_is_reported_for_destructive_tools()
    {
        var result = Validate("fixture.EraseRows", "parcels");

        Assert.True(result.IsValid);
        Assert.Equal(GpRiskTier.Destructive, result.Risk!.Tier);
        Assert.True(result.Risk.MutatesInput);
    }
}
