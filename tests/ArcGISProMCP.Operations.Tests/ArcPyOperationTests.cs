using System.Text.Json;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Operations.Tests.Fakes;

namespace ArcGISProMCP.Operations.Tests;

public sealed class ArcPyOperationTests
{
    [Fact]
    public void ArcPy_operations_are_explicitly_high_risk_hash_pinned_and_bounded()
    {
        using var arcPy = new ArcPyRuntimeFixture();
        using var pro = new FakePro(arcPy: arcPy.Settings);
        var inspect = pro.Operation("arcpy.inspect-script").Descriptor;
        var run = pro.Operation("arcpy.run-script").Descriptor;

        Assert.Equal(OperationRisk.ReadOnly, inspect.Risk);
        Assert.False(inspect.RequiresConfirmation);
        Assert.False(inspect.ExecutesUserCode);
        Assert.Equal(OperationRisk.ExternalSideEffect, run.Risk);
        Assert.True(run.RequiresConfirmation);
        Assert.True(run.ExecutesUserCode);
        Assert.All([inspect, run], descriptor => Assert.Equal(ExecutionTarget.ExternalWorker, descriptor.ExecutionTarget));
        Assert.Single(pro.Registry.Descriptors, descriptor => descriptor.Id.StartsWith("arcpy.", StringComparison.Ordinal) && descriptor.ExecutesUserCode);

        var properties = run.InputSchema.GetProperty("properties");
        Assert.Equal(["scriptPath", "scriptSha256", "arguments", "timeoutSeconds"], properties.EnumerateObject().Select(property => property.Name));
        Assert.Equal("^[A-Fa-f0-9]{64}$", properties.GetProperty("scriptSha256").GetProperty("pattern").GetString());
        Assert.Equal(900, properties.GetProperty("timeoutSeconds").GetProperty("maximum").GetInt32());
        Assert.Equal(64, properties.GetProperty("arguments").GetProperty("maxItems").GetInt32());
        Assert.Equal(["scriptPath", "scriptSha256"], run.InputSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.False(run.InputSchema.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void ArcPy_operations_are_registered_only_with_explicit_settings()
    {
        using var pro = new FakePro();

        Assert.DoesNotContain(pro.Registry.Descriptors, descriptor => descriptor.Id.StartsWith("arcpy.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inspect_returns_the_relative_path_hash_and_size_without_running_anything()
    {
        using var arcPy = new ArcPyRuntimeFixture();
        using var pro = new FakePro(arcPy: arcPy.Settings);
        var hash = arcPy.WriteScript("analysis/count.py", "import arcpy\n");

        var result = await pro.RunAsync("arcpy.inspect-script", """{"scriptPath": "analysis/count.py"}""");

        Assert.True(result.Success);
        var data = result.Data!.Value;
        Assert.Equal(Path.Combine("analysis", "count.py"), data.GetProperty("scriptPath").GetString());
        Assert.Equal(hash, data.GetProperty("Sha256").GetString());
        Assert.Equal(13, data.GetProperty("sizeBytes").GetInt32());
        Assert.Equal(0, pro.Dispatcher.MainCimCalls + pro.Dispatcher.UiCalls);
    }

    [Fact]
    public async Task Policy_rejections_become_failed_results_with_the_policy_code()
    {
        using var arcPy = new ArcPyRuntimeFixture();
        using var pro = new FakePro(arcPy: arcPy.Settings);
        arcPy.WriteScript("run.py", "print('safe')\n");

        var escaped = await pro.RunAsync("arcpy.inspect-script", """{"scriptPath": "../outside.py"}""");
        var changed = await pro.RunAsync("arcpy.run-script", JsonSerializer.Serialize(new { scriptPath = "run.py", scriptSha256 = new string('0', 64) }));

        Assert.False(escaped.Success);
        Assert.Equal("arcpy_script_rejected", escaped.ErrorCode);
        Assert.Equal("rev-0", escaped.WorkspaceRevision);
        Assert.False(changed.Success);
        Assert.Equal("arcpy_script_changed", changed.ErrorCode);
        Assert.Null(changed.Data);
    }

    [Fact]
    public async Task A_python_that_cannot_start_fails_with_bounded_evidence()
    {
        using var arcPy = new ArcPyRuntimeFixture();
        using var pro = new FakePro(arcPy: arcPy.Settings);
        var hash = arcPy.WriteScript("run.py", "print('safe')\n");

        var result = await pro.RunAsync("arcpy.run-script", JsonSerializer.Serialize(new
        {
            scriptPath = "run.py",
            scriptSha256 = hash.ToLowerInvariant(),
            arguments = new[] { "--quiet" },
            timeoutSeconds = 30
        }));

        Assert.False(result.Success);
        Assert.Equal("arcpy_start_failed", result.ErrorCode);
        Assert.Equal("ArcGIS Pro Python could not be started.", result.Message);
        var data = result.Data!.Value;
        Assert.Equal("start_failed", data.GetProperty("outcome").GetString());
        Assert.Equal(hash, data.GetProperty("scriptSha256").GetString());
        Assert.Equal(30, data.GetProperty("timeoutSeconds").GetInt32());
        Assert.Equal(
            ["outcome", "ExitCode", "stdout", "stderr", "stdoutTruncated", "stderrTruncated", "durationMilliseconds", "scriptSha256", "timeoutSeconds"],
            data.EnumerateObject().Select(property => property.Name));
        // ExternalSideEffect writes still publish a new revision.
        Assert.Equal("rev-1", result.WorkspaceRevision);
    }
}
