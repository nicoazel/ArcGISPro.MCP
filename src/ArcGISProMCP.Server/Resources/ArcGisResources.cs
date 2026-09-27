using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ArcGISProMCP.Bridge.Protocol;
using ArcGISProMCP.Bridge.Transport;
using ArcGISProMCP.Server.Skills;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArcGISProMCP.Server.Resources;

/// <summary>
/// Read-only MCP resources. One direct resource (project state) keeps resources/list small; everything
/// else is addressed through URI templates that mirror the read-only tools.
/// </summary>
[McpServerResourceType]
public sealed class ArcGisResources
{
    private const string JsonMimeType = "application/json";
    private const string ProjectStateUri = "arcgis://project/state";

    [McpServerResource(UriTemplate = ProjectStateUri, Name = "project_state", Title = "ArcGIS Pro project state", MimeType = JsonMimeType)]
    [Description("Compact revisioned snapshot of the open ArcGIS Pro project, maps, layouts, active view, capabilities and connection (same as system_get_state).")]
    public static async Task<ResourceContents> ProjectState(IBridgeClient bridge, CancellationToken cancellationToken)
    {
        var state = await CallAsync(bridge, "system.get_state", null, ProjectStateUri, cancellationToken).ConfigureAwait(false);
        return Json(ProjectStateUri, state.GetRawText());
    }

    [McpServerResource(UriTemplate = "arcgis://operations/{id}", Name = "operation", Title = "ArcGIS operation descriptor", MimeType = JsonMimeType)]
    [Description("Complete registry descriptor for one operation id (same as registry_describe).")]
    public static async Task<ResourceContents> Operation(IBridgeClient bridge, string id, CancellationToken cancellationToken)
    {
        var uri = "arcgis://operations/" + id;
        var descriptor = await CallAsync(bridge, "registry.describe", new { operationId = Decode(id) }, uri, cancellationToken).ConfigureAwait(false);
        return Json(uri, descriptor.GetRawText());
    }

    [McpServerResource(UriTemplate = "arcgis://workflows/{id}", Name = "workflow", Title = "ArcGIS workflow definition", MimeType = JsonMimeType)]
    [Description("One saved workflow definition (same as workflow_get). Use 'id' for the latest version or 'id@version' for an immutable version.")]
    public static async Task<ResourceContents> Workflow(IBridgeClient bridge, string id, CancellationToken cancellationToken)
    {
        var uri = "arcgis://workflows/" + id;
        var (workflowId, version) = SplitVersion(Decode(id));
        var workflow = await CallAsync(bridge, "workflow.get", new { workflowId, version }, uri, cancellationToken).ConfigureAwait(false);
        return Json(uri, workflow.GetRawText());
    }

    [McpServerResource(UriTemplate = "arcgis://skills/{id}", Name = "skill", Title = "ArcGIS skill manifest", MimeType = JsonMimeType)]
    [Description("One bundled skill manifest (same as skill_get): preconditions, allowed operations, visual checks, recovery guidance and linked workflow.")]
    public static async Task<ResourceContents> Skill(string id, CancellationToken cancellationToken)
    {
        var uri = "arcgis://skills/" + id;
        var skillId = Decode(id);
        var skill = await SkillCatalog.FindAsync(skillId, cancellationToken).ConfigureAwait(false)
            ?? throw new McpProtocolException($"Skill '{skillId}' was not found.", McpErrorCode.ResourceNotFound);
        return Json(uri, JsonSerializer.Serialize(skill, SkillCatalog.JsonOptions));
    }

    [McpServerResource(UriTemplate = "arcgis://resource/{id}", Name = "observation", Title = "ArcGIS observation")]
    [Description("A bounded semantic or image observation previously returned as an arcgis://resource/ handle (same as resource_read). Images are returned as blobs, textual observations as text.")]
    public static async Task<ResourceContents> Observation(IBridgeClient bridge, string id, CancellationToken cancellationToken)
    {
        var uri = "arcgis://resource/" + id;
        var resource = await CallAsync(bridge, "resource.read", new { uri }, uri, cancellationToken).ConfigureAwait(false);
        var mimeType = GetString(resource, "mimeType") ?? "application/octet-stream";
        var data = GetString(resource, "data");
        if (data is null)
            return Json(uri, resource.GetRawText());

        var base64 = string.Equals(GetString(resource, "encoding") ?? "base64", "base64", StringComparison.OrdinalIgnoreCase);
        if (!base64)
            return new TextResourceContents { Uri = uri, MimeType = mimeType, Text = data };

        var bytes = Convert.FromBase64String(data);
        if (mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || !IsTextual(mimeType))
            return BlobResourceContents.FromBytes(bytes, uri, mimeType);
        return new TextResourceContents { Uri = uri, MimeType = mimeType, Text = Encoding.UTF8.GetString(bytes) };
    }

    internal static (string Id, string? Version) SplitVersion(string value)
    {
        var at = value.LastIndexOf('@');
        return at > 0 && at < value.Length - 1 ? (value[..at], value[(at + 1)..]) : (value, null);
    }

    private static TextResourceContents Json(string uri, string text) =>
        new() { Uri = uri, MimeType = JsonMimeType, Text = text };

    private static string Decode(string value) => Uri.UnescapeDataString(value);

    private static bool IsTextual(string mimeType)
    {
        var type = mimeType.Split(';', 2)[0].Trim();
        return type.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || type.EndsWith("/json", StringComparison.OrdinalIgnoreCase)
            || type.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
            || type.EndsWith("/xml", StringComparison.OrdinalIgnoreCase)
            || type.EndsWith("+xml", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
        }
        return null;
    }

    private static async Task<JsonElement> CallAsync(
        IBridgeClient bridge,
        string method,
        object? parameters,
        string uri,
        CancellationToken cancellationToken)
    {
        try
        {
            return await bridge.CallAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (BridgeException exception)
        {
            // Surface the bridge code to the client; unknown ids map to the MCP "resource not found" error.
            var code = exception.Code.EndsWith("_not_found", StringComparison.Ordinal)
                ? McpErrorCode.ResourceNotFound
                : McpErrorCode.InternalError;
            throw new McpProtocolException($"{exception.Code}: {exception.Message} ({uri})", exception, code);
        }
    }
}
