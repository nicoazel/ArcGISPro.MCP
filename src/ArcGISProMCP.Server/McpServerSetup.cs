using System.Reflection;
using ArcGISProMCP.Bridge.Transport;
using ArcGISProMCP.Server.Prompts;
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

    // Tools and resources are built once per process, on one thread, and shared by every host.
    //
    // Why not WithTools<T>()/WithResources<T>(): those build a fresh McpServerTool per host from the
    // shared MethodInfo. Microsoft.Extensions.AI decides which parameters are injected (IBridgeClient)
    // by looking up ParameterInfo objects by reference, and RuntimeMethodInfo caches its
    // ParameterInfo[] lazily without synchronization. When two hosts build the same tool for the
    // first time concurrently, one can bind its injected parameters against an array that the other
    // thread then replaces, so the lookup misses and "bridge" is published as a required argument.
    // Building every primitive exactly once removes the concurrent first use.
    private static readonly Lazy<McpServerTool[]> Tools = new(CreateTools, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<McpServerResource[]> Resources = new(CreateResources, LazyThreadSafetyMode.ExecutionAndPublication);

    public static void ConfigureServerOptions(McpServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ServerInfo = new() { Name = ServerName, Version = typeof(McpServerSetup).Assembly.GetName().Version!.ToString(3) };
    }

    public static IMcpServerBuilder AddArcGisProMcp(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .WithTools(Tools.Value)
            // Argument binding failures answer with the standard error envelope, not SDK text.
            .WithRequestFilters(filters => filters.AddCallToolFilter(ToolArgumentErrors.Filter))
            .WithResources(Resources.Value)
            .WithListPromptsHandler(ArcGisPrompts.ListAsync)
            .WithGetPromptHandler(ArcGisPrompts.GetAsync);
    }

    private static McpServerTool[] CreateTools() =>
        [.. PrimitiveMethods<McpServerToolAttribute>(typeof(KernelTools), typeof(SkillTools))
            .Select(method => McpServerTool.Create(method, target: null, new McpServerToolCreateOptions
            {
                Services = InjectedServices.Instance,
                // One serializer for arguments, output schemas and structured results, so the
                // advertised outputSchema describes exactly what ToolResults writes.
                SerializerOptions = ToolResults.JsonOptions
            }))];

    private static McpServerResource[] CreateResources() =>
        [.. PrimitiveMethods<McpServerResourceAttribute>(typeof(ArcGisResources))
            .Select(method => McpServerResource.Create(method, target: null, new McpServerResourceCreateOptions
            {
                Services = InjectedServices.Instance
            }))];

    private static IEnumerable<MethodInfo> PrimitiveMethods<TAttribute>(params Type[] types)
        where TAttribute : Attribute =>
        types.SelectMany(type => type
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttribute<TAttribute>() is not null)
            .OrderBy(method => method.MetadataToken));

    /// <summary>
    /// Tells the SDK, at creation time, which parameter types are resolved from the host's service
    /// provider at invocation time. Every host registers these services (see Program.cs and the test
    /// harness), so the published schemas do not depend on which host happened to build a tool.
    /// </summary>
    private sealed class InjectedServices : IServiceProvider, IServiceProviderIsService
    {
        public static readonly InjectedServices Instance = new();

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IServiceProviderIsService) ? this : null;

        public bool IsService(Type serviceType) => serviceType == typeof(IBridgeClient);
    }
}
