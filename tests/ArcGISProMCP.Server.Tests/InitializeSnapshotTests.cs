using System.Text.Json;
using System.Text.Json.Nodes;
using ArcGISProMCP.Server.Tests.Harness;
using ModelContextProtocol;
using Xunit;

namespace ArcGISProMCP.Server.Tests;

public sealed class InitializeSnapshotTests
{
    private const string VersionPlaceholder = "<assembly-version>";

    [Fact]
    public async Task Initialize_result_matches_snapshot()
    {
        await using var server = await McpTestServer.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        var client = server.Client;

        // The version follows Directory.Build.props, so it is checked against the assembly and then
        // replaced by a placeholder; a release bump must not churn the snapshot.
        var expectedVersion = typeof(McpServerSetup).Assembly.GetName().Version!.ToString(3);
        Assert.Equal(McpServerSetup.ServerName, client.ServerInfo.Name);
        Assert.Equal(expectedVersion, client.ServerInfo.Version);

        var serverInfo = JsonSerializer.SerializeToNode(client.ServerInfo, McpJsonUtilities.DefaultOptions)!.AsObject();
        serverInfo["version"] = VersionPlaceholder;
        var snapshot = new JsonObject
        {
            ["protocolVersion"] = client.NegotiatedProtocolVersion,
            ["serverInfo"] = serverInfo,
            ["capabilities"] = JsonSerializer.SerializeToNode(client.ServerCapabilities, McpJsonUtilities.DefaultOptions),
            ["instructions"] = client.ServerInstructions
        };

        Snapshot.Match("initialize.json", snapshot);
    }
}
