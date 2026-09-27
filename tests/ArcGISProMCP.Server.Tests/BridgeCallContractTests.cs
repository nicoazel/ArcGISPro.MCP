using System.Text.Json;
using System.Text.Json.Nodes;
using ArcGISProMCP.Server.Tests.Harness;
using Xunit;

namespace ArcGISProMCP.Server.Tests;

/// <summary>
/// The gateway-to-add-in contract: for each tool, the exact bridge method and parameter JSON the
/// gateway sends. The add-in reads these names (ProBridgeRequestHandler), so a rename or a dropped
/// pass-through argument must show up as a snapshot diff here rather than as a live failure.
/// Omitted optional arguments are sent as explicit nulls (or the tool's default for value types);
/// the add-in treats a null member the same as an absent one.
/// </summary>
public sealed class BridgeCallContractTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_tool_is_covered_by_a_bridge_call_case()
    {
        await using var server = await McpTestServer.StartAsync(cancellationToken: Token);
        var tools = await server.Client.ListToolsAsync(cancellationToken: Token);

        Assert.Equal(
            tools.Select(tool => tool.Name).Order(StringComparer.Ordinal),
            ToolCases.All.Select(item => item.Tool).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Bridge_methods_and_parameters_match_snapshot()
    {
        var snapshot = new JsonArray();
        foreach (var item in ToolCases.All)
        {
            // An unscripted fake fails every call like an absent host; the calls are still recorded.
            await using var server = await McpTestServer.StartAsync(cancellationToken: Token);
            await server.Client.CallToolAsync(item.Tool, item.Arguments, cancellationToken: Token);

            var calls = new JsonArray();
            foreach (var call in server.Bridge.Calls)
            {
                AssertCamelCase(call.Parameters, $"{item.Tool}/{item.Name} -> {call.Method}");
                calls.Add(new JsonObject
                {
                    ["method"] = call.Method,
                    ["parameters"] = JsonNode.Parse(call.Parameters.GetRawText())
                });
            }

            if (ToolCases.LocalTools.Contains(item.Tool))
                Assert.Empty(calls);
            else
                Assert.Single(calls);

            snapshot.Add(new JsonObject
            {
                ["tool"] = item.Tool,
                ["case"] = item.Name,
                ["arguments"] = JsonNode.Parse(item.ArgumentsJson),
                ["calls"] = calls
            });
        }

        Snapshot.Match("bridge-calls.json", snapshot);
    }

    [Fact]
    public async Task Supplied_arguments_reach_the_bridge_unchanged()
    {
        // Independent of the snapshot: every argument a caller supplies is forwarded under the same
        // name with the same value, so pass-through fields (expectedRevision, idempotencyKey, dryRun,
        // confirmationToken, waitSeconds, ...) cannot be dropped, renamed or rewritten.
        foreach (var item in ToolCases.All.Where(item => !ToolCases.LocalTools.Contains(item.Tool)))
        {
            await using var server = await McpTestServer.StartAsync(cancellationToken: Token);
            await server.Client.CallToolAsync(item.Tool, item.Arguments, cancellationToken: Token);

            var parameters = Assert.Single(server.Bridge.Calls).Parameters;
            foreach (var argument in item.ArgumentsElement.EnumerateObject())
            {
                Assert.True(parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(argument.Name, out _),
                    $"{item.Tool}/{item.Name}: '{argument.Name}' was not sent to the bridge.");
                var sent = parameters.GetProperty(argument.Name);
                Assert.True(JsonElement.DeepEquals(argument.Value, sent),
                    $"{item.Tool}/{item.Name}: '{argument.Name}' was sent as {sent.GetRawText()}.");
            }
        }
    }

    private static void AssertCamelCase(JsonElement parameters, string context)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return;
        foreach (var property in parameters.EnumerateObject())
            Assert.True(char.IsLower(property.Name[0]), $"{context}: parameter '{property.Name}' is not camelCase.");
    }
}
