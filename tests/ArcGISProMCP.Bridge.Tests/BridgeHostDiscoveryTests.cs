using ArcGISProMCP.Bridge.Protocol;
using ArcGISProMCP.Bridge.Transport;
using Xunit;

namespace ArcGISProMCP.Bridge.Tests;

public sealed class BridgeHostDiscoveryTests
{
    [Fact]
    public void Process_pipe_names_are_stable_and_distinct()
    {
        Assert.Equal($"{BridgeProtocol.DefaultPipeName}.12", BridgeHostDiscovery.ProcessPipeName(12));
        Assert.NotEqual(BridgeHostDiscovery.ProcessPipeName(12), BridgeHostDiscovery.ProcessPipeName(13));
    }

    [Fact]
    public void Discovery_roundtrip_filters_non_live_records_and_preserves_project_identity()
    {
        var root = Path.Combine(Path.GetTempPath(), $"arcgis-pro-mcp-hosts-{Guid.NewGuid():N}");
        try
        {
            var first = Host(101, "One.aprx");
            var second = Host(202, "Two.aprx");
            BridgeHostDiscovery.Publish(first, root);
            BridgeHostDiscovery.Publish(second, root);

            var live = BridgeHostDiscovery.ListLive(root, host => host.ProcessId == 202);

            var selected = Assert.Single(live);
            Assert.Equal("Two.aprx", selected.ProjectName);
            Assert.Equal(BridgeHostDiscovery.ProcessPipeName(202), selected.PipeName);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Explicit_pipe_wins_over_discovery()
    {
        var hosts = new[] { Host(101, "One.aprx"), Host(202, "Two.aprx") };
        var pipe = BridgeEndpointResolver.ResolvePipeName(
            name => name == "ARCGIS_PRO_MCP_PIPE" ? "explicit.pipe" : null,
            hosts);

        Assert.Equal("explicit.pipe", pipe);
    }

    [Fact]
    public void Host_pid_selects_one_discovered_instance()
    {
        var hosts = new[] { Host(101, "One.aprx"), Host(202, "Two.aprx") };
        var pipe = BridgeEndpointResolver.ResolvePipeName(
            name => name == "ARCGIS_PRO_MCP_HOST_PID" ? "202" : null,
            hosts);

        Assert.Equal(BridgeHostDiscovery.ProcessPipeName(202), pipe);
    }

    [Fact]
    public void Multiple_unselected_hosts_fail_closed_with_actionable_choices()
    {
        var exception = Assert.Throws<BridgeException>(() =>
            BridgeEndpointResolver.ResolvePipeName(_ => null, [Host(101, "One.aprx"), Host(202, "Two.aprx")]));

        Assert.Equal("arcgis_host_ambiguous", exception.Code);
        Assert.Contains("PID 101 (One.aprx)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ARCGIS_PRO_MCP_HOST_PID", exception.Message, StringComparison.Ordinal);
    }

    private static BridgeHostRecord Host(int processId, string projectName) => new(
        processId,
        BridgeHostDiscovery.ProcessPipeName(processId),
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        projectName,
        $"C:\\Projects\\{projectName}");
}
