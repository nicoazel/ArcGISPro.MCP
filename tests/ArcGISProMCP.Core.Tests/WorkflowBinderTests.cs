using System.Collections.Immutable;
using System.Text.Json;
using ArcGISProMCP.Core.Workflows;
using Xunit;

namespace ArcGISProMCP.Core.Tests;

public sealed class WorkflowBinderTests
{
    [Fact]
    public void Binds_typed_parameters_into_nested_step_arguments()
    {
        var workflow = Workflow(
            new WorkflowParameter("source", "path", true, null, null),
            new WorkflowParameter("visible", "boolean", false, JsonSerializer.SerializeToElement(true), null));
        var supplied = JsonSerializer.SerializeToElement(new { source = "C:\\data\\site.gdb\\Parcels" });
        var parameters = WorkflowBinder.BindParameters(workflow, supplied);
        var template = JsonSerializer.SerializeToElement(new
        {
            source = "${parameters.source}",
            options = new { visible = "${parameters.visible}" }
        });

        var resolved = WorkflowBinder.ResolveArguments(template, parameters);

        Assert.Equal("C:\\data\\site.gdb\\Parcels", resolved.GetProperty("source").GetString());
        Assert.True(resolved.GetProperty("options").GetProperty("visible").GetBoolean());
    }

    [Fact]
    public void Rejects_unknown_and_missing_parameters()
    {
        var workflow = Workflow(new WorkflowParameter("source", "string", true, null, null));

        Assert.Throws<ArgumentException>(() => WorkflowBinder.BindParameters(
            workflow, JsonSerializer.SerializeToElement(new { extra = "value" })));
        Assert.Throws<ArgumentException>(() => WorkflowBinder.BindParameters(
            workflow, JsonSerializer.SerializeToElement(new { })));
    }

    private static WorkflowDefinition Workflow(params WorkflowParameter[] parameters) => new(
        "workflow.test",
        "1.0.0",
        "Test",
        "Test workflow",
        ImmutableHashSet<string>.Empty,
        ImmutableHashSet<string>.Empty,
        parameters.ToImmutableArray(),
        ImmutableArray<WorkflowStep>.Empty);
}
