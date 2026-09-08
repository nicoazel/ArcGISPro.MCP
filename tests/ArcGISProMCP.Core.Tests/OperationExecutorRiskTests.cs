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

    private sealed class CapturingAudit : IOperationAuditLog
    {
        public ValueTask WriteAsync(OperationAuditEvent auditEvent, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
