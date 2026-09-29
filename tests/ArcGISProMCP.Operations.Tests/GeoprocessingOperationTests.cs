using System.Text.Json;
using ArcGISProMCP.Core.Execution;
using ArcGISProMCP.Core.Geoprocessing;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Operations.Services;
using ArcGISProMCP.Operations.Tests.Fakes;

namespace ArcGISProMCP.Operations.Tests;

/// <summary>
/// gp.search/describe/query/run against the synthetic toolboxes shared with Core.Tests and a fake
/// geoprocessing engine that records every execution.
/// </summary>
public sealed class GeoprocessingOperationTests
{
    private const string BufferParameters = """["roads", "memory\\zones", "10 Meters", "left", "DISSOLVE", "a;b", "12", "0.5", "1;6", "#"]""";

    [Fact]
    public void Run_is_confirmed_user_code_capable_and_catalog_lookups_are_read_only()
    {
        using var pro = new FakePro();
        var run = pro.Operation("gp.run").Descriptor;

        Assert.Equal(OperationRisk.ExternalSideEffect, run.Risk);
        Assert.True(run.RequiresConfirmation);
        Assert.True(run.ExecutesUserCode);
        Assert.Equal(ExecutionTarget.Background, run.ExecutionTarget);
        Assert.IsAssignableFrom<IDryRunnableOperation>(pro.Operation("gp.run"));
        Assert.IsAssignableFrom<IUnattendedExecutionGate>(pro.Operation("gp.run"));
        Assert.IsAssignableFrom<IApprovalWarningSource>(pro.Operation("gp.run"));
        foreach (var id in new[] { "gp.search", "gp.describe", "gp.query" })
        {
            var descriptor = pro.Operation(id).Descriptor;
            Assert.Equal(OperationRisk.ReadOnly, descriptor.Risk);
            Assert.False(descriptor.RequiresConfirmation, id);
            Assert.False(descriptor.ExecutesUserCode, id);
        }

        var query = pro.Operation("gp.query").Descriptor.InputSchema.GetProperty("properties");
        Assert.Equal("^[a-z0-9]+\\.[A-Za-z0-9]+$", query.GetProperty("tool").GetProperty("pattern").GetString());
        Assert.False(query.TryGetProperty("environments", out _));
    }

    [Fact]
    public void Request_parsing_converts_values_and_makes_overwrite_explicit()
    {
        var request = GeoprocessingRequest.Parse(FakePro.Arguments("""
            {"tool": "  analysis.Buffer  ", "parameters": ["roads", null, true, false, 12.50, ["a", 1, null]],
             "environments": {"workspace": "C:\\data.gdb", "cellSize": 10}, "overwriteOutput": true}
            """));

        Assert.Equal("analysis.Buffer", request.Tool);
        Assert.Equal(["roads", "#", "true", "false", "12.50", "a;1;#"], request.Parameters);
        Assert.Equal(true, request.OverwriteOutput);
        Assert.Equal(
            [KeyValuePair.Create("workspace", "C:\\data.gdb"), KeyValuePair.Create("cellSize", "10"), KeyValuePair.Create("overwriteoutput", "true")],
            request.Environments);
    }

    [Fact]
    public void Without_overwriteOutput_no_overwrite_environment_is_sent()
    {
        var request = GeoprocessingRequest.Parse(FakePro.Arguments("""{"tool": "analysis.Buffer", "parameters": []}"""));

        Assert.Null(request.OverwriteOutput);
        Assert.Empty(request.Environments);
    }

    [Theory]
    [InlineData("""{"parameters": []}""", "Argument 'tool' is required.")]
    [InlineData("""{"tool": "   ", "parameters": []}""", "tool must be a single nonempty toolbox-qualified name")]
    [InlineData("""{"tool": "analysis.Buffer\nmanagement.Delete", "parameters": []}""", "tool must be a single nonempty toolbox-qualified name")]
    [InlineData("""{"tool": "analysis.Buffer", "parameters": {}}""", "parameters must be a JSON array.")]
    [InlineData("""{"tool": "analysis.Buffer", "parameters": [], "environments": []}""", "environments must be a JSON object.")]
    [InlineData("""{"tool": "analysis.Buffer", "parameters": [], "environments": {" ": "x"}}""", "environment names must contain 1 to 128 characters.")]
    [InlineData("""{"tool": "analysis.Buffer", "parameters": [], "overwriteOutput": "yes"}""", "overwriteOutput must be a boolean.")]
    public void Malformed_requests_are_rejected(string arguments, string message)
    {
        var exception = Assert.Throws<ArgumentException>(() => GeoprocessingRequest.Parse(FakePro.Arguments(arguments)));

        Assert.StartsWith(message, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Requests_are_bounded()
    {
        static JsonElement Parameters(int count) => FakePro.Arguments(JsonSerializer.Serialize(new { tool = "analysis.Buffer", parameters = Enumerable.Repeat("x", count) }));
        static JsonElement Environments(int count) => FakePro.Arguments(JsonSerializer.Serialize(new
        {
            tool = "analysis.Buffer",
            parameters = Array.Empty<string>(),
            environments = Enumerable.Range(0, count).ToDictionary(index => $"env{index}", _ => "x")
        }));

        Assert.Equal(256, GeoprocessingRequest.Parse(Parameters(256)).Parameters.Length);
        Assert.StartsWith("parameters cannot contain more than 256 values.",
            Assert.Throws<ArgumentException>(() => GeoprocessingRequest.Parse(Parameters(257))).Message, StringComparison.Ordinal);
        Assert.Equal(128, GeoprocessingRequest.Parse(Environments(128)).Environments.Length);
        Assert.StartsWith("environments cannot contain more than 128 values.",
            Assert.Throws<ArgumentException>(() => GeoprocessingRequest.Parse(Environments(129))).Message, StringComparison.Ordinal);
        Assert.Equal(32_768, GeoprocessingRequest.ToGpValue(FakePro.Arguments(JsonSerializer.Serialize(new string('a', 32_768)))).Length);
        Assert.StartsWith("A geoprocessing parameter or environment value exceeds 32768 characters.",
            Assert.Throws<ArgumentException>(() => GeoprocessingRequest.ToGpValue(FakePro.Arguments(JsonSerializer.Serialize(new string('a', 32_769))))).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_executes_on_the_GP_thread_with_explicit_environments_and_default_flags()
    {
        using var pro = new FakePro();
        pro.Geoprocessing.NextResult = FakeGeoprocessingService.Succeeded(
            new GeoprocessingMessage("Informative", "Start Time: now", 0),
            new GeoprocessingMessage("Warning", "Output has no features.", 000117));

        var result = await pro.RunAsync("gp.run", $$"""
            {"tool": "fixture.BufferZones", "parameters": {{BufferParameters}}, "environments": {"workspace": "memory"}, "overwriteOutput": false}
            """);

        Assert.True(result.Success, result.Message);
        var call = Assert.Single(pro.Geoprocessing.Calls);
        Assert.Equal("fixture.BufferZones", call.Tool);
        Assert.Equal(10, call.Parameters.Count);
        Assert.Equal([KeyValuePair.Create("workspace", "memory"), KeyValuePair.Create("overwriteoutput", "false")], call.Environments);
        Assert.Equal(new GeoprocessingExecutionFlags(AddOutputsToMap: true, AddToHistory: true, RefreshProjectItems: true), call.Flags);
        var data = result.Data!.Value;
        Assert.Equal("fixture.BufferZones", data.GetProperty("tool").GetString());
        Assert.False(data.GetProperty("IsFailed").GetBoolean());
        Assert.Equal(["memory/result"], data.GetProperty("values").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(["DEFeatureClass"], data.GetProperty("valueTypes").EnumerateArray().Select(value => value.GetString()));
        Assert.True(data.GetProperty("elapsedMilliseconds").GetInt64() >= 0);
        Assert.Equal("Standard", data.GetProperty("riskTier").GetString());
        Assert.False(data.GetProperty("flags").GetProperty("overwriteOutput").GetBoolean());
        Assert.Equal("Warning", data.GetProperty("messages")[1].GetProperty("type").GetString());
        var warning = Assert.Single(result.Notices);
        Assert.Equal("geoprocessing_warning", warning.Code);
        Assert.Equal("Output has no features.", warning.Message);
        Assert.Equal("rev-1", result.WorkspaceRevision);
    }

    [Fact]
    public async Task Run_flags_can_turn_off_history_refresh_and_map_output()
    {
        using var pro = new FakePro();

        var result = await pro.RunAsync("gp.run", $$"""
            {"tool": "fixture.BufferZones", "parameters": {{BufferParameters}}, "addOutputsToMap": false, "addToHistory": false, "refreshProjectItems": false}
            """);

        Assert.Equal(GeoprocessingExecutionFlags.None, Assert.Single(pro.Geoprocessing.Calls).Flags);
        var flags = result.Data!.Value.GetProperty("flags");
        Assert.False(flags.GetProperty("addOutputsToMap").GetBoolean());
        Assert.False(flags.GetProperty("addToHistory").GetBoolean());
        Assert.False(flags.GetProperty("refreshProjectItems").GetBoolean());
        Assert.Equal(JsonValueKind.Null, flags.GetProperty("overwriteOutput").ValueKind);
    }

    [Fact]
    public async Task Failed_and_cancelled_tools_fail_with_the_first_error_and_complete_evidence()
    {
        using var pro = new FakePro();
        pro.Geoprocessing.NextResult = FakeGeoprocessingService.Failed("ERROR 000732: Input Features: Dataset roads does not exist.");

        var failed = await pro.RunAsync("gp.run", """{"tool": "fixture.EraseRows", "parameters": ["parcels"]}""");
        pro.Geoprocessing.NextResult = FakeGeoprocessingService.Succeeded() with { IsCanceled = true };
        var cancelled = await pro.RunAsync("gp.run", """{"tool": "fixture.BufferZones", "parameters": []}""");

        Assert.False(failed.Success);
        Assert.Equal("geoprocessing_failed", failed.ErrorCode);
        Assert.Equal("ERROR 000732: Input Features: Dataset roads does not exist.", failed.Message);
        Assert.True(failed.Data!.Value.GetProperty("IsFailed").GetBoolean());
        Assert.Contains(failed.Notices, notice => notice.Code == GeoprocessingRunPolicy.MutatesInputNoticeCode);
        Assert.Equal("geoprocessing_cancelled", cancelled.ErrorCode);
        Assert.Equal("Geoprocessing tool 'fixture.BufferZones' did not complete.", cancelled.Message);
    }

    [Fact]
    public async Task Python_expressions_add_the_user_code_notice()
    {
        using var pro = new FakePro();

        var result = await pro.RunAsync("gp.run", """{"tool": "management.CalculateField", "parameters": ["parcels", "AREA", "!shape.area!", "PYTHON3"]}""");

        Assert.Contains(result.Notices, notice => notice.Code == UserCodeExecutionDetector.NoticeCode);
        Assert.Contains(result.Notices, notice => notice.Code == GeoprocessingRunPolicy.ToolNotIndexedNoticeCode);
    }

    [Fact]
    public async Task A_dry_run_validates_statically_and_never_reaches_the_geoprocessing_engine()
    {
        using var pro = new FakePro();
        var operation = (IDryRunnableOperation)pro.Operation("gp.run");

        var valid = await operation.DryRunAsync(FakePro.Arguments($$"""{"tool": "fixture.BufferZones", "parameters": {{BufferParameters}}}"""), pro.Context, TestContext.Current.CancellationToken);
        var missing = await operation.DryRunAsync(FakePro.Arguments("""{"tool": "fixture.BufferZones", "parameters": ["roads"]}"""), pro.Context, TestContext.Current.CancellationToken);
        var malformed = await operation.DryRunAsync(FakePro.Arguments("""{"parameters": []}"""), pro.Context, TestContext.Current.CancellationToken);

        Assert.Empty(pro.Geoprocessing.Calls);
        Assert.True(valid.Data!.Value.GetProperty("valid").GetBoolean());
        Assert.True(valid.Data!.Value.GetProperty("dryRun").GetBoolean());
        Assert.Equal("Standard", valid.Data!.Value.GetProperty("riskTier").GetString());
        Assert.False(missing.Data!.Value.GetProperty("valid").GetBoolean());
        Assert.Contains(missing.Data!.Value.GetProperty("issues").EnumerateArray(),
            issue => issue.GetProperty("code").GetString() == "required_parameter_missing");
        Assert.Equal("invalid_arguments", malformed.ErrorCode);
    }

    [Fact]
    public async Task Autonomous_mode_refuses_unreviewed_destructive_tools_before_they_run()
    {
        using var pro = new FakePro(autonomous: true);
        var token = TestContext.Current.CancellationToken;
        const string Erase = """{"tool": "fixture.EraseRows", "parameters": ["parcels"]}""";

        var destructive = await pro.InvokeUnattendedAsync("gp.run", Erase, token);
        var standard = await pro.InvokeUnattendedAsync("gp.run", $$"""{"tool": "fixture.BufferZones", "parameters": {{BufferParameters}}}""", token);

        Assert.False(destructive.Success);
        Assert.Equal(GeoprocessingRunPolicy.DestructiveToolRequiresReviewCode, destructive.ErrorCode);
        Assert.Contains("approval_request", destructive.Message, StringComparison.Ordinal);
        Assert.True(standard.Success, standard.Message);
        Assert.Equal("fixture.BufferZones", Assert.Single(pro.Geoprocessing.Calls).Tool);

        // A person-issued token still lets the reviewed request run in autonomous mode.
        var reviewed = await pro.InvokeAsync("gp.run", Erase);
        Assert.True(reviewed.Success, reviewed.Message);
        Assert.Equal("fixture.EraseRows", pro.Geoprocessing.Calls[^1].Tool);
    }

    [Fact]
    public void Approval_cards_warn_about_in_place_changes_and_user_code()
    {
        using var pro = new FakePro();
        var warnings = (IApprovalWarningSource)pro.Operation("gp.run");

        Assert.Equal(GeoprocessingRunPolicy.MutatesInputWarning,
            warnings.GetApprovalWarning(FakePro.Arguments("""{"tool": "fixture.EraseRows", "parameters": ["parcels"]}""")));
        Assert.Null(warnings.GetApprovalWarning(FakePro.Arguments($$"""{"tool": "fixture.BufferZones", "parameters": {{BufferParameters}}}""")));
        Assert.Contains("Runs user code:",
            warnings.GetApprovalWarning(FakePro.Arguments("""{"tool": "C:\\tools\\Custom.pyt\\DoThing", "parameters": []}""")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_runs_only_allowlisted_system_tools_without_side_effect_flags()
    {
        using var pro = new FakePro();
        pro.Geoprocessing.NextResult = FakeGeoprocessingService.Succeeded() with { ReturnValue = "42", Values = ["42"], ValueTypes = ["GPLong"] };

        var result = await pro.RunAsync("gp.query", """{"tool": "management.GetCount", "parameters": ["parcels"]}""");

        Assert.True(result.Success, result.Message);
        var call = Assert.Single(pro.Geoprocessing.Calls);
        Assert.Equal("management.GetCount", call.Tool);
        Assert.Equal(["parcels"], call.Parameters);
        Assert.Equal([KeyValuePair.Create("overwriteoutput", "false")], call.Environments);
        Assert.Equal(GeoprocessingExecutionFlags.None, call.Flags);
        var data = result.Data!.Value;
        Assert.Equal("42", data.GetProperty("returnValue").GetString());
        Assert.False(data.GetProperty("flags").GetProperty("overwriteOutput").GetBoolean());
        Assert.Equal("rev-0", result.WorkspaceRevision);
    }

    [Theory]
    [InlineData("""{"tool": "fixture.EraseRows", "parameters": ["parcels"]}""", "tool_not_query_allowed")]
    [InlineData("""{"tool": "Management.GetCount", "parameters": ["parcels"]}""", "invalid_tool_name")]
    [InlineData("""{"tool": "management.GetCount", "parameters": []}""", "invalid_parameters")]
    public async Task Query_refuses_everything_else_without_running_it(string arguments, string code)
    {
        using var pro = new FakePro();

        var result = await pro.RunAsync("gp.query", arguments);

        Assert.False(result.Success);
        Assert.Equal(code, result.ErrorCode);
        Assert.Empty(pro.Geoprocessing.Calls);
    }

    [Fact]
    public async Task Query_parameters_are_bounded()
    {
        using var pro = new FakePro();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            pro.RunAsync("gp.query", JsonSerializer.Serialize(new { tool = "management.GetCount", parameters = Enumerable.Repeat("x", 33) })));

        Assert.StartsWith("parameters cannot contain more than 32 values.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_and_describe_are_catalog_lookups()
    {
        using var pro = new FakePro();

        var search = await pro.RunAsync("gp.search", """{"query": "buffer", "limit": 5}""");
        var describe = await pro.RunAsync("gp.describe", """{"tool": "fixture.BufferZones"}""");
        var missing = await pro.RunAsync("gp.describe", """{"tool": "fixture.BufferZone"}""");

        Assert.Contains(search.Data!.Value.GetProperty("tools").EnumerateArray(),
            hit => hit.GetProperty("tool").GetProperty("executionName").GetString() == "fixture.BufferZones");
        Assert.Equal("in_features", describe.Data!.Value.GetProperty("signature")[0].GetString());
        Assert.Equal("Standard", describe.Data!.Value.GetProperty("risk").GetProperty("tier").GetString());
        Assert.Equal("tool_not_found", missing.ErrorCode);
        Assert.Contains("fixture.BufferZones", missing.Message, StringComparison.Ordinal);
        Assert.Empty(pro.Geoprocessing.Calls);
    }
}
