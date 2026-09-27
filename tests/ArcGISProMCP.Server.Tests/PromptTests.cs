using System.Text.Json;
using System.Text.Json.Nodes;
using ArcGISProMCP.Server.Tests.Harness;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace ArcGISProMCP.Server.Tests;

public sealed class PromptTests
{
    private const string SkillPrompt = "skill.arcgis.cartography.master-plan";
    private const string WorkflowPrompt = "run.workflow.master-cartography";

    private const string WorkflowList = """
        [
          { "id": "workflow.master-cartography", "version": "1.1.0", "title": "Three-map master cartography", "summary": "Build three maps and a layout.", "tags": ["cartography"], "ranking": null }
        ]
        """;

    private const string WorkflowDefinition = """
        {
          "id": "workflow.master-cartography",
          "version": "1.1.0",
          "title": "Three-map master cartography",
          "summary": "Build three maps and a layout.",
          "tags": ["cartography"],
          "requiredCapabilities": ["maps", "layouts"],
          "parameters": [
            { "name": "zoningSource", "type": "path", "required": true, "description": "Zoning polygons." },
            { "name": "labelSize", "type": "number", "required": false, "defaultValue": 9, "description": "Label size in points." },
            { "name": "labelExpression", "type": "string", "required": true, "defaultValue": "$feature.NAME", "description": "Arcade label expression." }
          ],
          "steps": [
            { "id": "inspect", "operation": "project.get", "arguments": {}, "dependsOn": [] }
          ],
          "contentHash": "abc"
        }
        """;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // A current host's workflow.list entry carries the parameters, so prompts/list needs no workflow.get.
    private const string WorkflowListWithParameters = """
        [
          {
            "id": "workflow.master-cartography", "version": "1.1.0", "title": "Three-map master cartography", "summary": "Build three maps and a layout.", "tags": ["cartography"], "ranking": null,
            "parameters": [
              { "name": "zoningSource", "type": "path", "required": true, "description": "Zoning polygons." },
              { "name": "labelSize", "type": "number", "required": false, "defaultValue": 9, "description": "Label size in points." },
              { "name": "labelExpression", "type": "string", "required": true, "defaultValue": "$feature.NAME", "description": "Arcade label expression." }
            ]
          }
        ]
        """;

    private static FakeBridgeClient WorkflowBridge() => new FakeBridgeClient()
        .ReturnsJson("workflow.list", WorkflowListWithParameters)
        .ReturnsJson("workflow.get", WorkflowDefinition);

    private static FakeBridgeClient LegacyWorkflowBridge() => new FakeBridgeClient()
        .ReturnsJson("workflow.list", WorkflowList)
        .ReturnsJson("workflow.get", WorkflowDefinition);

    [Fact]
    public async Task Prompts_list_matches_snapshot_with_skills_and_workflows()
    {
        await using var server = await McpTestServer.StartAsync(WorkflowBridge(), Token);

        var prompts = await server.Client.ListPromptsAsync(cancellationToken: Token);

        var snapshot = new JsonArray();
        foreach (var prompt in prompts.OrderBy(prompt => prompt.Name, StringComparer.Ordinal))
            snapshot.Add(JsonSerializer.SerializeToNode(prompt.ProtocolPrompt, McpJsonUtilities.DefaultOptions));
        Snapshot.Match("prompts-list.json", snapshot);
        Assert.Equal([WorkflowPrompt, SkillPrompt], prompts.Select(prompt => prompt.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Prompts_list_falls_back_to_skills_when_no_host_is_running()
    {
        // Unscripted bridge methods throw BridgeException("arcgis_unavailable").
        await using var server = await McpTestServer.StartAsync(cancellationToken: Token);

        var prompts = await server.Client.ListPromptsAsync(cancellationToken: Token);

        Assert.Equal([SkillPrompt], prompts.Select(prompt => prompt.Name));
        Assert.Equal("workflow.list", Assert.Single(server.Bridge.Calls).Method);
    }

    [Fact]
    public async Task Workflow_prompt_arguments_come_from_workflow_parameters()
    {
        await using var server = await McpTestServer.StartAsync(WorkflowBridge(), Token);

        var prompts = await server.Client.ListPromptsAsync(cancellationToken: Token);

        var workflow = Assert.Single(prompts, prompt => prompt.Name == WorkflowPrompt).ProtocolPrompt;
        Assert.Equal("Three-map master cartography", workflow.Title);
        var arguments = workflow.Arguments!;
        Assert.Equal(["zoningSource", "labelSize", "labelExpression"], arguments.Select(argument => argument.Name));
        // A required parameter with a default does not force the client to ask for a value.
        Assert.Equal([true, false, false], arguments.Select(argument => argument.Required ?? false));
        // The parameters came with workflow.list: no per-workflow workflow.get (no N+1).
        Assert.Equal("workflow.list", Assert.Single(server.Bridge.Calls).Method);
    }

    [Fact]
    public async Task Workflow_prompt_arguments_fall_back_to_workflow_get_for_older_hosts()
    {
        await using var server = await McpTestServer.StartAsync(LegacyWorkflowBridge(), Token);

        var prompts = await server.Client.ListPromptsAsync(cancellationToken: Token);

        var workflow = Assert.Single(prompts, prompt => prompt.Name == WorkflowPrompt).ProtocolPrompt;
        Assert.Equal(["zoningSource", "labelSize", "labelExpression"], workflow.Arguments!.Select(argument => argument.Name));
        Assert.Equal(["workflow.list", "workflow.get"], server.Bridge.Calls.Select(call => call.Method));
    }

    [Fact]
    public async Task Skill_prompt_guides_state_workflow_run_and_visual_review()
    {
        await using var server = await McpTestServer.StartAsync(cancellationToken: Token);

        var result = await server.Client.GetPromptAsync(
            SkillPrompt,
            new Dictionary<string, object?> { ["goal"] = "Plan the riverfront site." },
            cancellationToken: Token);

        var text = MessageText(result);
        Assert.Contains("Plan the riverfront site.", text, StringComparison.Ordinal);
        AssertInOrder(text, "system_get_state", "workflow_get", "workflow_run", "resource_read");
        Assert.Contains("\"workflow.master-cartography\"", text, StringComparison.Ordinal);
        Assert.Contains("All three maps are represented in the multi-map layout.", text, StringComparison.Ordinal);
        Assert.Empty(server.Bridge.Calls);
    }

    [Fact]
    public async Task Handler_provided_workflow_prompt_can_be_fetched_with_parameters()
    {
        var bridge = WorkflowBridge();
        await using var server = await McpTestServer.StartAsync(bridge, Token);

        var result = await server.Client.GetPromptAsync(
            WorkflowPrompt,
            new Dictionary<string, object?> { ["zoningSource"] = @"C:\data\zoning.shp", ["labelSize"] = "10.5" },
            cancellationToken: Token);

        var text = MessageText(result);
        AssertInOrder(text, "system_get_state", "workflow_get", "workflow_run", "resource_read");
        Assert.Contains("version \"1.1.0\"", text, StringComparison.Ordinal);
        var parameters = ParametersBlock(text);
        Assert.Equal(@"C:\data\zoning.shp", parameters.GetProperty("zoningSource").GetString());
        Assert.Equal(10.5, parameters.GetProperty("labelSize").GetDouble());
        Assert.False(parameters.TryGetProperty("labelExpression", out _));
        // Visual checks come from the bundled skill linked to this workflow id.
        Assert.Contains("The basemap remains subordinate to thematic layers.", text, StringComparison.Ordinal);
        var call = Assert.Single(bridge.Calls, c => c.Method == "workflow.get");
        Assert.Equal("workflow.master-cartography", call.Parameters.GetProperty("workflowId").GetString());
        Assert.Equal(JsonValueKind.Null, call.Parameters.GetProperty("version").ValueKind);
    }

    [Fact]
    public async Task Workflow_prompt_accepts_an_immutable_version_suffix()
    {
        var bridge = WorkflowBridge();
        await using var server = await McpTestServer.StartAsync(bridge, Token);

        await server.Client.GetPromptAsync(
            WorkflowPrompt + "@1.1.0",
            new Dictionary<string, object?> { ["zoningSource"] = "zoning" },
            cancellationToken: Token);

        var call = Assert.Single(bridge.Calls, c => c.Method == "workflow.get");
        Assert.Equal("1.1.0", call.Parameters.GetProperty("version").GetString());
    }

    [Fact]
    public async Task Workflow_prompt_rejects_missing_required_parameters()
    {
        await using var server = await McpTestServer.StartAsync(WorkflowBridge(), Token);

        var exception = await Assert.ThrowsAnyAsync<McpException>(() =>
            server.Client.GetPromptAsync(WorkflowPrompt, cancellationToken: Token).AsTask());

        Assert.Contains("zoningSource", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("skill.missing")]
    [InlineData("unknown")]
    public async Task Unknown_prompt_is_a_protocol_error(string name)
    {
        await using var server = await McpTestServer.StartAsync(cancellationToken: Token);

        await Assert.ThrowsAnyAsync<McpException>(() =>
            server.Client.GetPromptAsync(name, cancellationToken: Token).AsTask());
    }

    [Fact]
    public async Task Workflow_prompt_without_host_reports_the_bridge_code()
    {
        await using var server = await McpTestServer.StartAsync(cancellationToken: Token);

        var exception = await Assert.ThrowsAnyAsync<McpException>(() =>
            server.Client.GetPromptAsync(WorkflowPrompt, cancellationToken: Token).AsTask());

        Assert.Contains("arcgis_unavailable", exception.Message, StringComparison.Ordinal);
    }

    private static string MessageText(GetPromptResult result)
    {
        var message = Assert.Single(result.Messages);
        Assert.Equal(Role.User, message.Role);
        return Assert.IsType<TextContentBlock>(message.Content).Text;
    }

    private static JsonElement ParametersBlock(string text)
    {
        const string fence = "```json\n";
        var start = text.IndexOf(fence, StringComparison.Ordinal) + fence.Length;
        var end = text.IndexOf("\n```", start, StringComparison.Ordinal);
        return JsonDocument.Parse(text[start..end]).RootElement.Clone();
    }

    private static void AssertInOrder(string text, params string[] fragments)
    {
        var position = 0;
        foreach (var fragment in fragments)
        {
            var index = text.IndexOf(fragment, position, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Expected '{fragment}' after position {position} in:\n{text}");
            position = index + fragment.Length;
        }
    }
}
