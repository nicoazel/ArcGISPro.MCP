using Xunit;

// Each test hosts its own in-process MCP server. When several hosts build their tool lists at the
// same time, ModelContextProtocol 2.2.0 intermittently misses that an IBridgeClient parameter is a
// DI service and publishes it as a required "bridge" argument, which breaks the tools/list
// snapshot. The stdio server only ever builds one host, so run these tests one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
