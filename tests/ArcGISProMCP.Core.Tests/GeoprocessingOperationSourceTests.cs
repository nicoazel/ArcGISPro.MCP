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
