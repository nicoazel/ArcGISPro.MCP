using System.IO.Compression;
using ArcGISProMCP.Core.Geoprocessing;

namespace ArcGISProMCP.Core.Tests;

public sealed class GeoprocessingRunPolicyTests
{
    private static readonly ToolboxCatalog Catalog = new(GeoprocessingFixtures.Root);

    [Theory]
    [InlineData("fixture.EraseRows")]
    [InlineData("management.Delete")]
    [InlineData("fixture.AppendRecords")]
    [InlineData("fixture.CalcExpression")]
    [InlineData("C:\\tools\\Custom.pyt\\DoThing")]
    [InlineData("C:\\tools\\Custom.atbx\\DoThing")]
    public void Autonomous_mode_refuses_destructive_and_user_code_tools(string tool)
    {
        var refusal = GeoprocessingRunPolicy.UnattendedRefusal(tool, GeoprocessingRunPolicy.Assess(Catalog, tool), userCodeDetected: false);

        Assert.NotNull(refusal);
        Assert.Equal(GeoprocessingRunPolicy.DestructiveToolRequiresReviewCode, refusal.Code);
        Assert.Contains(tool, refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fixture.BufferZones")]
    [InlineData("fixture.GeocodeOnline")]
    [InlineData("management.GetCount")]
    public void Autonomous_mode_allows_standard_credit_and_query_tools(string tool)
    {
        Assert.Null(GeoprocessingRunPolicy.UnattendedRefusal(tool, GeoprocessingRunPolicy.Assess(Catalog, tool), userCodeDetected: false));
    }

    [Fact]
    public void Autonomous_mode_fails_closed_on_tools_it_cannot_classify()
    {
        var refusal = GeoprocessingRunPolicy.UnattendedRefusal("unknown.Tool", GeoprocessingRunPolicy.Assess(Catalog, "unknown.Tool"), userCodeDetected: false);

        Assert.NotNull(refusal);
        Assert.Equal(GeoprocessingRunPolicy.DestructiveToolRequiresReviewCode, refusal.Code);
        Assert.Contains("could not be classified", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("review is required", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_tools_are_refused_in_autonomous_mode_but_allowed_interactively_with_approval()
    {
        var interactive = GeoprocessingRunPolicy.DryRun(Catalog, "unknown.Tool", ["x"],
            userCodeDetected: false, requiresConfirmation: true, autonomousMode: false);
        var autonomous = GeoprocessingRunPolicy.DryRun(Catalog, "unknown.Tool", ["x"],
            userCodeDetected: false, requiresConfirmation: true, autonomousMode: true);

        Assert.Null(interactive.RiskTier);
        Assert.False(interactive.WouldBeRefused);
        Assert.True(interactive.RequiresConfirmation);
        Assert.True(autonomous.WouldBeRefused);
        Assert.Equal(GeoprocessingRunPolicy.DestructiveToolRequiresReviewCode, autonomous.UnattendedRefusal?.Code);
    }

    [Theory]
    [InlineData("management.Delete")]
    [InlineData("Delete_management")]
    [InlineData("MANAGEMENT.delete")]
    [InlineData("DeleteRows_management")]
    [InlineData("truncatetable_MANAGEMENT")]
    public void Curated_destructive_tools_are_refused_even_when_the_catalog_has_not_indexed_them(string tool)
    {
        var empty = new ToolboxCatalog(GeoprocessingFixtures.CreateTempDirectory());
        Assert.Null(empty.Describe(tool));

        var risk = GeoprocessingRunPolicy.Assess(empty, tool);
        var refusal = GeoprocessingRunPolicy.UnattendedRefusal(tool, risk, userCodeDetected: false);

        Assert.Equal(GpRiskTier.Destructive, risk?.Tier);
        Assert.True(risk!.MutatesInput);
        Assert.Equal(GeoprocessingRunPolicy.MutatesInputWarning, GeoprocessingRunPolicy.ApprovalWarning(risk));
        Assert.NotNull(refusal);
        Assert.Equal(GeoprocessingRunPolicy.DestructiveToolRequiresReviewCode, refusal.Code);
        Assert.Contains("Destructive", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Detected_user_code_is_refused_even_for_unknown_tools()
    {
        var refusal = GeoprocessingRunPolicy.UnattendedRefusal("mytools.MyTool", null, userCodeDetected: true);

        Assert.Equal(GeoprocessingRunPolicy.DestructiveToolRequiresReviewCode, refusal?.Code);
    }

    [Fact]
    public void Destructive_approval_warning_says_it_modifies_input_in_place()
    {
        var warning = GeoprocessingRunPolicy.ApprovalWarning(GeoprocessingRunPolicy.Assess(Catalog, "fixture.EraseRows"));

        Assert.Equal(GeoprocessingRunPolicy.MutatesInputWarning, warning);
        Assert.Contains("Modifies/deletes input data in place", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Credit_approval_warning_says_it_consumes_credits()
    {
        var warning = GeoprocessingRunPolicy.ApprovalWarning(GeoprocessingRunPolicy.Assess(Catalog, "fixture.GeocodeOnline"));

        Assert.Contains("Consumes ArcGIS Online credits", warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fixture.BufferZones")]
    [InlineData("unknown.Tool")]
    public void Standard_and_unknown_tools_have_no_risk_warning(string tool)
    {
        Assert.Null(GeoprocessingRunPolicy.ApprovalWarning(GeoprocessingRunPolicy.Assess(Catalog, tool)));
    }

    [Fact]
    public void Result_notices_flag_mutation_credits_and_unknown_tools()
    {
        Assert.Equal([GeoprocessingRunPolicy.MutatesInputNoticeCode],
            GeoprocessingRunPolicy.ResultNotices("fixture.EraseRows", GeoprocessingRunPolicy.Assess(Catalog, "fixture.EraseRows")).Select(notice => notice.Code));
        Assert.Equal([GeoprocessingRunPolicy.ConsumesCreditsNoticeCode],
            GeoprocessingRunPolicy.ResultNotices("fixture.GeocodeOnline", GeoprocessingRunPolicy.Assess(Catalog, "fixture.GeocodeOnline")).Select(notice => notice.Code));
        Assert.Equal([GeoprocessingRunPolicy.ToolNotIndexedNoticeCode],
            GeoprocessingRunPolicy.ResultNotices("unknown.Tool", null).Select(notice => notice.Code));
        Assert.Empty(GeoprocessingRunPolicy.ResultNotices("fixture.BufferZones", GeoprocessingRunPolicy.Assess(Catalog, "fixture.BufferZones")));
    }

    [Fact]
    public void Dry_run_reports_validation_tier_and_flags_for_a_valid_request()
    {
        var report = GeoprocessingRunPolicy.DryRun(Catalog, "fixture.BufferZones",
            ["roads", "memory\\zones", "10 Meters", "left", "DISSOLVE", "a;b", "12", "0.5", "1;6", "#"],
            userCodeDetected: false, requiresConfirmation: true, autonomousMode: false);

        Assert.True(report.Valid);
        Assert.True(report.Validation.Validated);
        Assert.Equal(GpRiskTier.Standard, report.RiskTier);
        Assert.True(report.RequiresConfirmation);
        Assert.False(report.ExecutesUserCode);
        Assert.Null(report.UnattendedRefusal);
    }

    [Fact]
    public void Dry_run_reports_static_errors()
    {
        var report = GeoprocessingRunPolicy.DryRun(Catalog, "fixture.BufferZones", ["roads"],
            userCodeDetected: false, requiresConfirmation: true, autonomousMode: false);

        Assert.False(report.Valid);
        Assert.Contains(report.Validation.Issues, issue => issue.Code == "required_parameter_missing");
    }

    [Fact]
    public void Dry_run_of_a_destructive_tool_is_invalid_only_in_autonomous_mode()
    {
        var interactive = GeoprocessingRunPolicy.DryRun(Catalog, "fixture.EraseRows", ["parcels"],
            userCodeDetected: false, requiresConfirmation: true, autonomousMode: false);
        var autonomous = GeoprocessingRunPolicy.DryRun(Catalog, "fixture.EraseRows", ["parcels"],
            userCodeDetected: false, requiresConfirmation: true, autonomousMode: true);

        Assert.True(interactive.Valid);
        Assert.False(interactive.WouldBeRefused);
        Assert.NotNull(interactive.UnattendedRefusal);
        Assert.Equal(GeoprocessingRunPolicy.MutatesInputWarning, interactive.ApprovalWarning);
        Assert.False(autonomous.Valid);
        Assert.True(autonomous.WouldBeRefused);
        Assert.Equal(GpRiskTier.Destructive, autonomous.RiskTier);
    }

    [Fact]
    public void Dry_run_of_a_toolbox_path_is_unvalidated_user_code()
    {
        var report = GeoprocessingRunPolicy.DryRun(Catalog, "C:\\tools\\Custom.pyt\\DoThing", ["x"],
            userCodeDetected: true, requiresConfirmation: true, autonomousMode: false);

        Assert.False(report.Validation.Validated);
        Assert.True(report.ExecutesUserCode);
        Assert.Equal(GpRiskTier.UserCode, report.RiskTier);
    }

    [Fact]
    public void Query_resolves_allowlisted_system_tools()
    {
        var resolution = GeoprocessingRunPolicy.ResolveQueryTool(Catalog, "management.GetCount");

        Assert.True(resolution.Succeeded);
        Assert.Equal("management.GetCount", resolution.Tool!.Tool.ExecutionName);
        Assert.Equal(GpRiskTier.ReadOnlyQuery, resolution.Tool.Risk.Tier);
    }

    [Theory]
    [InlineData("")]
    [InlineData("GetCount_management")]
    [InlineData("Management.GetCount")]
    [InlineData("management.GetCount\n")]
    [InlineData("management.Get_Count")]
    [InlineData("C:\\tools\\x.atbx\\GetCount")]
    [InlineData("a.b.GetCount")]
    public void Query_rejects_names_that_are_not_plain_alias_dot_name(string tool)
    {
        Assert.Equal("invalid_tool_name", GeoprocessingRunPolicy.ResolveQueryTool(Catalog, tool).ErrorCode);
    }

    [Theory]
    [InlineData("management.Delete")]
    [InlineData("fixture.BufferZones")]
    [InlineData("fixture.CountRecords")]
    public void Query_rejects_tools_outside_the_allowlist(string tool)
    {
        Assert.Equal("tool_not_query_allowed", GeoprocessingRunPolicy.ResolveQueryTool(Catalog, tool).ErrorCode);
    }

    [Fact]
    public void Query_does_not_resolve_allowlisted_names_from_user_toolboxes()
    {
        var directory = GeoprocessingFixtures.CreateTempDirectory();
        var atbx = Path.Combine(directory, "shadow.atbx");
        ZipFile.CreateFromDirectory(Path.Combine(GeoprocessingFixtures.Root, "mgmt.tbx"), atbx, CompressionLevel.Fastest, includeBaseDirectory: false);
        var userOnly = new ToolboxCatalog(GeoprocessingFixtures.CreateTempDirectory(), [atbx]);
        Assert.NotNull(userOnly.Describe("management.GetCount"));

        var resolution = GeoprocessingRunPolicy.ResolveQueryTool(userOnly, "management.GetCount");

        Assert.False(resolution.Succeeded);
        Assert.Equal("tool_not_found", resolution.ErrorCode);
    }
}
