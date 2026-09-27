namespace ArcGISProMCP.Core.Tests;

public sealed class GeoprocessingOperationSourceTests
{
    [Fact]
    public void Geoprocessing_is_bounded_confirmed_and_returns_complete_result_evidence()
    {
        var source = ReadSource("GeoprocessingOperations.cs");

        Assert.Contains("\"gp.run\"", source, StringComparison.Ordinal);
        Assert.Contains("OperationRisk.ExternalSideEffect", source, StringComparison.Ordinal);
        Assert.Contains("requiresConfirmation: true", source, StringComparison.Ordinal);
        Assert.Contains("executesUserCode: true", source, StringComparison.Ordinal);
        Assert.Contains("UserCodeExecutionDetector.RunsUserCode(Descriptor, arguments)", source, StringComparison.Ordinal);
        Assert.Contains("UserCodeExecutionDetector.NoticeCode", source, StringComparison.Ordinal);
        Assert.Contains("MaximumParameterCount = 256", source, StringComparison.Ordinal);
        Assert.Contains("MaximumEnvironmentCount = 128", source, StringComparison.Ordinal);
        Assert.Contains("MaximumValueLength = 32_768", source, StringComparison.Ordinal);
        Assert.Contains("request.Environments", source, StringComparison.Ordinal);
        Assert.Contains("GPExecuteToolFlags.GPThread", source, StringComparison.Ordinal);
        Assert.Contains("result.Values", source, StringComparison.Ordinal);
        Assert.Contains("result.ValueTypes", source, StringComparison.Ordinal);
        Assert.Contains("elapsedMilliseconds", source, StringComparison.Ordinal);
        Assert.Contains("geoprocessing_warning", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Overwrite_is_explicit_and_does_not_inherit_an_ambient_setting_silently()
    {
        var source = ReadSource("GeoprocessingOperations.cs");

        Assert.Contains("\"overwriteOutput\"", source, StringComparison.Ordinal);
        Assert.Contains("environments[\"overwriteoutput\"]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GPExecuteToolFlags.InheritGPOptions", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_uses_the_catalog_risk_tier_for_autonomous_refusal_warnings_notices_and_dry_runs()
    {
        var source = ReadSource("GeoprocessingOperations.cs");

        Assert.Contains("GeoprocessingRunOperation(ToolboxCatalog catalog)", source, StringComparison.Ordinal);
        Assert.Contains("IDryRunnableOperation, IUnattendedExecutionGate, IApprovalWarningSource", source, StringComparison.Ordinal);
        Assert.Contains("GeoprocessingRunPolicy.UnattendedRefusal(request.Tool, risk, userCode)", source, StringComparison.Ordinal);
        Assert.Contains("GeoprocessingRunPolicy.ResultNotices(request.Tool, risk)", source, StringComparison.Ordinal);
        Assert.Contains("GeoprocessingRunPolicy.ApprovalWarning(", source, StringComparison.Ordinal);
        Assert.Contains("GeoprocessingRunPolicy.DryRun(", source, StringComparison.Ordinal);
        Assert.Contains("IAutonomousExecutionPolicy { AllowsUnattendedRiskyOperations: true }", source, StringComparison.Ordinal);
        // The dry run must not reach the geoprocessing engine.
        var dryRun = source[source.IndexOf("public async Task<OperationResult> DryRunAsync", StringComparison.Ordinal)..
            source.IndexOf("public async ValueTask<OperationRefusal?> CheckUnattendedAsync", StringComparison.Ordinal)];
        Assert.DoesNotContain("ExecuteToolAsync", dryRun, StringComparison.Ordinal);
    }

    [Fact]
    public void Search_and_describe_are_read_only_catalog_lookups()
    {
        var source = ReadSource("GeoprocessingCatalogOperations.cs");

        Assert.Contains("\"gp.search\"", source, StringComparison.Ordinal);
        Assert.Contains("\"gp.describe\"", source, StringComparison.Ordinal);
        Assert.Contains("catalog.Search(query, limit)", source, StringComparison.Ordinal);
        Assert.Contains("catalog.Describe(tool)", source, StringComparison.Ordinal);
        Assert.Contains("signature = positional.Select(parameter => parameter.Name)", source, StringComparison.Ordinal);
        Assert.Contains("JsonSchemas.Object(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("requiresConfirmation", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OperationRisk.", source, StringComparison.Ordinal);
        Assert.Equal(1, Count(source, "Geoprocessing.ExecuteToolAsync"));
    }

    [Fact]
    public void Query_runs_only_resolved_allowlisted_system_tools_without_side_effect_flags()
    {
        var source = ReadSource("GeoprocessingCatalogOperations.cs");
        var query = source[source.IndexOf("internal sealed class GeoprocessingQueryOperation", StringComparison.Ordinal)..];

        Assert.Contains("\"gp.query\"", query, StringComparison.Ordinal);
        Assert.Contains("pattern: \"^[a-z0-9]+\\\\.[A-Za-z0-9]+$\"", query, StringComparison.Ordinal);
        Assert.Contains("GeoprocessingRunPolicy.ResolveQueryTool(catalog, toolName)", query, StringComparison.Ordinal);
        Assert.Contains("GpStaticValidator.Validate(description, parameters)", query, StringComparison.Ordinal);
        Assert.Contains("description.Tool.ExecutionName,", query, StringComparison.Ordinal);
        Assert.Contains("new(\"overwriteoutput\", \"false\")", query, StringComparison.Ordinal);
        Assert.Contains("GPExecuteToolFlags.GPThread).ConfigureAwait(false)", query, StringComparison.Ordinal);
        Assert.DoesNotContain("GPExecuteToolFlags.AddOutputsToMap", query, StringComparison.Ordinal);
        Assert.DoesNotContain("GPExecuteToolFlags.AddToHistory", query, StringComparison.Ordinal);
        Assert.DoesNotContain("GPExecuteToolFlags.RefreshProjectItems", query, StringComparison.Ordinal);
        Assert.DoesNotContain("GPExecuteToolFlags.InheritGPOptions", query, StringComparison.Ordinal);
        Assert.DoesNotContain("\"environments\"", query, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_registers_every_gp_operation_with_one_shared_toolbox_catalog()
    {
        var source = ReadSource("ProOperationCatalog.cs");

        Assert.Equal(1, Count(source, "ToolboxCatalog.Default"));
        Assert.Contains("new GeoprocessingSearchOperation(toolboxes)", source, StringComparison.Ordinal);
        Assert.Contains("new GeoprocessingDescribeOperation(toolboxes)", source, StringComparison.Ordinal);
        Assert.Contains("new GeoprocessingQueryOperation(toolboxes)", source, StringComparison.Ordinal);
        Assert.Contains("new GeoprocessingRunOperation(toolboxes)", source, StringComparison.Ordinal);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string ReadSource(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "ArcGISProMCP.AddIn",
                "Operations",
                fileName);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException($"Could not locate {fileName} from the test output tree.");
    }
}
