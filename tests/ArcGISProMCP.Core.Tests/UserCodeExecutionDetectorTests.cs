using System.Text.Json;
using ArcGISProMCP.Core.Execution;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.Core.Tests;

public sealed class UserCodeExecutionDetectorTests
{
    private static readonly OperationDescriptor GpRun = Descriptor("gp.run", executesUserCode: true);
    private static readonly OperationDescriptor ArcPyRun = Descriptor("arcpy.run-script", executesUserCode: true);

    [Fact]
    public void System_tool_with_plain_parameters_is_not_flagged()
    {
        var arguments = Args(new { tool = "analysis.Buffer", parameters = new[] { "roads", "roads_buffer", "100 Meters" } });

        Assert.False(UserCodeExecutionDetector.RunsUserCode(GpRun, arguments));
        Assert.Null(UserCodeExecutionDetector.GetWarning(GpRun, arguments));
    }

    [Theory]
    [InlineData(@"C:\tools\Custom.pyt\MyTool")]
    [InlineData("C:/tools/Custom.pyt/MyTool")]
    [InlineData(@"C:\tools\Custom.PYT")]
    [InlineData(@"C:\tools\Scripts.atbx\Tool")]
    public void Custom_python_toolbox_is_flagged(string tool)
    {
        var arguments = Args(new { tool, parameters = Array.Empty<string>() });

        Assert.True(UserCodeExecutionDetector.RunsUserCode(GpRun, arguments));
        Assert.StartsWith("Runs user code:", UserCodeExecutionDetector.GetWarning(GpRun, arguments), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PYTHON3")]
    [InlineData("python")]
    [InlineData(" PYTHON_9.3 ")]
    public void Python_expression_type_parameter_is_flagged(string expressionType)
    {
        var arguments = Args(new { tool = "management.CalculateField", parameters = new[] { "parcels", "area", "!shape.area!", expressionType } });

        Assert.True(UserCodeExecutionDetector.RunsUserCode(GpRun, arguments));
    }

    [Fact]
    public void Nested_arcpy_reference_is_flagged()
    {
        var arguments = Args(new
        {
            tool = "management.CalculateField",
            parameters = new object[] { "parcels", "area", new[] { "arcpy.env.workspace" } }
        });

        Assert.True(UserCodeExecutionDetector.RunsUserCode(GpRun, arguments));
    }

    [Fact]
    public void Paths_that_merely_contain_python_are_not_flagged()
    {
        var arguments = Args(new { tool = "analysis.Clip", parameters = new[] { @"C:\python\data.gdb\roads", "out" } });

        Assert.False(UserCodeExecutionDetector.RunsUserCode(GpRun, arguments));
    }

    [Fact]
    public void ArcPy_operations_are_always_flagged()
    {
        var arguments = Args(new { scriptPath = "analysis.py", scriptSha256 = new string('a', 64) });

        Assert.True(UserCodeExecutionDetector.RunsUserCode(ArcPyRun, arguments));
        Assert.Contains("Python script", UserCodeExecutionDetector.GetWarning(ArcPyRun, arguments), StringComparison.Ordinal);
    }

    [Fact]
    public void Operations_that_do_not_declare_user_code_are_never_flagged()
    {
        var descriptor = Descriptor("layer.add", executesUserCode: false);
        var arguments = Args(new { tool = "Custom.pyt", parameters = new[] { "PYTHON3", "arcpy.x" } });

        Assert.False(UserCodeExecutionDetector.RunsUserCode(descriptor, arguments));
        Assert.False(UserCodeExecutionDetector.RunsUserCode(Descriptor("arcpy.inspect-script", executesUserCode: false), arguments));
    }

    [Fact]
    public void Non_object_arguments_are_not_flagged_for_gp()
    {
        Assert.False(UserCodeExecutionDetector.RunsUserCode(GpRun, JsonSerializer.SerializeToElement("PYTHON3")));
    }

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static OperationDescriptor Descriptor(string id, bool executesUserCode) => OperationDescriptor.Create(
        id, id, "Detector test.", JsonSchemas.EmptyObject,
        risk: OperationRisk.ExternalSideEffect, executesUserCode: executesUserCode);
}
