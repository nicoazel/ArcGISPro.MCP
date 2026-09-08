using System.Collections.Concurrent;
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

public sealed class HostSchedulingTests
{
    [Fact]
    public async Task Slow_operations_are_serialized_by_the_resource_owner_gate()
    {
        using var fixture = new Fixture(OperationRisk.SafeWrite);
        var first = fixture.Call("registry.invoke", new { operationId = "test.write", arguments = new { }, expectedRevision = "r1" });
        await fixture.Operation.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var second = fixture.Call("registry.invoke", new { operationId = "test.write", arguments = new { }, expectedRevision = "r1" });
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, fixture.Operation.CallCount);

        fixture.Operation.Release.TrySetResult(true);
        var responses = await Task.WhenAll(first, second);
        Assert.All(responses, response => Assert.True(response.Success, response.Error?.Message));
        Assert.Equal(2, fixture.Operation.CallCount);
    }

    [Fact]
    public async Task System_state_remains_responsive_while_a_mutation_is_running()
    {
        using var fixture = new Fixture(OperationRisk.SafeWrite);
        var invoke = fixture.Call("registry.invoke", new { operationId = "test.write", arguments = new { }, expectedRevision = "r1" });
        await fixture.Operation.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var state = fixture.Call("system.get_state", null);
        var completed = await Task.WhenAny(state, Task.Delay(500, TestContext.Current.CancellationToken));
        Assert.Same(state, completed);
        var stateResponse = await state;
        Assert.True(stateResponse.Success, stateResponse.Error?.Message);
        Assert.Equal(1, stateResponse.Result!.Value.GetProperty("runningOperationCount").GetInt32());

        fixture.Operation.Release.TrySetResult(true);
        await invoke;
    }

    [Fact]
    public async Task Idempotency_replays_success_after_audit_failure_without_repeating_the_write()
    {
        using var fixture = new Fixture(OperationRisk.SafeWrite, new ThrowingAudit(), blocks: false);
        var request = new { operationId = "test.write", arguments = new { }, expectedRevision = "r1", idempotencyKey = "audit-failure-key" };

        var first = await fixture.Call("registry.invoke", request);
        var retry = await fixture.Call("registry.invoke", request);

        Assert.True(first.Success, first.Error?.Message);
        Assert.True(retry.Success, retry.Error?.Message);
        Assert.Equal(1, fixture.Operation.CallCount);
        Assert.Contains(first.Result!.Value.GetProperty("notices").EnumerateArray(), notice =>
            notice.GetProperty("code").GetString() == "audit_write_failed");
    }

    [Fact]
    public async Task Approval_request_and_status_bypass_the_operation_gate()
    {
        using var fixture = new Fixture(OperationRisk.Destructive);
        var initial = await fixture.Call("approval.request", new
        {
            operationId = "test.write", arguments = new { }, expectedRevision = "r1"
        });
        Assert.True(initial.Success, initial.Error?.Message);
        var requestId = initial.Result!.Value.GetProperty("requestId").GetString()!;
        Assert.True(fixture.Approvals.TryResolve(requestId, ApprovalResolution.ApproveOnce));
        var approved = await fixture.Call("approval.status", new { requestId });
        var token = approved.Result!.Value.GetProperty("confirmationToken").GetString()!;

        var invoke = fixture.Call("registry.invoke", new
        {
            operationId = "test.write", arguments = new { }, expectedRevision = "r1", confirmationToken = token
        });
        await fixture.Operation.Started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        var pending = fixture.Call("approval.request", new
        {
            operationId = "test.write", arguments = new { }, expectedRevision = "r1"
        });
        var completed = await Task.WhenAny(pending, Task.Delay(500, TestContext.Current.CancellationToken));
        Assert.Same(pending, completed);
        var pendingResponse = await pending;
        Assert.True(pendingResponse.Success, pendingResponse.Error?.Message);
        var pendingRequestId = pendingResponse.Result!.Value.GetProperty("requestId").GetString()!;

        var status = await fixture.Call("approval.status", new { requestId = pendingRequestId });
        Assert.True(status.Success, status.Error?.Message);
        Assert.Equal("pending", status.Result!.Value.GetProperty("status").GetString());

        fixture.Operation.Release.TrySetResult(true);
        await invoke;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ProBridgeRequestHandler _handler;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ArcGISProMCP.HostScheduling", Guid.NewGuid().ToString("N"));

        public ApprovalService Approvals { get; } = new();
        public TestOperation Operation { get; }

        public Fixture(OperationRisk risk, IOperationAuditLog? audit = null, bool blocks = true)
        {
            Operation = new TestOperation(risk) { Blocks = blocks };
            var registry = new OperationRegistry();
            registry.Register(Operation);
            var workflows = new FileWorkflowLibrary(Path.Combine(_root, "workflows"), registry);
            var context = new OperationContext(
                new DirectDispatcher(), new FixedWorkspace(), Approvals,
                audit ?? new NoopAudit(), "test", CancellationToken.None);
            _handler = new ProBridgeRequestHandler(
                registry, context, workflows,
                new FileResourceStore(Path.Combine(_root, "resources")),
                new BridgeAccessState());
            Workflows = workflows;
        }

        public FileWorkflowLibrary Workflows { get; }

        public Task<BridgeResponse> Call(string method, object? parameters)
        {
            JsonElement? serialized = parameters is null ? null : JsonSerializer.SerializeToElement(parameters);
            return _handler.HandleAsync(
                new(BridgeProtocol.Version, Guid.NewGuid().ToString("N"), method, serialized, DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);
        }

        public void Dispose()
        {
            Operation.Release.TrySetResult(true);
            if (Operation.Started.Task.IsCompleted)
                Operation.Completed.Task.Wait(TimeSpan.FromSeconds(2));
            _handler.Dispose();
            Approvals.Dispose();
            Workflows.Dispose();
        }
    }

    private sealed class TestOperation(OperationRisk risk) : IOperation
    {
        private int _callCount;
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Blocks { get; set; }
        public int CallCount => Volatile.Read(ref _callCount);
        public OperationDescriptor Descriptor { get; } = OperationDescriptor.Create(
            "test.write", "Test write", "Test", JsonSchemas.EmptyObject,
            risk: risk,
            requiresConfirmation: risk is OperationRisk.Destructive or OperationRisk.ExternalSideEffect);

        public async Task<OperationResult> ExecuteAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            Started.TrySetResult(true);
            try
            {
                if (Blocks)
                    await Release.Task.WaitAsync(cancellationToken);
                return OperationResult.Ok(JsonSerializer.SerializeToElement(new { accepted = true }), "r1");
            }
            finally
            {
                Completed.TrySetResult(true);
            }
        }
    }

    private sealed class ThrowingAudit : IOperationAuditLog
    {
        public ValueTask WriteAsync(OperationAuditEvent auditEvent, CancellationToken cancellationToken) =>
            ValueTask.FromException(new IOException("audit sink unavailable"));
    }

    private sealed class NoopAudit : IOperationAuditLog
    {
        public ValueTask WriteAsync(OperationAuditEvent auditEvent, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class DirectDispatcher : IOperationDispatcher
    {
        public Task<T> OnMainCimThreadAsync<T>(Func<T> action, CancellationToken cancellationToken) => Task.FromResult(action());
        public Task OnMainCimThreadAsync(Action action, CancellationToken cancellationToken) { action(); return Task.CompletedTask; }
        public Task<T> OnUiThreadAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken) => action();
    }

    private sealed class FixedWorkspace : IWorkspaceStateProvider
    {
        public Task<WorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(
            new WorkspaceSnapshot("r1", DateTimeOffset.UtcNow,
                new("Test", "test.aprx", false, true), [], [], null, null, []));
    }
}
