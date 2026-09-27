using ArcGISProMCP.Server.Resources;
using ArcGISProMCP.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace ArcGISProMCP.Server;

/// <summary>
/// Single place that describes the MCP surface (server info, tools, resources and prompts) so the
/// stdio host and the in-process test harness register exactly the same primitives.
/// </summary>
public static class McpServerSetup
{
    public const string ServerName = "arcgis-pro-mcp";

    public static void ConfigureServerOptions(McpServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ServerInfo = new() { Name = ServerName, Version = typeof(McpServerSetup).Assembly.GetName().Version!.ToString(3) };
    }

    public static IMcpServerBuilder AddArcGisProMcp(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .WithTools<KernelTools>()
            .WithTools<SkillTools>()
            .WithResources<ArcGisResources>();
    }
}
