namespace ArcGISProMCP.Core.Tests;

/// <summary>
/// Process launch hardening is only observable by starting a real Python, so it stays a
/// source-level check. Descriptors are tested in Operations.Tests and path/hash policy by
/// ArcPyExecutionPolicyTests.
/// </summary>
public sealed class ArcPyOperationSourceTests
{
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
