using System.Text.Json;
using ArcGISProMCP.Server.Tests.Harness;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace ArcGISProMCP.Server.Tests;

/// <summary>
/// Error paths of the tools/call contract. Success envelopes of all 16 tools are covered by
/// <see cref="ToolCallTests"/>; this class covers the failure envelope of every bridge-backed tool
/// and how malformed calls are rejected.
/// </summary>
public sealed class ToolErrorContractTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<string> BridgeTools => [.. ToolCases.BridgeTools];

    [Theory]
    [MemberData(nameof(BridgeTools))]
    public async Task Bridge_exception_is_an_error_envelope_that_matches_the_output_schema(string toolName)
    {
        var item = ToolCases.All.First(candidate => candidate.Tool == toolName);
        var method = await BridgeMethodAsync(item);
        var bridge = new FakeBridgeClient().Throws(method, "workspace_revision_mismatch", "Refresh state before writing.");
        await using var server = await McpTestServer.StartAsync(bridge, Token);

        var (call, schema) = await CallAsync(server, toolName, item.Arguments);

        Assert.True(call.IsError);
        var envelope = AssertEnvelope(call, schema);
        Assert.False(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("result").ValueKind);
        var error = envelope.GetProperty("error");
        Assert.Equal("workspace_revision_mismatch", error.GetProperty("code").GetString());
        Assert.Equal("Refresh state before writing.", error.GetProperty("message").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
    }

    [Theory]
    [MemberData(nameof(BridgeTools))]
    public async Task Unreadable_bridge_result_is_a_contract_error_that_matches_the_output_schema(string toolName)
    {
        var item = ToolCases.All.First(candidate => candidate.Tool == toolName);
        var method = await BridgeMethodAsync(item);
        await using var server = await McpTestServer.StartAsync(new FakeBridgeClient().ReturnsJson(method, "\"not a result\""), Token);

        var (call, schema) = await CallAsync(server, toolName, item.Arguments);

        Assert.True(call.IsError);
        var error = AssertEnvelope(call, schema).GetProperty("error");
        Assert.Equal("bridge_contract_mismatch", error.GetProperty("code").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
    }

    [Theory]
    [InlineData("missing operationId", """{"arguments":{}}""")]
    [InlineData("missing arguments", """{"operationId":"layer.list"}""")]
    [InlineData("no arguments at all", "{}")]
    [InlineData("operationId is not a string", """{"operationId":5,"arguments":{}}""")]
    [InlineData("dryRun is not a boolean", """{"operationId":"layer.list","arguments":{},"dryRun":"yes"}""")]
    [InlineData("expectedRevision is not a string", """{"operationId":"layer.list","arguments":{},"expectedRevision":7}""")]
    public async Task Registry_invoke_with_malformed_arguments_is_a_tool_error_and_never_reaches_the_host(string because, string arguments)
    {
        await using var server = await McpTestServer.StartAsync(cancellationToken: Token);

        var (call, schema) = await CallAsync(server, "registry_invoke",
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments)!.ToDictionary(pair => pair.Key, pair => (object?)pair.Value));

        // The SDK reports argument binding failures as a tool execution error (isError), not as a
        // JSON-RPC invalid-params error, so the model can correct the call.
        Assert.True(call.IsError, because);
        Assert.Empty(server.Bridge.Calls);
        // Binding fails before the tool runs, so there is currently no envelope; if one is added it
        // must still match the advertised outputSchema.
        if (call.StructuredContent is { } structured)
            SchemaAssert.Valid(schema, structured);
    }

    [Fact]
    public async Task Unknown_tool_is_a_protocol_invalid_params_error()
    {
        await using var server = await McpTestServer.StartAsync(cancellationToken: Token);

        var exception = await Assert.ThrowsAsync<McpProtocolException>(() =>
            server.Client.CallToolAsync("registry_invoke_v2", new Dictionary<string, object?>(), cancellationToken: Token).AsTask());

        Assert.Equal(McpErrorCode.InvalidParams, exception.ErrorCode);
        Assert.Empty(server.Bridge.Calls);
    }

    [Fact]
    public async Task Dry_run_with_idempotency_key_is_forwarded_and_the_host_conflict_is_an_error_envelope()
    {
        // The add-in rejects the combination (ProBridgeRequestHandler); the gateway forwards both
        // flags unchanged and reports the host's error as a non-retryable envelope.
        var bridge = new FakeBridgeClient().Throws("registry.invoke", "dry_run_idempotency_conflict",
            "dryRun cannot be combined with idempotencyKey; a dry run executes nothing and is never cached.");
        await using var server = await McpTestServer.StartAsync(bridge, Token);

        var (call, schema) = await CallAsync(server, "registry_invoke",
            new() { ["operationId"] = "gp.run", ["arguments"] = new { }, ["dryRun"] = true, ["idempotencyKey"] = "key-1" });

        Assert.True(call.IsError);
        var error = AssertEnvelope(call, schema).GetProperty("error");
        Assert.Equal("dry_run_idempotency_conflict", error.GetProperty("code").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        var parameters = Assert.Single(bridge.Calls).Parameters;
        Assert.True(parameters.GetProperty("dryRun").GetBoolean());
        Assert.Equal("key-1", parameters.GetProperty("idempotencyKey").GetString());
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(0)]
    [InlineData(120)]
    [InlineData(121)]
    [InlineData(3600)]
    public async Task Approval_status_leaves_wait_seconds_clamping_to_the_host(int waitSeconds)
    {
        // The documented 0..120 range is enforced by the add-in (Math.Clamp in
        // ProBridgeRequestHandler), which also caps concurrent waits; the gateway forwards the value
        // unchanged so both ends cannot disagree about the bound.
        var bridge = new FakeBridgeClient().Throws("approval.status", "approval_not_found", "Unknown request.");
        await using var server = await McpTestServer.StartAsync(bridge, Token);

        await server.Client.CallToolAsync("approval_status",
            new Dictionary<string, object?> { ["requestId"] = "req-1", ["waitSeconds"] = waitSeconds }, cancellationToken: Token);

        Assert.Equal(waitSeconds, Assert.Single(bridge.Calls).Parameters.GetProperty("waitSeconds").GetInt32());
    }

    /// <summary>Discovers which bridge method a tool calls by invoking it against an unscripted fake.</summary>
    private static async Task<string> BridgeMethodAsync(ToolCases.ToolCase item)
    {
        await using var server = await McpTestServer.StartAsync(cancellationToken: Token);
        await server.Client.CallToolAsync(item.Tool, item.Arguments, cancellationToken: Token);
        return Assert.Single(server.Bridge.Calls).Method;
    }

    private static Task<(CallToolResult Call, JsonElement OutputSchema)> CallAsync(
        McpTestServer server, string toolName, Dictionary<string, object?> arguments) =>
        ToolCall.CallAsync(server, toolName, arguments, Token);

    private static JsonElement AssertEnvelope(CallToolResult call, JsonElement outputSchema) =>
        ToolCall.AssertEnvelope(call, outputSchema);
}
