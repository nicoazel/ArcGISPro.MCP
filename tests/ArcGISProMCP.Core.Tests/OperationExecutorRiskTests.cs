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

    private const string Revision = "revision-risk";

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
