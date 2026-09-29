using System.Text.Json;
using ModelContextProtocol.Protocol;
using Xunit;

namespace ArcGISProMCP.Server.Tests.Harness;

/// <summary>Calls tools through the MCP client and checks the envelope contract every tool shares.</summary>
public static class ToolCall
{
    /// <summary>Calls <paramref name="toolName"/> and returns the result with the tool's advertised outputSchema.</summary>
    public static async Task<(CallToolResult Call, JsonElement OutputSchema)> CallAsync(
        McpTestServer server, string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        var tools = await server.Client.ListToolsAsync(cancellationToken: cancellationToken);
        var schema = tools.Single(tool => tool.Name == toolName).ProtocolTool.OutputSchema
                     ?? throw new InvalidOperationException($"Tool '{toolName}' has no outputSchema.");
        var call = await server.Client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken);
        return (call, schema);
    }

    /// <summary>
    /// Asserts the envelope contract shared by every tool: structuredContent validates against the
    /// tool's outputSchema, ok agrees with isError, and the last text block is the same JSON.
    /// </summary>
    public static JsonElement AssertEnvelope(CallToolResult call, JsonElement outputSchema)
    {
        ArgumentNullException.ThrowIfNull(call);
        var structured = call.StructuredContent ?? throw new InvalidOperationException("structuredContent is missing.");
        SchemaAssert.Valid(outputSchema, structured);
        Assert.Equal(call.IsError == true, !structured.GetProperty("ok").GetBoolean());
        var text = Assert.IsType<TextContentBlock>(call.Content[^1]);
        Assert.True(JsonElement.DeepEquals(structured, JsonDocument.Parse(text.Text).RootElement));
        return structured;
    }
}
