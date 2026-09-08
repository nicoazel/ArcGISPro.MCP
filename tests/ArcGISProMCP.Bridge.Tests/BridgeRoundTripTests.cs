using System.Text.Json;
using ArcGISProMCP.Bridge.Protocol;
using ArcGISProMCP.Bridge.Transport;
using Xunit;

namespace ArcGISProMCP.Bridge.Tests;

public sealed class BridgeRoundTripTests
{
    [Fact]
    public async Task Same_user_pipe_round_trips_a_versioned_request()
    {
        var pipeName = $"ArcGISProMCP.Tests.{Guid.NewGuid():N}";
        await using var server = new NamedPipeBridgeServer(new EchoHandler(), pipeName);
        server.Start();

        var client = new NamedPipeBridgeClient(pipeName, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
        var result = await client.CallAsync("echo", new { message = "hello" }, TestContext.Current.CancellationToken);

        Assert.Equal("echo", result.GetProperty("method").GetString());
        Assert.Equal("hello", result.GetProperty("parameters").GetProperty("message").GetString());
        Assert.True(server.IsRunning);
    }

    [Fact]
    public async Task Missing_add_in_is_reported_as_retryable_unavailable()
    {
        var client = new NamedPipeBridgeClient(
            $"ArcGISProMCP.Tests.Missing.{Guid.NewGuid():N}",
            TimeSpan.FromMilliseconds(75),
            TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAsync<BridgeException>(() =>
            client.CallAsync("system.get_state", null, TestContext.Current.CancellationToken));

        Assert.Equal("arcgis_unavailable", exception.Code);
        Assert.True(exception.Retryable);
    }

    private sealed class EchoHandler : IBridgeRequestHandler
    {
        public Task<BridgeResponse> HandleAsync(BridgeRequest request, CancellationToken cancellationToken)
        {
            var result = JsonSerializer.SerializeToElement(new
            {
                method = request.Method,
                parameters = request.Parameters
            });
            return Task.FromResult(new BridgeResponse(
                BridgeProtocol.Version,
                request.RequestId,
                true,
                result,
                null,
                DateTimeOffset.UtcNow));
        }
    }
}
