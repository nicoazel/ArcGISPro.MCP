using ArcGISProMCP.Bridge.Transport;
using ArcGISProMCP.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<IBridgeClient>(_ => new DiscoveringBridgeClient(
    TimeSpan.FromSeconds(5),
    TimeSpan.FromMinutes(15)));
builder.Services
    .AddMcpServer(McpServerSetup.ConfigureServerOptions)
    .WithStdioServerTransport()
    .AddArcGisProMcp();

await builder.Build().RunAsync().ConfigureAwait(false);
