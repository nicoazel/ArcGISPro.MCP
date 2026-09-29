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

    [Fact]
    public void Records_without_a_host_kind_are_arcgis_pro()
    {
        var root = Path.Combine(Path.GetTempPath(), $"arcgis-pro-mcp-hosts-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            // A record written before hostKind existed.
            File.WriteAllText(Path.Combine(root, "host-303.json"), """
                {"processId":303,"pipeName":"ArcGISProMCP.v1.303","processStartedAtUtc":"2026-01-01T00:00:00+00:00","publishedAtUtc":"2026-01-01T00:00:00+00:00","projectName":"Old.aprx"}
                """);
            BridgeHostDiscovery.Publish(Host(404, "Fake", BridgeHostKinds.FakeHost), root);

            var live = BridgeHostDiscovery.ListLive(root, _ => true);

            var legacy = Assert.Single(live, host => host.ProcessId == 303);
            Assert.Null(legacy.HostKind);
            Assert.Equal(BridgeHostKinds.ArcGISPro, legacy.EffectiveHostKind);
            Assert.True(legacy.IsArcGISPro);
            var fake = Assert.Single(live, host => host.ProcessId == 404);
            Assert.True(fake.IsFakeHost);
            Assert.False(fake.IsArcGISPro);
            Assert.Contains("\"hostKind\": \"fakehost\"", File.ReadAllText(Path.Combine(root, "host-404.json")), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Automatic_selection_ignores_fakehost_records()
    {
        var hosts = new[] { Host(101, "[FakeHost] Riverton", BridgeHostKinds.FakeHost), Host(202, "Two.aprx") };

        var pipe = BridgeEndpointResolver.ResolvePipeName(_ => null, hosts);

        Assert.Equal(BridgeHostDiscovery.ProcessPipeName(202), pipe);
    }

    [Fact]
    public void Automatic_selection_never_attaches_to_a_lone_fakehost()
    {
        var exception = Assert.Throws<BridgeException>(() =>
            BridgeEndpointResolver.ResolvePipeName(_ => null, [Host(101, "[FakeHost] Riverton", BridgeHostKinds.FakeHost)]));

        Assert.Equal("arcgis_host_not_found", exception.Code);
        Assert.Contains("PID 101", exception.Message, StringComparison.Ordinal);
        Assert.Contains(BridgeEndpointResolver.AllowFakeHostVariable, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_host_kinds_are_not_selected_automatically()
    {
        var pipe = BridgeEndpointResolver.ResolvePipeName(_ => null, [Host(101, "Other", "something-else")]);

        Assert.Equal(BridgeProtocol.DefaultPipeName, pipe);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData(" TRUE ")]
    public void Allow_fakehost_opts_in_to_automatic_selection(string value)
    {
        var pipe = BridgeEndpointResolver.ResolvePipeName(
            name => name == BridgeEndpointResolver.AllowFakeHostVariable ? value : null,
            [Host(101, "[FakeHost] Riverton", BridgeHostKinds.FakeHost)]);

        Assert.Equal(BridgeHostDiscovery.ProcessPipeName(101), pipe);
    }

    [Fact]
    public void Allow_fakehost_still_fails_closed_when_ambiguous_with_arcgis_pro()
    {
        var exception = Assert.Throws<BridgeException>(() => BridgeEndpointResolver.ResolvePipeName(
            name => name == BridgeEndpointResolver.AllowFakeHostVariable ? "true" : null,
            [Host(101, "[FakeHost] Riverton", BridgeHostKinds.FakeHost), Host(202, "Two.aprx")]));

        Assert.Equal("arcgis_host_ambiguous", exception.Code);
        Assert.Contains("PID 101 ([FakeHost] Riverton)", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("yes")]
    public void Allow_fakehost_requires_an_explicit_true(string value)
    {
        Assert.Throws<BridgeException>(() => BridgeEndpointResolver.ResolvePipeName(
            name => name == BridgeEndpointResolver.AllowFakeHostVariable ? value : null,
            [Host(101, "[FakeHost] Riverton", BridgeHostKinds.FakeHost)]));
    }

    [Fact]
    public async Task Gateway_client_without_a_selector_refuses_a_lone_fakehost()
    {
        var client = new DiscoveringBridgeClient(
            TimeSpan.FromMilliseconds(75),
            TimeSpan.FromSeconds(1),
            _ => null,
            () => [Host(101, "[FakeHost] Riverton", BridgeHostKinds.FakeHost)]);

        var exception = await Assert.ThrowsAsync<BridgeException>(() =>
            client.CallAsync("system.get_state", null, TestContext.Current.CancellationToken));

        Assert.Equal("arcgis_host_not_found", exception.Code);
        Assert.True(exception.Retryable);
    }

    [Fact]
    public void Explicit_host_pid_may_select_a_fakehost()
    {
        var hosts = new[] { Host(101, "[FakeHost] Riverton", BridgeHostKinds.FakeHost), Host(202, "Two.aprx") };

        var pipe = BridgeEndpointResolver.ResolvePipeName(name => name == "ARCGIS_PRO_MCP_HOST_PID" ? "101" : null, hosts);

        Assert.Equal(BridgeHostDiscovery.ProcessPipeName(101), pipe);
    }

    private static BridgeHostRecord Host(int processId, string projectName, string? hostKind = null) => new(
        processId,
        BridgeHostDiscovery.ProcessPipeName(processId),
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        projectName,
        $"C:\\Projects\\{projectName}",
        hostKind);
}
