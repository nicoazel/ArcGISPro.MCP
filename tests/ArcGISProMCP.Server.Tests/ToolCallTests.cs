using System.Text.Json;
using ArcGISProMCP.Bridge.Protocol;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Workflows;
using ArcGISProMCP.Core.Workspaces;
using ArcGISProMCP.Server.Tests.Harness;
using ModelContextProtocol.Protocol;
using Xunit;

namespace ArcGISProMCP.Server.Tests;

public sealed class ToolCallTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly WorkspaceSnapshot Workspace = new(
        "r1", DateTimeOffset.UnixEpoch, new ProjectState("Demo", "C:/demo.aprx", false, true),
        [new MapState("map-1", "Map", "Map", 3, true)], [new LayoutState("layout-1", "Layout", 1, false)],
        "map-1", "view-1", [new CapabilityState("maps", true)]);

    private static readonly OperationDescriptor Descriptor = OperationDescriptor.Create(
        "layer.list", "List layers", "Lists layers.", JsonSchemas.EmptyObject,
        tags: ["layer"], capabilities: ["maps"], aliases: ["layers"], examples: ["{}"], related: ["map.list"]);

    private static readonly ApprovalStatusResult Pending = new(
        "req-1", "feature.delete", "1.0.0", "r1", "pending", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(2), null, "Approve in the panel.");

    private static readonly WorkflowDefinition Workflow = new(
        "flow", "1.0.0", "Flow", "A flow.", ["demo"], ["maps"],
        [new WorkflowParameter("title", "string", true, null, "Layout title")],
        [new WorkflowStep("one", "layer.list", JsonSerializer.SerializeToElement(new { }), [])]);

    private static readonly WorkflowRunResult FailedRun = new(
        false, null, null, "flow", "1.0.0", null, null, null, null,
        [WorkflowStepResult.From("one", "layer.list", OperationResult.Fail("layer_not_found", "No such layer.", "r2")),
         WorkflowStepResult.Skipped("two", "layer.list", "dependency_failed", "Skipped because a required step failed.")],
        "r2", null);

    /// <summary>A realistic bridge result for every bridge-backed tool, keyed by tool name.</summary>
    public static TheoryData<string> BridgeTools => [.. Scripts.Keys];

    private static readonly Dictionary<string, (string Method, object Result, Dictionary<string, object?> Arguments)> Scripts = new(StringComparer.Ordinal)
    {
        ["system_get_state"] = ("system.get_state", new SystemStateResult(1, 42, Workspace, 70, 0, true), []),
        ["registry_search"] = ("registry.search", new[] { new RegistrySearchHit(OperationSummary.From(Descriptor), 3.5, ["layer"]) }, new() { ["query"] = "layers" }),
        ["registry_browse"] = ("registry.browse", new RegistryBrowseResult(70, [new RegistryDomainCount("layer", 9)], null, null), []),
        ["registry_describe"] = ("registry.describe", OperationDescription.From(Descriptor), new() { ["operationId"] = "layer.list" }),
        ["registry_validate"] = ("registry.validate", new RegistryValidationResult(false, OperationSummary.From(Descriptor), "r1", [new ValidationIssue("invalid_arguments", "$.x: unexpected")], false),
            new() { ["operationId"] = "layer.list", ["arguments"] = new { } }),
        ["registry_invoke"] = ("registry.invoke", OperationResult.Ok(JsonSerializer.SerializeToElement(new { layers = new[] { "Parcels" } }), "r1",
            [new OperationNotice("note", "A note.")], [new ResourceHandle("arcgis://resource/abc", "image/png", "view")]),
            new() { ["operationId"] = "layer.list", ["arguments"] = new { } }),
        ["workflow_list"] = ("workflow.list", new[] { new WorkflowSummary("flow", "1.0.0", "Flow", "A flow.", ["demo"], new WorkflowRanking("flow", "1.0.0", 2, 1, 0, 0.5),
            [new WorkflowParameter("label", "string", true, null, "A label.")]) }, []),
        ["approval_request"] = ("approval.request", Pending, new() { ["operationId"] = "feature.delete", ["arguments"] = new { }, ["expectedRevision"] = "r1" }),
        ["approval_status"] = ("approval.status", Pending with { Status = "approved", ConfirmationToken = "token", Instructions = null }, new() { ["requestId"] = "req-1" }),
        ["approval_cancel"] = ("approval.cancel", new ApprovalCancelResult("req-1", true), new() { ["requestId"] = "req-1" }),
        ["workflow_get"] = ("workflow.get", Workflow, new() { ["workflowId"] = "flow" }),
        ["workflow_save"] = ("workflow.save", new WorkflowSaveResult(true, "flow", "1.0.1"), new() { ["workflow"] = Workflow }),
        ["workflow_run"] = ("workflow.run", FailedRun with { Success = true, Results = [WorkflowStepResult.From("one", "layer.list", OperationResult.Ok(null, "r1"))], Revision = "r1" },
            new() { ["workflowId"] = "flow", ["parameters"] = new { title = "T" } }),
        ["resource_read"] = ("resource.read", new ResourceContent("arcgis://resource/abc", "application/json", "layers", "base64",
            Convert.ToBase64String("""{"layers":[]}"""u8.ToArray()), DateTimeOffset.UnixEpoch), new() { ["uri"] = "arcgis://resource/abc" }),
    };

    [Theory]
    [MemberData(nameof(BridgeTools))]
    public async Task Successful_calls_return_structured_content_that_matches_the_output_schema(string toolName)
    {
        var (method, result, arguments) = Scripts[toolName];
        await using var server = await McpTestServer.StartAsync(new FakeBridgeClient().Returns(method, result), Token);

        var (call, schema) = await CallAsync(server, toolName, arguments);

        var envelope = AssertEnvelope(call, schema);
        Assert.NotEqual(true, call.IsError);
        Assert.True(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("error").ValueKind);
        // The typed result carries the bridge result unchanged.
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(result, BridgeJson.Options), envelope.GetProperty("result")),
            envelope.GetProperty("result").GetRawText());
    }

    [Theory]
    [InlineData("skill_search", "{\"query\":\"cartography\"}")]
    [InlineData("skill_get", "{\"skillId\":\"arcgis.cartography.master-plan\"}")]
    public async Task Skill_tools_return_structured_content_that_matches_the_output_schema(string toolName, string arguments)
    {
        await using var server = await McpTestServer.StartAsync(cancellationToken: Token);

        var (call, schema) = await CallAsync(server, toolName, JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments)!);

        var envelope = AssertEnvelope(call, schema);
        Assert.True(envelope.GetProperty("ok").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, envelope.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task Bridge_failure_is_an_error_envelope_with_code_and_retryability()
    {
        var bridge = new FakeBridgeClient().Throws("registry.describe", "operation_not_found", "Unknown operation 'x'.");
        await using var server = await McpTestServer.StartAsync(bridge, Token);

        var (call, schema) = await CallAsync(server, "registry_describe", new() { ["operationId"] = "x" });
        var (unavailable, stateSchema) = await CallAsync(server, "system_get_state", []);

        Assert.True(call.IsError);
        var envelope = AssertEnvelope(call, schema);
        Assert.False(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, envelope.GetProperty("result").ValueKind);
        Assert.Equal("operation_not_found", envelope.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Unknown operation 'x'.", envelope.GetProperty("error").GetProperty("message").GetString());
        Assert.False(envelope.GetProperty("error").GetProperty("retryable").GetBoolean());

        // An unreachable host (unscripted method in the fake) is retryable.
        Assert.True(unavailable.IsError);
        var error = AssertEnvelope(unavailable, stateSchema).GetProperty("error");
        Assert.Equal("arcgis_unavailable", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public async Task Invoke_that_reports_failure_is_an_error_and_keeps_the_operation_result()
    {
        var bridge = new FakeBridgeClient().Returns("registry.invoke",
            OperationResult.Fail("workspace_revision_mismatch", "Refresh state before writing.", "r9"));
        await using var server = await McpTestServer.StartAsync(bridge, Token);

        var (call, schema) = await CallAsync(server, "registry_invoke",
            new() { ["operationId"] = "layer.remove", ["arguments"] = new { }, ["expectedRevision"] = "r1" });

        Assert.True(call.IsError);
        var envelope = AssertEnvelope(call, schema);
        Assert.False(envelope.GetProperty("ok").GetBoolean());
        var result = envelope.GetProperty("result");
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("workspace_revision_mismatch", result.GetProperty("errorCode").GetString());
        var error = envelope.GetProperty("error");
        Assert.Equal("workspace_revision_mismatch", error.GetProperty("code").GetString());
        Assert.Equal("Refresh state before writing.", error.GetProperty("message").GetString());
        Assert.Equal("r9", error.GetProperty("revision").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public async Task Workflow_run_that_reports_failure_is_an_error_and_keeps_every_step()
    {
        await using var server = await McpTestServer.StartAsync(new FakeBridgeClient().Returns("workflow.run", FailedRun), Token);

        var (call, schema) = await CallAsync(server, "workflow_run",
            new() { ["workflowId"] = "flow", ["parameters"] = new { }, ["expectedRevision"] = "r1" });

        Assert.True(call.IsError);
        var envelope = AssertEnvelope(call, schema);
        Assert.Equal(2, envelope.GetProperty("result").GetProperty("results").GetArrayLength());
        Assert.Equal("workflow_step_failed", envelope.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("r2", envelope.GetProperty("error").GetProperty("revision").GetString());
    }

    [Fact]
    public async Task Unexpected_bridge_result_shape_is_a_contract_error()
    {
        await using var server = await McpTestServer.StartAsync(new FakeBridgeClient().ReturnsJson("registry.describe", "[1, 2]"), Token);

        var (call, schema) = await CallAsync(server, "registry_describe", new() { ["operationId"] = "layer.list" });

        Assert.True(call.IsError);
        Assert.Equal("bridge_contract_mismatch", AssertEnvelope(call, schema).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unknown_skill_is_an_error_envelope()
    {
        await using var server = await McpTestServer.StartAsync(cancellationToken: Token);

        var (call, schema) = await CallAsync(server, "skill_get", new() { ["skillId"] = "no.such.skill" });

        Assert.True(call.IsError);
        Assert.Equal("skill_not_found", AssertEnvelope(call, schema).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Approval_status_forwards_wait_seconds_to_the_host()
    {
        var bridge = new FakeBridgeClient().Returns("approval.status", Pending);
        await using var server = await McpTestServer.StartAsync(bridge, Token);

        await CallAsync(server, "approval_status", new() { ["requestId"] = "req-1", ["waitSeconds"] = 45 });
        await CallAsync(server, "approval_status", new() { ["requestId"] = "req-1" });

        var calls = bridge.Calls.Where(call => call.Method == "approval.status").ToArray();
        Assert.Equal(45, calls[0].Parameters.GetProperty("waitSeconds").GetInt32());
        Assert.Equal(0, calls[1].Parameters.GetProperty("waitSeconds").GetInt32());
    }

    [Fact]
    public async Task Invoke_forwards_dry_run_and_a_completed_dry_run_is_not_an_error()
    {
        // A dry run that ran its validation succeeds even when the verdict is invalid.
        var bridge = new FakeBridgeClient().Returns("registry.invoke",
            OperationResult.Ok(JsonSerializer.SerializeToElement(new { valid = false, dryRun = true }), "r1"));
        await using var server = await McpTestServer.StartAsync(bridge, Token);

        var (call, schema) = await CallAsync(server, "registry_invoke",
            new() { ["operationId"] = "gp.run", ["arguments"] = new { }, ["dryRun"] = true });
        await CallAsync(server, "registry_invoke", new() { ["operationId"] = "gp.run", ["arguments"] = new { } });

        Assert.NotEqual(true, call.IsError);
        var envelope = AssertEnvelope(call, schema);
        Assert.False(envelope.GetProperty("result").GetProperty("data").GetProperty("valid").GetBoolean());
        var calls = bridge.Calls.Where(c => c.Method == "registry.invoke").ToArray();
        Assert.True(calls[0].Parameters.GetProperty("dryRun").GetBoolean());
        Assert.False(calls[1].Parameters.GetProperty("dryRun").GetBoolean());
    }

    [Fact]
    public async Task Image_resource_is_an_image_block_and_structured_metadata_without_the_payload()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47];
        var bridge = new FakeBridgeClient().Returns("resource.read",
            new ResourceContent("arcgis://resource/img", "image/png", "view", "base64", Convert.ToBase64String(png), DateTimeOffset.UnixEpoch));
        await using var server = await McpTestServer.StartAsync(bridge, Token);

        var (call, schema) = await CallAsync(server, "resource_read", new() { ["uri"] = "arcgis://resource/img" });

        var image = Assert.IsType<ImageContentBlock>(call.Content[0]);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(png, image.DecodedData.ToArray());
        var result = AssertEnvelope(call, schema).GetProperty("result");
        Assert.Equal("image/png", result.GetProperty("mimeType").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("data").ValueKind);
    }

    private static async Task<(CallToolResult Call, JsonElement OutputSchema)> CallAsync(
        McpTestServer server, string toolName, Dictionary<string, object?> arguments)
    {
        var tools = await server.Client.ListToolsAsync(cancellationToken: Token);
        var schema = tools.Single(tool => tool.Name == toolName).ProtocolTool.OutputSchema
                     ?? throw new InvalidOperationException($"Tool '{toolName}' has no outputSchema.");
        var call = await server.Client.CallToolAsync(toolName, arguments, cancellationToken: Token);
        return (call, schema);
    }

    /// <summary>
    /// Asserts the envelope contract shared by every tool: structuredContent validates against the
    /// tool's outputSchema, ok agrees with isError, and the last text block is the same JSON.
    /// </summary>
    private static JsonElement AssertEnvelope(CallToolResult call, JsonElement outputSchema)
    {
        var structured = call.StructuredContent ?? throw new InvalidOperationException("structuredContent is missing.");
        SchemaAssert.Valid(outputSchema, structured);
        Assert.Equal(call.IsError == true, !structured.GetProperty("ok").GetBoolean());
        var text = Assert.IsType<TextContentBlock>(call.Content[^1]);
        Assert.True(JsonElement.DeepEquals(structured, JsonDocument.Parse(text.Text).RootElement));
        return structured;
    }
}
