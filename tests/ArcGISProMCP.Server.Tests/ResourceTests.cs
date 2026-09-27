using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcGISProMCP.Server.Tests.Harness;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace ArcGISProMCP.Server.Tests;

public sealed class ResourceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Resource_and_template_lists_match_snapshot()
    {
        await using var server = await McpTestServer.StartAsync(cancellationToken: Token);

        var resources = await server.Client.ListResourcesAsync(cancellationToken: Token);
        var templates = await server.Client.ListResourceTemplatesAsync(cancellationToken: Token);

        // resources/list stays small: only the one direct resource; everything else is a template.
        Assert.Equal(["arcgis://project/state"], resources.Select(resource => resource.Uri));
        var snapshot = new JsonObject
        {
            ["resources"] = new JsonArray([.. resources.OrderBy(r => r.Uri, StringComparer.Ordinal).Select(r => (JsonNode)new JsonObject
            {
                ["uri"] = r.Uri,
                ["name"] = r.Name,
                ["title"] = r.Title,
                ["mimeType"] = r.MimeType
            })]),
            ["resourceTemplates"] = new JsonArray([.. templates.OrderBy(t => t.UriTemplate, StringComparer.Ordinal).Select(t => (JsonNode)new JsonObject
            {
                ["uriTemplate"] = t.UriTemplate,
                ["name"] = t.Name,
                ["title"] = t.Title,
                ["mimeType"] = t.MimeType
            })])
        };
        Snapshot.Match("resources-list.json", snapshot);
    }

    [Fact]
    public async Task Project_state_reads_system_get_state_as_json()
    {
        var bridge = new FakeBridgeClient().ReturnsJson("system.get_state", """{"revision":"r-7","project":{"name":"Demo"}}""");
        await using var server = await McpTestServer.StartAsync(bridge, Token);

        var result = await server.Client.ReadResourceAsync("arcgis://project/state", cancellationToken: Token);

        var text = Assert.IsType<TextResourceContents>(Assert.Single(result.Contents));
        Assert.Equal("arcgis://project/state", text.Uri);
        Assert.Equal("application/json", text.MimeType);
        Assert.Equal("r-7", JsonDocument.Parse(text.Text).RootElement.GetProperty("revision").GetString());
        Assert.Equal("system.get_state", Assert.Single(bridge.Calls).Method);
    }

    [Fact]
    public async Task Operation_template_describes_the_operation_id()
    {
        var bridge = new FakeBridgeClient().OnObject("registry.describe", p => new { id = p.GetProperty("operationId").GetString(), risk = "ReadOnly" });
        await using var server = await McpTestServer.StartAsync(bridge, Token);

        var result = await server.Client.ReadResourceAsync("arcgis://operations/layer.list", cancellationToken: Token);

        var text = Assert.IsType<TextResourceContents>(Assert.Single(result.Contents));
        Assert.Equal("arcgis://operations/layer.list", text.Uri);
        Assert.Equal("layer.list", JsonDocument.Parse(text.Text).RootElement.GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("arcgis://workflows/workflow.master-cartography", "workflow.master-cartography", null)]
    [InlineData("arcgis://workflows/workflow.master-cartography@1.1.0", "workflow.master-cartography", "1.1.0")]
    [InlineData("arcgis://workflows/workflow.master-cartography%401.1.0", "workflow.master-cartography", "1.1.0")]
    public async Task Workflow_template_supports_optional_immutable_version(string uri, string expectedId, string? expectedVersion)
    {
        var bridge = new FakeBridgeClient().OnObject("workflow.get", p => new { id = p.GetProperty("workflowId").GetString(), version = "1.1.0" });
        await using var server = await McpTestServer.StartAsync(bridge, Token);

        var result = await server.Client.ReadResourceAsync(uri, cancellationToken: Token);

        var text = Assert.IsType<TextResourceContents>(Assert.Single(result.Contents));
        Assert.Equal("application/json", text.MimeType);
        var call = Assert.Single(bridge.Calls);
        Assert.Equal("workflow.get", call.Method);
        Assert.Equal(expectedId, call.Parameters.GetProperty("workflowId").GetString());
        var version = call.Parameters.GetProperty("version");
        if (expectedVersion is null) Assert.Equal(JsonValueKind.Null, version.ValueKind);
        else Assert.Equal(expectedVersion, version.GetString());
    }

    [Fact]
    public async Task Skill_template_reads_bundled_manifest_without_the_bridge()
    {
        await using var server = await McpTestServer.StartAsync(cancellationToken: Token);

        var result = await server.Client.ReadResourceAsync("arcgis://skills/arcgis.cartography.master-plan", cancellationToken: Token);

        var text = Assert.IsType<TextResourceContents>(Assert.Single(result.Contents));
        var manifest = JsonDocument.Parse(text.Text).RootElement;
        Assert.Equal("arcgis.cartography.master-plan", manifest.GetProperty("id").GetString());
        Assert.Equal("workflow.master-cartography", manifest.GetProperty("workflowId").GetString());
        Assert.NotEqual(0, manifest.GetProperty("visualChecks").GetArrayLength());
        Assert.Empty(server.Bridge.Calls);
    }

    [Fact]
    public async Task Unknown_skill_is_a_protocol_error()
    {
        await using var server = await McpTestServer.StartAsync(cancellationToken: Token);

        var exception = await Assert.ThrowsAnyAsync<McpException>(() =>
            server.Client.ReadResourceAsync("arcgis://skills/does.not.exist", cancellationToken: Token).AsTask());

        Assert.Contains("does.not.exist", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mimeType", "image/png")]
    [InlineData("MimeType", "IMAGE/PNG")]
    public async Task Image_observation_is_returned_as_blob(string mimeTypeProperty, string mimeType)
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var bridge = new FakeBridgeClient().On("resource.read", p => JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["uri"] = p.GetProperty("uri").GetString(),
            [mimeTypeProperty] = mimeType,
            ["name"] = "capture.png",
            ["encoding"] = "base64",
            ["data"] = Convert.ToBase64String(png)
        }));
        await using var server = await McpTestServer.StartAsync(bridge, Token);

        var result = await server.Client.ReadResourceAsync("arcgis://resource/0123abcd", cancellationToken: Token);

        var blob = Assert.IsType<BlobResourceContents>(Assert.Single(result.Contents));
        Assert.Equal("arcgis://resource/0123abcd", blob.Uri);
        Assert.Equal(mimeType, blob.MimeType);
        Assert.Equal(png, blob.DecodedData.ToArray());
        Assert.Equal("arcgis://resource/0123abcd", Assert.Single(bridge.Calls).Parameters.GetProperty("uri").GetString());
    }

    [Fact]
    public async Task Json_observation_is_returned_as_decoded_text()
    {
        const string payload = """{"layers":["Zoning"]}""";
        var bridge = new FakeBridgeClient().Returns("resource.read", new
        {
            uri = "arcgis://resource/feed",
            mimeType = "application/json",
            encoding = "base64",
            data = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
        });
        await using var server = await McpTestServer.StartAsync(bridge, Token);

        var result = await server.Client.ReadResourceAsync("arcgis://resource/feed", cancellationToken: Token);

        var text = Assert.IsType<TextResourceContents>(Assert.Single(result.Contents));
        Assert.Equal("application/json", text.MimeType);
        Assert.Equal(payload, text.Text);
    }

    [Fact]
    public async Task Bridge_failure_surfaces_code_to_the_client()
    {
        var bridge = new FakeBridgeClient().Throws("registry.describe", "operation_not_found", "Unknown operation 'nope'.");
        await using var server = await McpTestServer.StartAsync(bridge, Token);

        var exception = await Assert.ThrowsAnyAsync<McpException>(() =>
            server.Client.ReadResourceAsync("arcgis://operations/nope", cancellationToken: Token).AsTask());

        Assert.Contains("operation_not_found", exception.Message, StringComparison.Ordinal);
    }
}
