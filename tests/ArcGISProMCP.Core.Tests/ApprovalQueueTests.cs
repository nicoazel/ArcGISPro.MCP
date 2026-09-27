using System.Text.Json;
using ArcGISProMCP.Core.Approvals;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Workspaces;

namespace ArcGISProMCP.Core.Tests;

public sealed class ApprovalServiceTests
{
    [Fact]
    public async Task ApprovedTokenIsArgumentRevisionBoundAndSingleUse()
    {
        var time = new ManualTimeProvider();
        using var queue = new ApprovalService(time, TimeSpan.FromMinutes(2));
        var descriptor = Descriptor();
        var requestedArguments = Json("{\"z\":2,\"a\":1}");
        var request = queue.Request(descriptor, requestedArguments, Workspace("revision-1"));

        Assert.True(queue.TryResolve(request.Id, ApprovalResolution.ApproveOnce));
        var approved = Assert.IsType<ApprovalRequestSnapshot>(queue.GetStatus(request.Id));
        Assert.Equal(ApprovalRequestState.Approved, approved.State);
        Assert.False(string.IsNullOrWhiteSpace(approved.ConfirmationToken));

        Assert.False(await queue.IsValidAsync(
            approved.ConfirmationToken!,
            descriptor,
            Json("{\"a\":1,\"z\":3}"),
            Workspace("revision-1"),
            CancellationToken.None));
        Assert.False(await queue.IsValidAsync(
            approved.ConfirmationToken!,
            descriptor,
            Json("{\"a\":1,\"z\":2}"),
            Workspace("revision-2"),
            CancellationToken.None));

        Assert.True(await queue.IsValidAsync(
            approved.ConfirmationToken!,
            descriptor,
            Json("{\"a\":1,\"z\":2}"),
            Workspace("revision-1"),
            CancellationToken.None));
        Assert.False(await queue.IsValidAsync(
            approved.ConfirmationToken!,
            descriptor,
            requestedArguments,
            Workspace("revision-1"),
            CancellationToken.None));
        Assert.Equal(ApprovalRequestState.Consumed, queue.GetStatus(request.Id)!.State);
    }

    [Fact]
    public async Task PendingDeniedCancelledAndExpiredRequestsFailClosed()
    {
        var time = new ManualTimeProvider();
        using var queue = new ApprovalService(time, TimeSpan.FromSeconds(30));
        var descriptor = Descriptor();
        var arguments = Json("{\"layer\":\"Parcels\"}");
        var workspace = Workspace("revision-1");

        var pending = queue.Request(descriptor, arguments, workspace);
        Assert.Null(queue.GetStatus(pending.Id)!.ConfirmationToken);
        Assert.False(await queue.IsValidAsync("not-a-token", descriptor, arguments, workspace, CancellationToken.None));
        Assert.True(queue.TryResolve(pending.Id, ApprovalResolution.Deny));
        Assert.Equal(ApprovalRequestState.Denied, queue.GetStatus(pending.Id)!.State);

        var invalid = queue.Request(descriptor, Json("{\"layer\":\"Invalid\"}"), workspace);
        Assert.False(queue.TryResolve(invalid.Id, (ApprovalResolution)999));
        Assert.Equal(ApprovalRequestState.Pending, queue.GetStatus(invalid.Id)!.State);

        var cancelled = queue.Request(descriptor, Json("{\"layer\":\"Roads\"}"), workspace);
        Assert.True(queue.TryCancel(cancelled.Id));
        Assert.Equal(ApprovalRequestState.Cancelled, queue.GetStatus(cancelled.Id)!.State);

        var expiring = queue.Request(descriptor, Json("{\"layer\":\"Buildings\"}"), workspace);
        Assert.True(queue.TryResolve(expiring.Id, ApprovalResolution.ApproveOnce));
        var token = queue.GetStatus(expiring.Id)!.ConfirmationToken!;
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.False(await queue.IsValidAsync(token, descriptor, arguments, workspace, CancellationToken.None));
        Assert.Equal(ApprovalRequestState.Expired, queue.GetStatus(expiring.Id)!.State);

        var revoked = queue.Request(descriptor, Json("{\"layer\":\"Trees\"}"), workspace);
        Assert.True(queue.TryResolve(revoked.Id, ApprovalResolution.ApproveOnce));
        queue.RevokeAll();
        Assert.Equal(ApprovalRequestState.Cancelled, queue.GetStatus(revoked.Id)!.State);
    }

    [Fact]
    public void PendingSnapshotRetainsFullArgumentsAndDeduplicatesExactRequest()
    {
        using var queue = new ApprovalService(lifetime: TimeSpan.FromMinutes(2));
        var descriptor = Descriptor();
        var workspace = Workspace("revision-1");
        var arguments = Json("{\"nested\":{\"label\":\"full review text\",\"values\":[1,2,3]}}");

        var first = queue.Request(descriptor, arguments, workspace);
        var duplicate = queue.Request(descriptor, arguments, workspace);

        Assert.Equal(first.Id, duplicate.Id);
        var pending = Assert.Single(queue.GetPending());
        Assert.Equal(arguments.GetRawText(), pending.Arguments.GetRawText());
        Assert.Equal("operation.test", pending.OperationId);
        Assert.Equal("revision-1", pending.WorkspaceRevision);
    }

    [Fact]
    public void ExclusiveRequestNeverReusesAPendingEntry()
    {
        using var queue = new ApprovalService(lifetime: TimeSpan.FromMinutes(2));
        var descriptor = Descriptor();
        var workspace = Workspace("revision-1");
        var arguments = Json("{\"path\":\"C:/projects/city.aprx\"}");

        var remote = queue.Request(descriptor, arguments, workspace);
        var reused = queue.Request(descriptor, arguments, workspace);
        var exclusive = queue.Request(descriptor, arguments, workspace, reuseExisting: false);

        Assert.Equal(remote.Id, reused.Id);
        Assert.NotEqual(remote.Id, exclusive.Id);
        Assert.True(queue.TryResolve(exclusive.Id, ApprovalResolution.ApproveOnce));
        // Approving the exclusive entry leaves the remotely queued request pending for its own review.
        Assert.Equal(ApprovalRequestState.Pending, queue.GetStatus(remote.Id)!.State);
        Assert.Equal(remote.Id, Assert.Single(queue.GetPending()).Id);
    }

    private static OperationDescriptor Descriptor() => OperationDescriptor.Create(
        "operation.test",
        "Test operation",
        "A test operation requiring a local decision.",
        Json("{\"type\":\"object\"}"),
        risk: OperationRisk.SafeWrite,
        requiresConfirmation: true);

    private static WorkspaceSnapshot Workspace(string revision) => new(
        revision,
        DateTimeOffset.UtcNow,
        new ProjectState("Test", null, false, true),
        [],
        [],
        null,
        null,
        []);

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) => new NullTimer();

        private sealed class NullTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
