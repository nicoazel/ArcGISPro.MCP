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

    [Fact]
    public async Task Risky_dry_run_does_not_request_approval_or_execute()
    {
        var fixture = new ExecutorFixture(OperationRisk.Destructive);
        var result = await fixture.ExecuteAsync(expectedRevision: null, dryRun: true);
        Assert.True(result.Success);
        Assert.Equal(0, fixture.Operation.CallCount);
    }

    [Fact]
    public async Task Dry_run_is_audited_as_a_dry_run()
    {
        var fixture = new ExecutorFixture(OperationRisk.SafeWrite);

        await fixture.ExecuteAsync(expectedRevision: null, dryRun: true);

        Assert.Equal(OperationAuditKinds.DryRun, Assert.Single(fixture.Audit.Events).Kind);
    }

    [Fact]
    public async Task Dry_run_uses_the_operation_static_validation_and_never_executes()
    {
        var operation = new DryRunnableOperation();
        var audit = new CapturingAuditLog();
        var registry = new OperationRegistry();
        registry.Register(operation);
        var executor = new OperationExecutor(registry, new OperationContext(
            new InlineDispatcher(), new StaticWorkspace(), new RejectConfirmation(), audit, "dry-run", CancellationToken.None));

        // No revision and no confirmation token: a dry run needs neither, even for a confirmation-gated operation.
        var result = await executor.ExecuteAsync(
            new OperationRequest(operation.Descriptor.Id, JsonSerializer.SerializeToElement(new { }), DryRun: true),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("static", result.Data?.GetProperty("checked").GetString());
        Assert.Equal(1, operation.DryRunCalls);
        Assert.Equal(0, operation.ExecuteCalls);
        var auditEvent = Assert.Single(audit.Events);
        Assert.Equal(OperationAuditKinds.DryRun, auditEvent.Kind);
        Assert.False(auditEvent.AutonomousBypass);
    }

    [Fact]
    public async Task Dry_run_with_invalid_arguments_does_not_reach_the_operation()
    {
        var operation = new DryRunnableOperation();
        var registry = new OperationRegistry();
        registry.Register(operation);
        var executor = new OperationExecutor(registry, new OperationContext(
            new InlineDispatcher(), new StaticWorkspace(), new RejectConfirmation(), new CapturingAuditLog(), "dry-run", CancellationToken.None));

        var result = await executor.ExecuteAsync(
            new OperationRequest(operation.Descriptor.Id, JsonSerializer.SerializeToElement(new { unexpected = 1 }), DryRun: true),
            TestContext.Current.CancellationToken);

        Assert.Equal("invalid_arguments", result.ErrorCode);
        Assert.Equal(0, operation.DryRunCalls);
        Assert.Equal(0, operation.ExecuteCalls);
    }

    [Fact]
    public async Task Unknown_operation_id_is_audited_as_operation_not_found()
    {
        var fixture = new ExecutorFixture(OperationRisk.ReadOnly);

        var result = await fixture.ExecuteAsync(expectedRevision: null, operationId: "does.not.exist");

        Assert.False(result.Success);
        Assert.Equal("operation_not_found", result.ErrorCode);
        var auditEvent = Assert.Single(fixture.Audit.Events);
        Assert.Equal("does.not.exist", auditEvent.OperationId);
        Assert.Equal("unknown", auditEvent.OperationVersion);
        Assert.Equal("operation_not_found", auditEvent.ErrorCode);
        Assert.Equal(OperationAuditKinds.Operation, auditEvent.Kind);
        Assert.False(auditEvent.Success);
        Assert.False(auditEvent.AutonomousBypass);
    }

    [Fact]
    public async Task Unknown_operation_id_audit_failure_adds_a_notice()
    {
        var fixture = new ExecutorFixture(OperationRisk.ReadOnly);
        fixture.Audit.Fail = true;

        var result = await fixture.ExecuteAsync(expectedRevision: null, operationId: "does.not.exist");

        Assert.Equal("operation_not_found", result.ErrorCode);
        var notice = Assert.Single(result.Notices);
        Assert.Equal("audit_write_failed", notice.Code);
    }

    [Fact]
    public async Task Known_operation_audit_failure_adds_a_notice()
    {
        var fixture = new ExecutorFixture(OperationRisk.SafeWrite);
        fixture.Audit.Fail = true;

        var result = await fixture.ExecuteAsync(expectedRevision: ExecutorFixture.Revision);

        Assert.True(result.Success);
        Assert.Equal("audit_write_failed", Assert.Single(result.Notices).Code);
    }

    [Fact]
    public async Task Oversized_unknown_operation_id_is_truncated_in_the_audit_record()
    {
        var fixture = new ExecutorFixture(OperationRisk.ReadOnly);

        await fixture.ExecuteAsync(expectedRevision: null, operationId: new string('x', 5000));

        var auditEvent = Assert.Single(fixture.Audit.Events);
        Assert.True(auditEvent.OperationId.Length < 300);
    }

    [Fact]
    public async Task Ordinary_operation_audit_records_default_kind_and_no_bypass()
    {
        var fixture = new ExecutorFixture(OperationRisk.SafeWrite);

        await fixture.ExecuteAsync(expectedRevision: ExecutorFixture.Revision);

        var auditEvent = Assert.Single(fixture.Audit.Events);
        Assert.Equal(OperationAuditKinds.Operation, auditEvent.Kind);
        Assert.False(auditEvent.AutonomousBypass);
        Assert.Null(auditEvent.Decision);
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

        internal Task<OperationResult> ExecuteAsync(string? expectedRevision, bool dryRun = false, string? operationId = null) =>
            _executor.ExecuteAsync(
                new OperationRequest(
                    operationId ?? Operation.Descriptor.Id,
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
            risk: risk, requiresConfirmation: risk == OperationRisk.Destructive);

        public Task<OperationResult> ExecuteAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(OperationResult.Ok(JsonSerializer.SerializeToElement(new { executed = true }), ExecutorFixture.Revision));
        }
    }

    private sealed class DryRunnableOperation : IOperation, IDryRunnableOperation
    {
        public int ExecuteCalls { get; private set; }

        public int DryRunCalls { get; private set; }

        public OperationDescriptor Descriptor { get; } = OperationDescriptor.Create(
            "test.dry-runnable", "Dry-runnable", "Exercises the static dry-run path.", JsonSchemas.EmptyObject,
            risk: OperationRisk.ExternalSideEffect, requiresConfirmation: true);

        public Task<OperationResult> ExecuteAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
        {
            ExecuteCalls++;
            throw new InvalidOperationException("A dry run must never execute.");
        }

        public Task<OperationResult> DryRunAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
        {
            DryRunCalls++;
            return Task.FromResult(OperationResult.Ok(JsonSerializer.SerializeToElement(new { @checked = "static" }), ExecutorFixture.Revision));
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
        internal bool Fail { get; set; }

        public ValueTask WriteAsync(OperationAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            if (Fail) return ValueTask.FromException(new IOException("audit sink unavailable"));
            Events.Add(auditEvent);
            return ValueTask.CompletedTask;
        }
    }
}
