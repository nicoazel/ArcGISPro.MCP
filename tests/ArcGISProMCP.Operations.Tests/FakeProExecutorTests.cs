using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Operations.Tests.Fakes;

namespace ArcGISProMCP.Operations.Tests;

/// <summary>The fake catalog behind the real executor: argument validation, revisions, approval and audit.</summary>
public sealed class FakeProExecutorTests
{
    [Fact]
    public async Task A_write_runs_through_the_executor_with_the_current_revision_and_is_audited()
    {
        using var pro = new FakePro();

        var result = await pro.InvokeAsync("map.ensure", """{"name": "Transit", "type": "scene"}""");

        Assert.True(result.Success, result.Message);
        Assert.Equal("rev-1", result.WorkspaceRevision);
        Assert.Equal("Transit", Assert.Single(pro.State.Maps).Name);
        var audit = Assert.Single(pro.Audit.Events);
        Assert.Equal("map.ensure", audit.OperationId);
        Assert.Equal("rev-0", audit.StartRevision);
        Assert.Equal("rev-1", audit.EndRevision);
    }

    [Fact]
    public async Task Arguments_are_validated_against_the_real_input_schema_before_ArcGIS_is_touched()
    {
        using var pro = new FakePro();

        var result = await pro.InvokeAsync("map.ensure", """{"name": "Transit", "type": "cube"}""");

        Assert.False(result.Success);
        Assert.Equal("invalid_arguments", result.ErrorCode);
        Assert.Empty(pro.State.Maps);
        Assert.Equal(0, pro.Dispatcher.MainCimCalls);
    }

    [Fact]
    public async Task Confirmation_gated_operations_need_an_approval_token()
    {
        using var pro = new FakePro();
        var revision = pro.Workspace.Revision;

        var result = await pro.Executor.ExecuteAsync(
            new OperationRequest("project.save", FakePro.Arguments("{}"), revision), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("confirmation_required", result.ErrorCode);
        Assert.Empty(pro.State.Calls);
    }
}
