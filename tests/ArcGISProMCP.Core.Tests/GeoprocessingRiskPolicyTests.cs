using ArcGISProMCP.Core.Geoprocessing;

namespace ArcGISProMCP.Core.Tests;

public sealed class GeoprocessingRiskPolicyTests
{
    private static readonly ToolboxCatalog SystemCatalog = new(GeoprocessingFixtures.Root);

    private static GpRiskAssessment Risk(string executionName) =>
        SystemCatalog.Describe(executionName)?.Risk ?? throw new InvalidOperationException($"{executionName} not indexed.");

    [Theory]
    [InlineData("fixture.BufferZones", GpRiskTier.Standard)]
    [InlineData("fixture.FastRaster", GpRiskTier.Standard)]
    [InlineData("fixture.CountRecords", GpRiskTier.Standard)]
    [InlineData("fixture.EraseRows", GpRiskTier.Destructive)]
    [InlineData("fixture.AppendRecords", GpRiskTier.Destructive)]
    [InlineData("fixture.CalcExpression", GpRiskTier.UserCode)]
    [InlineData("fixture.GeocodeOnline", GpRiskTier.ConsumesCredits)]
    [InlineData("fixture.ScriptSummary", GpRiskTier.Standard)]
    [InlineData("fixture.PySummarize", GpRiskTier.Standard)]
    [InlineData("management.GetCount", GpRiskTier.ReadOnlyQuery)]
    [InlineData("management.Delete", GpRiskTier.Destructive)]
    public void System_tools_are_classified_by_attributes_names_and_parameters(string tool, GpRiskTier expected)
    {
        Assert.Equal(expected, Risk(tool).Tier);
        Assert.Equal(expected, SystemCatalog.Describe(tool)!.Tool.RiskTier);
    }

    [Fact]
    public void No_data_change_is_not_treated_as_read_only()
    {
        Assert.Contains("no_data_change", SystemCatalog.Describe("fixture.CountRecords")!.Attributes);
        Assert.Equal(GpRiskTier.Standard, Risk("fixture.CountRecords").Tier);
        Assert.Contains("no_data_change", SystemCatalog.Describe("management.Delete")!.Attributes);
        Assert.True(Risk("management.Delete").MutatesInput);
    }

    [Fact]
    public void Destructive_signals_are_input_data_change_edit_session_and_curated_names()
    {
        Assert.True(Risk("fixture.EraseRows").MutatesInput);
        Assert.Contains(Risk("fixture.EraseRows").Reasons, reason => reason.Contains("input_data_change", StringComparison.Ordinal));
        Assert.Contains(Risk("fixture.AppendRecords").Reasons, reason => reason.Contains("edit_session", StringComparison.Ordinal));
        Assert.Contains(Risk("management.Delete").Reasons, reason => reason.Contains("Curated", StringComparison.Ordinal));
        Assert.Contains("management.Rename", GeoprocessingRiskPolicy.CuratedDestructiveTools);
        Assert.Contains("MANAGEMENT.TRUNCATETABLE", GeoprocessingRiskPolicy.CuratedDestructiveTools);
    }

    [Fact]
    public void Raw_performance_attributes_do_not_change_the_tier()
    {
        var raster = SystemCatalog.Describe("fixture.FastRaster")!;

        Assert.Equal(["GPU", "mCPU", "spark", "output_no_memory", "overwrite_on"], raster.Attributes);
        Assert.False(raster.Risk.MutatesInput);
        Assert.False(raster.Risk.ConsumesCredits);
        Assert.False(raster.Risk.ExecutesUserCode);
    }

    [Fact]
    public void Credits_are_flagged()
    {
        var risk = Risk("fixture.GeocodeOnline");

        Assert.True(risk.ConsumesCredits);
        Assert.False(risk.MutatesInput);
    }

    [Fact]
    public void Python_expression_parameters_mark_user_code()
    {
        var risk = Risk("fixture.CalcExpression");

        Assert.True(risk.AcceptsPythonExpression);
        Assert.True(risk.ExecutesUserCode);
        Assert.Contains(risk.Reasons, reason => reason.Contains("expression_type", StringComparison.Ordinal));
    }

    [Fact]
    public void System_script_tools_are_informational_but_user_script_tools_are_user_code()
    {
        Assert.False(Risk("fixture.ScriptSummary").ExecutesUserCode);
        Assert.Contains(Risk("fixture.ScriptSummary").Reasons, reason => reason.Contains("informational", StringComparison.Ordinal));

        var directory = GeoprocessingFixtures.CreateTempDirectory();
        var atbx = GeoprocessingFixtures.CreateAtbx(directory);
        var user = new ToolboxCatalog(GeoprocessingFixtures.CreateTempDirectory(), [atbx]);

        Assert.Equal(GpRiskTier.UserCode, user.Describe("fixture.ScriptSummary")!.Risk.Tier);
        Assert.Equal(GpRiskTier.UserCode, user.Describe("fixture.PySummarize")!.Risk.Tier);
        Assert.True(user.Describe("fixture.PySummarize")!.Risk.ExecutesUserCode);
        Assert.Equal(GpRiskTier.Standard, user.Describe("fixture.BufferZones")!.Risk.Tier);
        Assert.True(user.Toolboxes.Single().MayExecuteUserCode);
    }

    [Fact]
    public void Read_only_allowlist_requires_a_system_tool()
    {
        var parameters = Array.Empty<GpParameter>();

        Assert.Equal(GpRiskTier.ReadOnlyQuery, GeoprocessingRiskPolicy.Assess("management.GetCount", "FunctionTool", true, [], parameters).Tier);
        Assert.Equal(GpRiskTier.Standard, GeoprocessingRiskPolicy.Assess("management.GetCount", "FunctionTool", false, [], parameters).Tier);
        Assert.Equal(GpRiskTier.Destructive, GeoprocessingRiskPolicy.Assess("management.GetCount", "FunctionTool", true, ["input_data_change"], parameters).Tier);
        Assert.True(GeoprocessingRiskPolicy.IsReadOnlyQuery(" management.getcellvalue "));
        Assert.False(GeoprocessingRiskPolicy.IsReadOnlyQuery("management.CopyFeatures"));
    }

    [Fact]
    public void Tool_level_user_code_outranks_destructive()
    {
        var risk = GeoprocessingRiskPolicy.Assess("user.Wipe", "ScriptTool", false, ["input_data_change"], []);

        Assert.Equal(GpRiskTier.UserCode, risk.Tier);
        Assert.True(risk.MutatesInput);
    }

    [Fact]
    public void Unindexed_toolboxes_execute_user_code()
    {
        var risk = GeoprocessingRiskPolicy.ForUnindexedToolbox(GpToolboxKind.PythonToolbox);

        Assert.Equal(GpRiskTier.UserCode, risk.Tier);
        Assert.True(risk.ExecutesUserCode);
    }
}
