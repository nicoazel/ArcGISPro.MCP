namespace ArcGISProMCP.Core.Tests;

public sealed class ArcPyOperationSourceTests
{
    [Fact]
    public void ArcPy_operation_is_explicitly_high_risk_hash_pinned_and_bounded()
    {
        var operation = ReadSource("ArcGISProMCP.AddIn", "Operations", "ArcPyOperations.cs");

        Assert.Contains("\"arcpy.inspect-script\"", operation, StringComparison.Ordinal);
        Assert.Contains("\"arcpy.run-script\"", operation, StringComparison.Ordinal);
        Assert.Contains("OperationRisk.ExternalSideEffect", operation, StringComparison.Ordinal);
        Assert.Contains("requiresConfirmation: true", operation, StringComparison.Ordinal);
        Assert.Contains("ExecutionTarget.ExternalWorker", operation, StringComparison.Ordinal);
        Assert.Contains("\"scriptSha256\"", operation, StringComparison.Ordinal);
        Assert.Contains("\"timeoutSeconds\"", operation, StringComparison.Ordinal);
        Assert.Contains("stdoutTruncated", operation, StringComparison.Ordinal);
        Assert.Contains("stderrTruncated", operation, StringComparison.Ordinal);
        Assert.Contains("processResult.ExitCode", operation, StringComparison.Ordinal);
        Assert.Contains("context.ApplicationStopping", operation, StringComparison.Ordinal);
        Assert.DoesNotContain("inlineScript", operation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("environmentVariables", operation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ArcPy_runner_has_no_shell_eval_path_and_terminates_process_trees()
    {
        var runner = ReadSource("ArcGISProMCP.Core", "Execution", "ArcPyProcessRunner.cs");

        Assert.Contains("UseShellExecute = false", runner, StringComparison.Ordinal);
        Assert.Contains("startInfo.ArgumentList.Add", runner, StringComparison.Ordinal);
        Assert.Contains("startInfo.Environment.Clear()", runner, StringComparison.Ordinal);
        Assert.Contains("startInfo.ArgumentList.Add(\"-I\")", runner, StringComparison.Ordinal);
        Assert.Contains("process.Kill(entireProcessTree: true)", runner, StringComparison.Ordinal);
        Assert.Contains("BoundedTextBuffer(settings.MaximumOutputCharacters)", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("cmd.exe", runner, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("powershell", runner, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-c\"", runner, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ArcPy_policy_rejects_traversal_links_and_content_changes()
    {
        var policy = ReadSource("ArcGISProMCP.Core", "Execution", "ArcPyExecutionPolicy.cs");

        Assert.Contains("Path.GetRelativePath", policy, StringComparison.Ordinal);
        Assert.Contains("FileAttributes.ReparsePoint", policy, StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.FixedTimeEquals", policy, StringComparison.Ordinal);
        Assert.Contains("FileShare.Read", policy, StringComparison.Ordinal);
        Assert.Contains("arcpy_init.py", policy, StringComparison.Ordinal);
        Assert.Contains("arcpy_script_changed", policy, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                new[] { directory.FullName, "src" }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(segments)} from the test output tree.");
    }
}
