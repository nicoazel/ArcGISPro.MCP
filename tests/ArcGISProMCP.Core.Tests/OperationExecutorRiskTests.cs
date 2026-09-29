using System.Text.Json;
using ArcGISProMCP.Core.Execution;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Workspaces;
using Xunit;

namespace ArcGISProMCP.Core.Tests;

public sealed class OperationExecutorRiskTests
{
    [Fact]
    public async Task Destructive_risk_requires_confirmation_even_when_descriptor_flag_is_false()
    {
        var operation = new RiskyOperation();
        var executor = new OperationExecutor(
            new SingleOperationRegistry(operation),
            new OperationContext(
                new InlineDispatcher(),
                new StaticWorkspace(),
                new RejectConfirmation(),
                new CapturingAudit(),
                "risk-test",
                CancellationToken.None));

        var result = await executor.ExecuteAsync(
            new OperationRequest(operation.Descriptor.Id, JsonSerializer.SerializeToElement(new { }), Revision),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("confirmation_required", result.ErrorCode);
        Assert.Equal(0, operation.CallCount);
    }

    [Fact]
    public async Task Explicit_autonomous_policy_runs_risky_operation_without_a_token_and_warns()
    {
        var operation = new RiskyOperation();
        var audit = new CapturingAudit();
        var executor = new OperationExecutor(
            new SingleOperationRegistry(operation),
            new OperationContext(
                new InlineDispatcher(),
                new StaticWorkspace(),
                new AutonomousConfirmation(),
                audit,
                "autonomous-test",
                CancellationToken.None));

        var result = await executor.ExecuteAsync(
            new OperationRequest(operation.Descriptor.Id, JsonSerializer.SerializeToElement(new { }), Revision),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(1, operation.CallCount);
        Assert.Contains(result.Notices, notice => notice.Code == "autonomous_control" && notice.Severity == "warning");
        var auditEvent = Assert.Single(audit.Events);
        Assert.True(auditEvent.AutonomousBypass);
        Assert.True(auditEvent.Success);
    }

    [Fact]
    public async Task Rejected_risky_operation_is_audited_without_bypass()
    {
        var operation = new RiskyOperation();
        var audit = new CapturingAudit();
        var executor = new OperationExecutor(
            new SingleOperationRegistry(operation),
            new OperationContext(
                new InlineDispatcher(),
                new StaticWorkspace(),
                new RejectConfirmation(),
                audit,
                "risk-test",
                CancellationToken.None));

        await executor.ExecuteAsync(
            new OperationRequest(operation.Descriptor.Id, JsonSerializer.SerializeToElement(new { }), Revision),
            TestContext.Current.CancellationToken);

        var auditEvent = Assert.Single(audit.Events);
        Assert.False(auditEvent.AutonomousBypass);
        Assert.Equal("confirmation_required", auditEvent.ErrorCode);
    }

    [Fact]
    public async Task Autonomous_mode_refuses_requests_the_operation_reserves_for_review()
    {
        var operation = new GatedOperation(refuse: true);
        var audit = new CapturingAudit();
        var executor = Autonomous(operation, audit);

        var result = await executor.ExecuteAsync(
            new OperationRequest(operation.Descriptor.Id, JsonSerializer.SerializeToElement(new { }), Revision),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("destructive_tool_requires_review", result.ErrorCode);
        Assert.Equal(0, operation.CallCount);
        Assert.Equal(1, operation.GateCalls);
        var auditEvent = Assert.Single(audit.Events);
        Assert.False(auditEvent.AutonomousBypass);
        Assert.Equal("destructive_tool_requires_review", auditEvent.ErrorCode);
    }

    [Fact]
    public async Task Autonomous_mode_runs_requests_the_gate_accepts()
    {
        var operation = new GatedOperation(refuse: false);
        var executor = Autonomous(operation, new CapturingAudit());

        var result = await executor.ExecuteAsync(
            new OperationRequest(operation.Descriptor.Id, JsonSerializer.SerializeToElement(new { }), Revision),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(1, operation.CallCount);
        Assert.Contains(result.Notices, notice => notice.Code == "autonomous_control");
    }

    [Fact]
    public async Task Interactive_mode_does_not_consult_the_unattended_gate()
    {
        var operation = new GatedOperation(refuse: true);
        var executor = new OperationExecutor(
            new SingleOperationRegistry(operation),
            new OperationContext(new InlineDispatcher(), new StaticWorkspace(), new RejectConfirmation(), new CapturingAudit(), "risk-test", CancellationToken.None));

        var result = await executor.ExecuteAsync(
            new OperationRequest(operation.Descriptor.Id, JsonSerializer.SerializeToElement(new { }), Revision),
            TestContext.Current.CancellationToken);

        Assert.Equal("confirmation_required", result.ErrorCode);
        Assert.Equal(0, operation.GateCalls);
    }

    [Fact]
    public async Task Autonomous_mode_runs_a_gated_request_that_carries_a_valid_local_review_token()
    {
        var operation = new GatedOperation(refuse: true);
        var audit = new CapturingAudit();
        var confirmation = new AutonomousTokenConfirmation("reviewed");
        var executor = AutonomousWith(operation, audit, confirmation);

        var result = await executor.ExecuteAsync(
            new OperationRequest(operation.Descriptor.Id, JsonSerializer.SerializeToElement(new { }), Revision, "reviewed"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(1, operation.CallCount);
        Assert.Equal(0, operation.GateCalls);
        Assert.Equal(1, confirmation.Validations);
        Assert.DoesNotContain(result.Notices, notice => notice.Code == "autonomous_control");
        var auditEvent = Assert.Single(audit.Events);
        Assert.False(auditEvent.AutonomousBypass);
        Assert.True(auditEvent.Success);
    }

    [Fact]
    public async Task Autonomous_mode_rejects_an_invalid_token_instead_of_falling_back_to_the_gate_or_bypass()
    {
        var gated = new GatedOperation(refuse: true);
        var gatedAudit = new CapturingAudit();
        var gatedResult = await AutonomousWith(gated, gatedAudit, new AutonomousTokenConfirmation("reviewed")).ExecuteAsync(
            new OperationRequest(gated.Descriptor.Id, JsonSerializer.SerializeToElement(new { }), Revision, "forged"),
            TestContext.Current.CancellationToken);

        Assert.Equal("confirmation_required", gatedResult.ErrorCode);
        Assert.Equal(0, gated.CallCount);
        Assert.Equal(0, gated.GateCalls);
        Assert.False(Assert.Single(gatedAudit.Events).AutonomousBypass);

        // An operation the autonomous policy would run unattended is not bypassed when its token is bad either.
        var risky = new RiskyOperation();
        var riskyAudit = new CapturingAudit();
        var riskyResult = await AutonomousWith(risky, riskyAudit, new AutonomousTokenConfirmation("reviewed")).ExecuteAsync(
            new OperationRequest(risky.Descriptor.Id, JsonSerializer.SerializeToElement(new { }), Revision, "forged"),
            TestContext.Current.CancellationToken);

        Assert.Equal("confirmation_required", riskyResult.ErrorCode);
        Assert.Equal(0, risky.CallCount);
        Assert.False(Assert.Single(riskyAudit.Events).AutonomousBypass);
    }

    private static OperationExecutor AutonomousWith(IOperation operation, CapturingAudit audit, IConfirmationValidator confirmation) =>
        new(
            new SingleOperationRegistry(operation),
            new OperationContext(new InlineDispatcher(), new StaticWorkspace(), confirmation, audit, "autonomous-test", CancellationToken.None));

    private static OperationExecutor Autonomous(IOperation operation, CapturingAudit audit) =>
        new(
            new SingleOperationRegistry(operation),
            new OperationContext(new InlineDispatcher(), new StaticWorkspace(), new AutonomousConfirmation(), audit, "autonomous-test", CancellationToken.None));

    private const string Revision = "revision-risk";

    private sealed class GatedOperation(bool refuse) : IOperation, IUnattendedExecutionGate
    {
        public int CallCount { get; private set; }

        public int GateCalls { get; private set; }

        public OperationDescriptor Descriptor { get; } = OperationDescriptor.Create(
            "test.gated.execute", "Gated", "Unattended gate test.", JsonSchemas.EmptyObject,
            risk: OperationRisk.ExternalSideEffect, requiresConfirmation: true);

        public Task<OperationResult> ExecuteAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(OperationResult.Ok(null, Revision));
        }

        public ValueTask<OperationRefusal?> CheckUnattendedAsync(JsonElement arguments, CancellationToken cancellationToken)
        {
            GateCalls++;
            return ValueTask.FromResult(refuse ? new OperationRefusal("destructive_tool_requires_review", "Needs a person.") : null);
        }
    }

    private sealed class RiskyOperation : IOperation
    {
        public int CallCount { get; private set; }

        public OperationDescriptor Descriptor { get; } = OperationDescriptor.Create(
            "test.risky.execute", "Risky", "Risk confirmation test.", JsonSchemas.EmptyObject,
            risk: OperationRisk.Destructive, requiresConfirmation: false);

        public Task<OperationResult> ExecuteAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(OperationResult.Ok(null, Revision));
        }
    }

    private sealed class SingleOperationRegistry(IOperation operation) : IOperationRegistry
    {
        public IReadOnlyCollection<OperationDescriptor> Descriptors => [operation.Descriptor];
        public void Register(IOperation value) => throw new NotSupportedException();
        public bool TryGet(string id, out IOperation value)
        {
            if (string.Equals(id, operation.Descriptor.Id, StringComparison.OrdinalIgnoreCase))
            {
                value = operation;
                return true;
            }
            value = null!;
            return false;
        }
        public IReadOnlyList<SearchHit> Search(OperationQuery query) => [];
    }

    private sealed class StaticWorkspace : IWorkspaceStateProvider
    {
        public Task<WorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new WorkspaceSnapshot(
                Revision, DateTimeOffset.UtcNow,
                new ProjectState("Risk", "risk.aprx", false, true), [], [], null, null, []));
    }

    private sealed class InlineDispatcher : IOperationDispatcher
    {
        public Task<T> OnMainCimThreadAsync<T>(Func<T> action, CancellationToken cancellationToken) => Task.FromResult(action());
        public Task OnMainCimThreadAsync(Action action, CancellationToken cancellationToken) { action(); return Task.CompletedTask; }
        public Task<T> OnUiThreadAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken) => action();
    }

    private sealed class RejectConfirmation : IConfirmationValidator
    {
        public ValueTask<bool> IsValidAsync(string token, OperationDescriptor descriptor, JsonElement arguments, WorkspaceSnapshot workspace, CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);
    }

    private sealed class AutonomousConfirmation : IConfirmationValidator, IAutonomousExecutionPolicy
    {
        public bool AllowsUnattendedRiskyOperations => true;

        public ValueTask<bool> IsValidAsync(string token, OperationDescriptor descriptor, JsonElement arguments, WorkspaceSnapshot workspace, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Autonomous execution must not validate a confirmation token.");
    }

    /// <summary>Autonomous policy that also validates tokens, as the add-in's approval service does.</summary>
    private sealed class AutonomousTokenConfirmation(string validToken) : IConfirmationValidator, IAutonomousExecutionPolicy
    {
        public int Validations { get; private set; }

        public bool AllowsUnattendedRiskyOperations => true;

        public ValueTask<bool> IsValidAsync(string token, OperationDescriptor descriptor, JsonElement arguments, WorkspaceSnapshot workspace, CancellationToken cancellationToken)
        {
            Validations++;
            return ValueTask.FromResult(string.Equals(token, validToken, StringComparison.Ordinal));
        }
    }

    private sealed class CapturingAudit : IOperationAuditLog
    {
        internal List<OperationAuditEvent> Events { get; } = [];

        public ValueTask WriteAsync(OperationAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            Events.Add(auditEvent);
            return ValueTask.CompletedTask;
        }
    }
}
