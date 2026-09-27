using System.Text.Json;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Operations.Tests.Fakes;

namespace ArcGISProMCP.Operations.Tests;

/// <summary>Revision publishing and cancellation rules every operation inherits.</summary>
public sealed class ProOperationBaseTests
{
    [Theory]
    [InlineData(OperationRisk.SafeWrite)]
    [InlineData(OperationRisk.Destructive)]
    [InlineData(OperationRisk.ExternalSideEffect)]
    public async Task Writes_advance_the_revision_and_publish_the_settled_snapshot(OperationRisk risk)
    {
        using var pro = new FakePro();
        var operation = new ProbeOperation(risk);

        var result = await operation.ExecuteAsync(FakePro.Arguments("{}"), pro.Context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("rev-1", pro.Workspace.Revision);
        Assert.Equal("rev-1", result.WorkspaceRevision);
        Assert.Equal([CancellationToken.None], pro.Workspace.SettledSnapshotTokens);
    }

    [Fact]
    public async Task A_write_publishes_its_revision_even_when_the_caller_cancels_during_execution()
    {
        using var pro = new FakePro();
        using var caller = new CancellationTokenSource();
        var operation = new ProbeOperation(OperationRisk.SafeWrite, onExecute: caller.Cancel);

        var result = await operation.ExecuteAsync(FakePro.Arguments("{}"), pro.Context, caller.Token);

        Assert.True(caller.IsCancellationRequested);
        Assert.Equal("rev-1", result.WorkspaceRevision);
        Assert.Equal([CancellationToken.None], pro.Workspace.SettledSnapshotTokens);
    }

    [Fact]
    public async Task Reads_never_advance_or_settle_the_revision()
    {
        using var pro = new FakePro();

        var result = await new ProbeOperation(OperationRisk.ReadOnly).ExecuteAsync(FakePro.Arguments("{}"), pro.Context, CancellationToken.None);

        Assert.Equal("rev-0", result.WorkspaceRevision);
        Assert.Equal("rev-0", pro.Workspace.Revision);
        Assert.Empty(pro.Workspace.SettledSnapshotTokens);
    }

    [Fact]
    public async Task A_failed_write_does_not_advance_the_revision()
    {
        using var pro = new FakePro();
        var operation = new ProbeOperation(OperationRisk.SafeWrite, onExecute: () => throw new InvalidOperationException("host refused"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            operation.ExecuteAsync(FakePro.Arguments("{}"), pro.Context, CancellationToken.None));

        Assert.Equal("rev-0", pro.Workspace.Revision);
        Assert.Empty(pro.Workspace.SettledSnapshotTokens);
    }

    [Fact]
    public async Task Safe_writes_drain_without_the_caller_token_and_other_risks_keep_it()
    {
        using var pro = new FakePro();
        using var caller = new CancellationTokenSource();
        var safeWrite = new ProbeOperation(OperationRisk.SafeWrite);
        var destructive = new ProbeOperation(OperationRisk.Destructive);
        var readOnly = new ProbeOperation(OperationRisk.ReadOnly);

        await safeWrite.ExecuteAsync(FakePro.Arguments("{}"), pro.Context, caller.Token);
        await destructive.ExecuteAsync(FakePro.Arguments("{}"), pro.Context, caller.Token);
        await readOnly.ExecuteAsync(FakePro.Arguments("{}"), pro.Context, caller.Token);

        Assert.Equal(CancellationToken.None, safeWrite.ExecutionToken);
        Assert.Equal(caller.Token, destructive.ExecutionToken);
        Assert.Equal(caller.Token, readOnly.ExecutionToken);
    }

    [Fact]
    public async Task A_request_cancelled_before_it_starts_never_executes()
    {
        using var pro = new FakePro();
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();
        var operation = new ProbeOperation(OperationRisk.SafeWrite);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            operation.ExecuteAsync(FakePro.Arguments("{}"), pro.Context, caller.Token));

        Assert.Equal(0, operation.Executions);
    }

    private sealed class ProbeOperation(OperationRisk risk, Action? onExecute = null) : ProOperationBase(OperationDescriptor.Create(
        "test.probe", "Probe", "Records how the base class runs it.", JsonSchemas.EmptyObject, risk: risk))
    {
        public CancellationToken ExecutionToken { get; private set; }

        public int Executions { get; private set; }

        protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
        {
            Executions++;
            ExecutionToken = cancellationToken;
            onExecute?.Invoke();
            var snapshot = await context.Workspace.GetSnapshotAsync(CancellationToken.None);
            return OperationResult.Ok(Json(new { ok = true }), snapshot.Revision);
        }
    }
}
