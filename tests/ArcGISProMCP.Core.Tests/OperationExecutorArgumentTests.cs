using System.Text.Json;
using ArcGISProMCP.Core.Execution;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Workspaces;
using Xunit;

namespace ArcGISProMCP.Core.Tests;

public sealed class OperationExecutorArgumentTests
{
    [Fact]
    public async Task Invalid_arguments_are_rejected_before_operation_execution()
    {
        var operation = new CountingOperation(OperationRisk.ReadOnly);
        var executor = CreateExecutor(operation);

        using var arguments = JsonDocument.Parse("{}");
        var result = await executor.ExecuteAsync(
            new OperationRequest(operation.Descriptor.Id, arguments.RootElement.Clone()),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("invalid_arguments", result.ErrorCode);
        Assert.Contains("$.name", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, operation.CallCount);
    }

    [Fact]
    public async Task Valid_dry_run_is_described_without_writing_or_executing()
    {
        var operation = new CountingOperation(OperationRisk.SafeWrite);
        var executor = CreateExecutor(operation);

        using var arguments = JsonDocument.Parse("""
            {"name":"Map"}
            """);
        var result = await executor.ExecuteAsync(
            new OperationRequest(operation.Descriptor.Id, arguments.RootElement.Clone(), DryRun: true),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(0, operation.CallCount);
        Assert.True(result.Data?.GetProperty("valid").GetBoolean());
    }

    private static OperationExecutor CreateExecutor(CountingOperation operation) => new(
        new SingleOperationRegistry(operation),
        new OperationContext(
            new InlineDispatcher(),
            new StaticWorkspace(),
            new RejectConfirmation(),
            new CapturingAudit(),
            "argument-test",
            CancellationToken.None));

    private const string Revision = "revision-arguments";

    private sealed class CountingOperation(OperationRisk risk) : IOperation
    {
        public int CallCount { get; private set; }

        public OperationDescriptor Descriptor { get; } = OperationDescriptor.Create(
            "test.arguments.execute",
            "Argument test",
            "Exercises operation argument validation.",
            JsonSchemas.ObjectSchema("\"name\": { \"type\": \"string\", \"minLength\": 3 }", "name"),
            risk: risk);

        public Task<OperationResult> ExecuteAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(OperationResult.Ok(JsonSerializer.SerializeToElement(new { executed = true }), Revision));
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
                new ProjectState("Arguments", "arguments.aprx", false, true), [], [], null, null, []));
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
