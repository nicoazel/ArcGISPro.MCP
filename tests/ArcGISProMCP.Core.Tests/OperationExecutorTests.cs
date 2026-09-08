using System.Text.Json;
using ArcGISProMCP.Core.Execution;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Registry;
using ArcGISProMCP.Core.Workspaces;
using Xunit;

namespace ArcGISProMCP.Core.Tests;

public sealed class OperationExecutorTests
{
    [Fact]
    public async Task Write_requires_a_workspace_revision_and_audits_the_denial()
    {
        var fixture = new ExecutorFixture(OperationRisk.SafeWrite);

        var result = await fixture.ExecuteAsync(expectedRevision: null);

        Assert.False(result.Success);
        Assert.Equal("workspace_revision_required", result.ErrorCode);
        Assert.Equal(0, fixture.Operation.CallCount);
        Assert.Single(fixture.Audit.Events);
    }

    [Fact]
    public async Task Stale_workspace_revision_prevents_mutation()
    {
        var fixture = new ExecutorFixture(OperationRisk.SafeWrite);

        var result = await fixture.ExecuteAsync(expectedRevision: "stale");

        Assert.False(result.Success);
        Assert.Equal("workspace_revision_mismatch", result.ErrorCode);
        Assert.Equal(0, fixture.Operation.CallCount);
    }

    [Fact]
    public async Task Matching_workspace_revision_allows_mutation()
    {
        var fixture = new ExecutorFixture(OperationRisk.SafeWrite);

        var result = await fixture.ExecuteAsync(expectedRevision: ExecutorFixture.Revision);

        Assert.True(result.Success);
        Assert.Equal(1, fixture.Operation.CallCount);
    }

    [Fact]
    public async Task Dry_run_describes_a_write_without_executing_it()
    {
        var fixture = new ExecutorFixture(OperationRisk.SafeWrite);

        var result = await fixture.ExecuteAsync(expectedRevision: null, dryRun: true);

        Assert.True(result.Success);
        Assert.True(result.Data?.GetProperty("valid").GetBoolean());
        Assert.Equal(0, fixture.Operation.CallCount);
        Assert.Single(fixture.Audit.Events);
    }

    private sealed class ExecutorFixture
    {
        internal const string Revision = "revision-1";
        internal TestOperation Operation { get; }
        internal CapturingAuditLog Audit { get; } = new();

        private readonly OperationExecutor _executor;

        internal ExecutorFixture(OperationRisk risk)
        {
            Operation = new TestOperation(risk);
            var registry = new OperationRegistry();
            registry.Register(Operation);
            var workspace = new StaticWorkspace();
            var context = new OperationContext(
                new InlineDispatcher(),
                workspace,
                new RejectConfirmation(),
                Audit,
                "test-correlation",
                CancellationToken.None);
            _executor = new OperationExecutor(registry, context);
        }

        internal Task<OperationResult> ExecuteAsync(string? expectedRevision, bool dryRun = false) =>
            _executor.ExecuteAsync(
                new OperationRequest(
                    Operation.Descriptor.Id,
                    JsonSerializer.SerializeToElement(new { }),
                    expectedRevision,
                    DryRun: dryRun),
                TestContext.Current.CancellationToken);
    }

    private sealed class TestOperation(OperationRisk risk) : IOperation
    {
        public int CallCount { get; private set; }

        public OperationDescriptor Descriptor { get; } = OperationDescriptor.Create(
            "test.mutate.execute",
            "Test execute",
            "Exercises the operation safety boundary.",
            JsonSchemas.EmptyObject,
            risk: risk);

        public Task<OperationResult> ExecuteAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(OperationResult.Ok(JsonSerializer.SerializeToElement(new { executed = true }), ExecutorFixture.Revision));
        }
    }

    private sealed class StaticWorkspace : IWorkspaceStateProvider
    {
        public Task<WorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new WorkspaceSnapshot(
                ExecutorFixture.Revision,
                DateTimeOffset.UtcNow,
                new ProjectState("Test", "test.aprx", false, true),
                [],
                [],
                null,
                null,
                []));
    }

    private sealed class InlineDispatcher : IOperationDispatcher
    {
        public Task<T> OnMainCimThreadAsync<T>(Func<T> action, CancellationToken cancellationToken) => Task.FromResult(action());
        public Task OnMainCimThreadAsync(Action action, CancellationToken cancellationToken)
        {
            action();
            return Task.CompletedTask;
        }
        public Task<T> OnUiThreadAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken) => action();
    }

    private sealed class RejectConfirmation : IConfirmationValidator
    {
        public ValueTask<bool> IsValidAsync(
            string token,
            OperationDescriptor descriptor,
            JsonElement arguments,
            WorkspaceSnapshot workspace,
            CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }

    private sealed class CapturingAuditLog : IOperationAuditLog
    {
        internal List<OperationAuditEvent> Events { get; } = [];

        public ValueTask WriteAsync(OperationAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            Events.Add(auditEvent);
            return ValueTask.CompletedTask;
        }
    }
}
