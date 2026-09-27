using ArcGISProMCP.Bridge.Transport;
using ArcGISProMCP.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<IBridgeClient>(_ => new DiscoveringBridgeClient(
    TimeSpan.FromSeconds(5),
    TimeSpan.FromMinutes(15)));
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "arcgis-pro-mcp", Version = typeof(KernelTools).Assembly.GetName().Version!.ToString(3) };
    })
    .WithStdioServerTransport()
    .WithTools<KernelTools>()
    .WithTools<SkillTools>();

await builder.Build().RunAsync().ConfigureAwait(false);
