using System.Text.Json;
using System.Text.Json.Nodes;
using ArcGISProMCP.Server.Tests.Harness;
using ModelContextProtocol;
using Xunit;

namespace ArcGISProMCP.Server.Tests;

public sealed class ToolListSnapshotTests
{
    [Fact]
    public async Task Tools_list_exposes_exactly_sixteen_uniquely_named_tools()
    {
        await using var server = await McpTestServer.StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var tools = await server.Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(16, tools.Count);
        Assert.Equal(tools.Count, tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Concurrently_started_hosts_never_publish_the_injected_bridge_as_an_argument()
    {
        // Regression: hosts that built their tools concurrently could publish IBridgeClient as a
        // required "bridge" argument (see McpServerSetup). Tests now run in parallel again.
        var schemas = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var server = await McpTestServer.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
            var tools = await server.Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
            return tools.Select(tool => (tool.Name, Schema: tool.JsonSchema)).ToArray();
        }));

        foreach (var (name, schema) in schemas.SelectMany(list => list))
        {
            Assert.False(
                schema.TryGetProperty("properties", out var properties) && properties.TryGetProperty("bridge", out _),
                $"Tool '{name}' publishes the injected bridge as an argument.");
        }
    }

    [Fact]
    public async Task Tools_list_names_annotations_and_schemas_match_snapshot()
    {
        await using var server = await McpTestServer.StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var tools = await server.Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Names, titles, annotations and schemas are the contract clients rely on. Descriptions are
        // prose and deliberately left out so wording edits do not churn the snapshot.
        var snapshot = new JsonArray();
        foreach (var tool in tools.OrderBy(tool => tool.Name, StringComparer.Ordinal))
        {
            var node = JsonSerializer.SerializeToNode(tool.ProtocolTool, McpJsonUtilities.DefaultOptions)!.AsObject();
            snapshot.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["title"] = node["title"]?.DeepClone(),
                ["annotations"] = node["annotations"]?.DeepClone(),
                ["inputSchema"] = node["inputSchema"]?.DeepClone(),
                ["outputSchema"] = node["outputSchema"]?.DeepClone()
            });
        }

        Snapshot.Match("tools-list.json", snapshot);
    }
}
