using System.IO.Pipes;
using System.Text.Json;
using ArcGISProMCP.Bridge.Protocol;
using ArcGISProMCP.Bridge.Transport;
using Xunit;

namespace ArcGISProMCP.Bridge.Tests;

public sealed class BridgeConcurrencyTests
{
    [Fact]
    public async Task Slow_operation_does_not_block_control_plane()
    {
        var handler = new BlockingHandler();
        var pipe = "ArcGISProMCP.Tests." + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeBridgeServer(handler, pipe);
        server.Start();
        var client = new NamedPipeBridgeClient(pipe, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
        var slow = client.CallAsync("slow", null, TestContext.Current.CancellationToken);
        try
        {
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            var health = await client.CallAsync("health", null, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal("health", health.GetProperty("method").GetString());
            Assert.False(slow.IsCompleted);
        }
        finally { handler.Release.TrySetResult(); }
        await slow;
    }

    [Fact]
    public async Task Incomplete_client_frame_does_not_block_other_connections()
    {
        var pipe = "ArcGISProMCP.Tests." + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeBridgeServer(new BlockingHandler(), pipe, ioTimeout: TimeSpan.FromMilliseconds(300));
        server.Start();
        await using var stalled = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await stalled.ConnectAsync(2000, TestContext.Current.CancellationToken);
        await stalled.WriteAsync(new byte[] { 42 }, TestContext.Current.CancellationToken);
        var client = new NamedPipeBridgeClient(pipe, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        var result = await client.CallAsync("health", null, TestContext.Current.CancellationToken);
        Assert.Equal("health", result.GetProperty("method").GetString());
        var eof = await stalled.ReadAsync(new byte[1], TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(0, eof);
    }

    [Fact]
    public async Task Same_pipe_cannot_be_owned_by_two_servers_and_releases_after_disposal()
    {
        var pipe = "ArcGISProMCP.Tests." + Guid.NewGuid().ToString("N");
        var first = new NamedPipeBridgeServer(new BlockingHandler(), pipe);
        await using var second = new NamedPipeBridgeServer(new BlockingHandler(), pipe.ToUpperInvariant());
        first.Start();
        try { Assert.Throws<IOException>(second.Start); }
        finally { await first.DisposeAsync(); }
        second.Start();
        Assert.True(second.IsRunning);
    }

    [Fact]
    public async Task Shutdown_cancels_cooperative_handlers_and_is_idempotent()
    {
        var handler = new BlockingHandler();
        var pipe = "ArcGISProMCP.Tests." + Guid.NewGuid().ToString("N");
        var server = new NamedPipeBridgeServer(handler, pipe);
        server.Start();
        var client = new NamedPipeBridgeClient(pipe, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
        var pending = client.CallAsync("slow", null, TestContext.Current.CancellationToken);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<BridgeException>(() => pending);
        Assert.Equal("outcome_unknown", error.Code);
        Assert.False(error.Retryable);
        await server.DisposeAsync();
        Assert.False(server.IsRunning);
        Assert.Throws<ObjectDisposedException>(server.Start);
    }

    [Fact]
    public async Task Caller_cancellation_after_transmission_does_not_claim_host_cancellation()
    {
        var handler = new BlockingHandler();
        var pipe = "ArcGISProMCP.Tests." + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeBridgeServer(handler, pipe);
        server.Start();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var client = new NamedPipeBridgeClient(pipe, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
        var pending = client.CallAsync("slow", null, caller.Token);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        try
        {
            await caller.CancelAsync();
            var error = await Assert.ThrowsAsync<BridgeException>(() => pending);
            Assert.Equal("outcome_unknown", error.Code);
            Assert.False(error.Retryable);
            Assert.False(handler.Release.Task.IsCompleted);
        }
        finally { handler.Release.TrySetResult(); }
    }

    private sealed class BlockingHandler : IBridgeRequestHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<BridgeResponse> HandleAsync(BridgeRequest request, CancellationToken cancellationToken)
        {
            if (request.Method == "slow")
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return new BridgeResponse(BridgeProtocol.Version, request.RequestId, true,
                JsonSerializer.SerializeToElement(new { method = request.Method }), null, DateTimeOffset.UtcNow);
        }
    }
}
