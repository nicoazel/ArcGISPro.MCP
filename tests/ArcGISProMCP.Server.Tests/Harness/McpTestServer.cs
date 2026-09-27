using System.IO.Pipelines;
using ArcGISProMCP.Bridge.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ArcGISProMCP.Server.Tests.Harness;

/// <summary>
/// Runs the real server MCP configuration (<see cref="McpServerSetup"/>) in-process over a pair of
/// in-memory pipes and connects an SDK <see cref="McpClient"/> to it, with a
/// <see cref="FakeBridgeClient"/> standing in for ArcGIS Pro.
/// </summary>
public sealed class McpTestServer : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly Pipe _clientToServer;
    private readonly Pipe _serverToClient;

    private McpTestServer(IHost host, Pipe clientToServer, Pipe serverToClient, McpClient client, FakeBridgeClient bridge)
    {
        _host = host;
        _clientToServer = clientToServer;
        _serverToClient = serverToClient;
        Client = client;
        Bridge = bridge;
    }

    public McpClient Client { get; }

    public FakeBridgeClient Bridge { get; }

    public static async Task<McpTestServer> StartAsync(FakeBridgeClient? bridge = null, CancellationToken cancellationToken = default)
    {
        bridge ??= new FakeBridgeClient();
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddSingleton<IBridgeClient>(bridge);
        builder.Services
            .AddMcpServer(McpServerSetup.ConfigureServerOptions)
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .AddArcGisProMcp();
        var host = builder.Build();
        await host.StartAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var transport = new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream());
            var client = await McpClient.CreateAsync(
                transport,
                new McpClientOptions { ClientInfo = new Implementation { Name = "server-tests", Version = "1.0.0" } },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new McpTestServer(host, clientToServer, serverToClient, client, bridge);
        }
        catch
        {
            await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
            host.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync().ConfigureAwait(false);
        await _clientToServer.Writer.CompleteAsync().ConfigureAwait(false);
        await _serverToClient.Writer.CompleteAsync().ConfigureAwait(false);
        await _host.StopAsync(CancellationToken.None).ConfigureAwait(false);
        _host.Dispose();
    }
}
