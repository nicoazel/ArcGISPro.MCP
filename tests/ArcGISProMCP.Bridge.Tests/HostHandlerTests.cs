using System.Text.Json;
using ArcGISProMCP.AddIn.Bridge;
using ArcGISProMCP.AddIn.Services;
using ArcGISProMCP.Bridge.Protocol;
using ArcGISProMCP.Core.Approvals;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Registry;
using ArcGISProMCP.Core.Workflows;
using ArcGISProMCP.Core.Workspaces;
using Xunit;

namespace ArcGISProMCP.Bridge.Tests;

public sealed class HostHandlerTests
{
    [Fact]
    public async Task Approval_is_local_single_use_and_bound_to_revision()
    {
        using var fixture = new Fixture(OperationRisk.Destructive);
        var denied = await fixture.Call("registry.invoke", new { operationId = "test.write", arguments = new { }, expectedRevision = "r1", idempotencyKey = "same" });
        Assert.Equal("confirmation_required", denied.Result!.Value.GetProperty("errorCode").GetString());
        var stale = await fixture.Call("approval.request", new { operationId = "test.write", arguments = new { }, expectedRevision = "old" });
        Assert.Equal("workspace_revision_mismatch", stale.Error!.Code);
        var pending = await fixture.Call("approval.request", new { operationId = "test.write", arguments = new { }, expectedRevision = "r1" });
        var requestId = pending.Result!.Value.GetProperty("requestId").GetString()!;
        Assert.Equal("pending", pending.Result.Value.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, pending.Result.Value.GetProperty("confirmationToken").ValueKind);
        Assert.Equal("method_not_found", (await fixture.Call("approval.resolve", new { requestId })).Error!.Code);
        Assert.True(fixture.Approvals.TryResolve(requestId, ApprovalResolution.ApproveOnce));
        var status = await fixture.Call("approval.status", new { requestId });
        var token = status.Result!.Value.GetProperty("confirmationToken").GetString();
        var invoke = new { operationId = "test.write", arguments = new { }, expectedRevision = "r1", confirmationToken = token, idempotencyKey = "same" };
        Assert.True((await fixture.Call("registry.invoke", invoke)).Result!.Value.GetProperty("success").GetBoolean());
        Assert.True((await fixture.Call("registry.invoke", invoke)).Result!.Value.GetProperty("success").GetBoolean());
        Assert.Equal(1, fixture.Operation.CallCount);
        var replay = await fixture.Call("registry.invoke", new { operationId = "test.write", arguments = new { }, expectedRevision = "r1", confirmationToken = token });
        Assert.Equal("confirmation_required", replay.Result!.Value.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Concurrent_duplicate_keys_execute_once_and_conflicts_fail()
    {
        using var fixture = new Fixture(OperationRisk.SafeWrite);
        var request = new { operationId = "test.write", arguments = new { }, expectedRevision = "r1", idempotencyKey = "key" };
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => fixture.Call("registry.invoke", request)));
        Assert.All(responses, response => Assert.True(response.Result!.Value.GetProperty("success").GetBoolean()));
        Assert.Equal(1, fixture.Operation.CallCount);
        var conflict = await fixture.Call("registry.invoke", new { operationId = "test.write", arguments = new { }, expectedRevision = "other", idempotencyKey = "key" });
        Assert.Equal("idempotency_conflict", conflict.Error!.Code);
    }

    [Fact]
    public async Task Workflow_continuation_preserves_failure_and_skips_dependants()
    {
        using var fixture = new Fixture(OperationRisk.SafeWrite);
        fixture.Operation.Fail = true;
        var arguments = JsonSerializer.SerializeToElement(new { });
        var workflow = new WorkflowDefinition("test-flow", "1.0.0", "Test", "Test", [], [], [],
            [new("one", "test.write", arguments, [], true), new("two", "test.write", arguments, ["one"])]);
        await fixture.Workflows.SaveAsync(workflow, TestContext.Current.CancellationToken);
        var response = await fixture.Call("workflow.run", new { workflowId = workflow.Id, parameters = new { }, expectedRevision = "r1" });
        Assert.False(response.Result!.Value.GetProperty("success").GetBoolean());
        Assert.Equal(1, fixture.Operation.CallCount);
        Assert.Equal("dependency_failed", response.Result.Value.GetProperty("results")[1].GetProperty("errorCode").GetString());
        var rank = Assert.Single(await fixture.Workflows.RankAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, rank.SuccessfulRuns);
        Assert.Equal(1, rank.FailedRuns);
    }

    [Fact]
    public async Task Workflow_retry_key_replays_result_and_requires_immutable_version()
    {
        using var fixture = new Fixture(OperationRisk.SafeWrite);
        var workflow = new WorkflowDefinition("retry-flow", "1.0.0", "Retry", "Test", [], [], [],
            [new("one", "test.write", JsonSerializer.SerializeToElement(new { }), [])]);
        await fixture.Workflows.SaveAsync(workflow, TestContext.Current.CancellationToken);
        var unpinned = await fixture.Call("workflow.run", new { workflowId = workflow.Id, parameters = new { }, expectedRevision = "r1", idempotencyKey = "key" });
        Assert.Equal("workflow_version_required", unpinned.Error!.Code);
        var request = new { workflowId = workflow.Id, version = "1.0.0", parameters = new { }, expectedRevision = "r1", idempotencyKey = "key" };
        var runs = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => fixture.Call("workflow.run", request)));
        Assert.All(runs, run => Assert.True(run.Result!.Value.GetProperty("success").GetBoolean()));
        Assert.Equal(1, fixture.Operation.CallCount);
        var rank = Assert.Single(await fixture.Workflows.RankAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, rank.SuccessfulRuns);
        var conflict = await fixture.Call("workflow.run", new { workflowId = workflow.Id, version = "1.0.0", parameters = new { }, expectedRevision = "changed", idempotencyKey = "key" });
        Assert.Equal("idempotency_conflict", conflict.Error!.Code);
    }

    [Theory]
    [InlineData(null, "workspace_revision_required")]
    [InlineData("old", "workspace_revision_mismatch")]
    public async Task Workflow_rejects_initial_revision_before_a_leading_read_can_refresh_it(string? revision, string error)
    {
        using var fixture = new Fixture(OperationRisk.SafeWrite);
        var workflow = new WorkflowDefinition("guarded-flow", "1.0.0", "Guarded", "Test", [], [], [],
            [new("read", "test.read", JsonSerializer.SerializeToElement(new { }), [], true),
             new("write", "test.write", JsonSerializer.SerializeToElement(new { }), [])]);
        await fixture.Workflows.SaveAsync(workflow, TestContext.Current.CancellationToken);
        var response = await fixture.Call("workflow.run", new { workflowId = workflow.Id, parameters = new { }, expectedRevision = revision });
        Assert.Equal(error, response.Error!.Code);
        Assert.Equal(0, fixture.ReadOperation.CallCount);
        Assert.Equal(0, fixture.Operation.CallCount);
    }

    [Fact]
    public async Task Read_only_workflow_does_not_require_a_write_revision()
    {
        using var fixture = new Fixture(OperationRisk.SafeWrite);
        var workflow = new WorkflowDefinition("read-flow", "1.0.0", "Read", "Test", [], [], [],
            [new("read", "test.read", JsonSerializer.SerializeToElement(new { }), [])]);
        await fixture.Workflows.SaveAsync(workflow, TestContext.Current.CancellationToken);
        var response = await fixture.Call("workflow.run", new { workflowId = workflow.Id, parameters = new { } });
        Assert.True(response.Result!.Value.GetProperty("success").GetBoolean());
        Assert.Equal(1, fixture.ReadOperation.CallCount);
    }

    [Fact]
    public async Task Workflow_retries_once_when_host_revision_advances_before_a_step_executes()
    {
        var workspace = new SequenceWorkspace("r1", "r2", "r2");
        using var fixture = new Fixture(OperationRisk.SafeWrite, workspace);
        var workflow = new WorkflowDefinition("settling-flow", "1.0.0", "Settling", "Test", [], [], [],
            [new("write", "test.write", JsonSerializer.SerializeToElement(new { }), [])]);
        await fixture.Workflows.SaveAsync(workflow, TestContext.Current.CancellationToken);

        var response = await fixture.Call("workflow.run", new { workflowId = workflow.Id, parameters = new { }, expectedRevision = "r1" });

        Assert.True(response.Result!.Value.GetProperty("success").GetBoolean());
        Assert.Equal(1, fixture.Operation.CallCount);
        var notices = response.Result.Value.GetProperty("results")[0].GetProperty("notices");
        Assert.Contains(notices.EnumerateArray(), notice =>
            notice.GetProperty("code").GetString() == "workflow_revision_refreshed");
    }

    private sealed class Fixture : IDisposable
    {
        public ApprovalService Approvals { get; } = new();
        public TestOperation Operation { get; }
        public TestOperation ReadOperation { get; } = new(OperationRisk.ReadOnly, "test.read");
        public FileWorkflowLibrary Workflows { get; }
        private readonly ProBridgeRequestHandler _handler;
        // Test files are isolated and intentionally retained for post-failure diagnosis.
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ArcGISProMCP.Tests", Guid.NewGuid().ToString("N"));
        public Fixture(OperationRisk risk, IWorkspaceStateProvider? workspace = null)
        {
            Operation = new(risk);
            var registry = new OperationRegistry();
            registry.Register(Operation);
            registry.Register(ReadOperation);
            Workflows = new FileWorkflowLibrary(Path.Combine(_root, "workflows"), registry);
            var context = new OperationContext(new Dispatcher(), workspace ?? new Workspace(), Approvals, new Audit(), "test", CancellationToken.None);
            _handler = new ProBridgeRequestHandler(registry, context, Workflows, new FileResourceStore(Path.Combine(_root, "resources")), new BridgeAccessState());
        }
        public Task<BridgeResponse> Call(string method, object parameters) => _handler.HandleAsync(
            new(BridgeProtocol.Version, Guid.NewGuid().ToString("N"), method, JsonSerializer.SerializeToElement(parameters), DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        public void Dispose() { _handler.Dispose(); Approvals.Dispose(); Workflows.Dispose(); }
    }

    private sealed class TestOperation(OperationRisk risk, string id = "test.write") : IOperation
    {
        public int CallCount { get; private set; }
        public bool Fail { get; set; }
        public OperationDescriptor Descriptor { get; } = OperationDescriptor.Create(id, "Test operation", "Test", JsonSchemas.EmptyObject, risk: risk, requiresConfirmation: risk == OperationRisk.Destructive);
        public Task<OperationResult> ExecuteAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(Fail ? OperationResult.Fail("test_failure", "Deliberate failure", "r1") : OperationResult.Ok(JsonSerializer.SerializeToElement(new { }), "r1"));
        }
    }

    private sealed class Workspace : IWorkspaceStateProvider
    {
        public Task<WorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new WorkspaceSnapshot("r1", DateTimeOffset.UtcNow, new("Test", "test.aprx", false, true), [], [], null, null, []));
    }

    private sealed class SequenceWorkspace(params string[] revisions) : IWorkspaceStateProvider
    {
        private int _index = -1;

        public Task<WorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            var index = Math.Min(Interlocked.Increment(ref _index), revisions.Length - 1);
            return Task.FromResult(new WorkspaceSnapshot(revisions[index], DateTimeOffset.UtcNow, new("Test", "test.aprx", false, true), [], [], null, null, []));
        }
    }
    private sealed class Dispatcher : IOperationDispatcher
    {
        public Task<T> OnMainCimThreadAsync<T>(Func<T> action, CancellationToken cancellationToken) => Task.FromResult(action());
        public Task OnMainCimThreadAsync(Action action, CancellationToken cancellationToken) { action(); return Task.CompletedTask; }
        public Task<T> OnUiThreadAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken) => action();
    }
    private sealed class Audit : IOperationAuditLog
    {
        public ValueTask WriteAsync(OperationAuditEvent auditEvent, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
