using System.IO.Pipelines;
using System.Text.Json;
using ArcGISProMCP.Bridge.Transport;
using ArcGISProMCP.Core.Approvals;
using ArcGISProMCP.Server.Tests.Harness;
using ArcGISProMCP.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace ArcGISProMCP.Server.Tests.EndToEnd;

/// <summary>
/// The whole chain without ArcGIS Pro: an SDK <see cref="McpClient"/> talks to the real gateway
/// configuration (<see cref="McpServerSetup"/>) over in-memory pipes; the gateway's
/// <see cref="IBridgeClient"/> calls the add-in's real bridge handler in-process
/// (<see cref="InProcessBridgeClient"/>), which runs the production operations over a fake project
/// (<see cref="FakeHostRuntime"/>). The test plays the person at the ArcGIS Pro panel through
/// <see cref="ApproveAsPerson"/>.
/// </summary>
internal sealed class EndToEndServer : IAsyncDisposable
{
    public const string DefaultScenario = "riverton";

    private readonly IHost _host;
    private readonly Pipe _clientToServer;
    private readonly Pipe _serverToClient;
    private Dictionary<string, Tool>? _tools;

    private EndToEndServer(FakeHostRuntime runtime, InProcessBridgeClient bridge, IHost host, Pipe clientToServer, Pipe serverToClient, McpClient client)
    {
        Runtime = runtime;
        Bridge = bridge;
        _host = host;
        _clientToServer = clientToServer;
        _serverToClient = serverToClient;
        Client = client;
    }

    public FakeHostRuntime Runtime { get; }

    public InProcessBridgeClient Bridge { get; }

    public McpClient Client { get; }

    public static string ScenarioPath(string name) => Path.Combine(AppContext.BaseDirectory, "scenarios", name + ".json");

    public static async Task<EndToEndServer> StartAsync(FakeHostOptions? options = null, string scenario = DefaultScenario, CancellationToken cancellationToken = default)
    {
        var runtime = await FakeHostRuntime.StartAsync(FakeProScenario.Load(ScenarioPath(scenario)), options, cancellationToken).ConfigureAwait(false);
        var bridge = runtime.CreateClient();
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddSingleton<IBridgeClient>(bridge);
        builder.Services
            .AddMcpServer(McpServerSetup.ConfigureServerOptions)
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .AddArcGisProMcp();
        var host = builder.Build();
        try
        {
            await host.StartAsync(cancellationToken).ConfigureAwait(false);
            var client = await McpClient.CreateAsync(
                new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()),
                new McpClientOptions { ClientInfo = new Implementation { Name = "end-to-end-tests", Version = "1.0.0" } },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new EndToEndServer(runtime, bridge, host, clientToServer, serverToClient, client);
        }
        catch
        {
            await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
            host.Dispose();
            runtime.Dispose();
            throw;
        }
    }

    public async Task<IReadOnlyDictionary<string, Tool>> ToolsAsync(CancellationToken cancellationToken)
    {
        if (_tools is null)
        {
            var tools = await Client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            _tools = tools.ToDictionary(tool => tool.ProtocolTool.Name, tool => tool.ProtocolTool, StringComparer.Ordinal);
        }
        return _tools;
    }

    /// <summary>
    /// Calls a tool with JSON arguments, checks the shared envelope contract (structuredContent
    /// matches the outputSchema; ok agrees with isError) and returns the envelope.
    /// </summary>
    public async Task<ToolOutcome> CallAsync(string tool, object arguments, CancellationToken cancellationToken)
    {
        var element = arguments as JsonElement? ?? JsonSerializer.SerializeToElement(arguments);
        var dictionary = element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal)
            : [];
        var schema = (await ToolsAsync(cancellationToken).ConfigureAwait(false))[tool].OutputSchema
                     ?? throw new InvalidOperationException($"Tool '{tool}' has no outputSchema.");
        var call = await Client.CallToolAsync(tool, dictionary, cancellationToken: cancellationToken).ConfigureAwait(false);
        var envelope = ToolCall.AssertEnvelope(call, schema);
        return new ToolOutcome(tool, call.IsError == true, envelope);
    }

    /// <summary>Calls a tool and fails the test unless it succeeded; returns <c>result</c>.</summary>
    public async Task<JsonElement> CallOkAsync(string tool, object arguments, CancellationToken cancellationToken)
    {
        var outcome = await CallAsync(tool, arguments, cancellationToken).ConfigureAwait(false);
        Assert.False(outcome.IsError, $"{tool} failed: {outcome.Envelope.GetRawText()}");
        return outcome.Result;
    }

    /// <summary>What the person at the ArcGIS Pro panel does: approve the one pending request for an operation.</summary>
    public string ApproveAsPerson(string operationId)
    {
        var pending = Runtime.Approvals.GetPending().Where(request => request.OperationId == operationId).ToArray();
        var request = Assert.Single(pending);
        Assert.True(Runtime.Approvals.TryResolve(request.Id, ApprovalResolution.ApproveOnce));
        return request.Id;
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync().ConfigureAwait(false);
        await _clientToServer.Writer.CompleteAsync().ConfigureAwait(false);
        await _serverToClient.Writer.CompleteAsync().ConfigureAwait(false);
        await _host.StopAsync(CancellationToken.None).ConfigureAwait(false);
        _host.Dispose();
        Runtime.Dispose();
    }
}

internal sealed record ToolOutcome(string Tool, bool IsError, JsonElement Envelope)
{
    public JsonElement Result => Envelope.GetProperty("result");

    public string? ErrorCode => Envelope.GetProperty("error") is { ValueKind: JsonValueKind.Object } error
        ? error.GetProperty("code").GetString()
        : null;
}
