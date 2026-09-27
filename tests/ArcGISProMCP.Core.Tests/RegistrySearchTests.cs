using System.Text.Json;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Registry;

namespace ArcGISProMCP.Core.Tests;

public sealed class RegistrySearchTests
{
    [Fact]
    public void Maximum_risk_read_only_excludes_write_operations()
    {
        var registry = CreateRegistry();

        var hits = registry.Search(new OperationQuery("layer", MaximumRisk: OperationRisk.ReadOnly));

        Assert.Equal(["layer.list"], hits.Select(hit => hit.Descriptor.Id));
    }

    [Fact]
    public void Maximum_risk_includes_lower_tiers()
    {
        var registry = CreateRegistry();

        var hits = registry.Search(new OperationQuery("layer", MaximumRisk: OperationRisk.SafeWrite));

        Assert.Equal(["layer.list", "layer.set-transparency"], hits.Select(hit => hit.Descriptor.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Capabilities_filter_requires_every_listed_capability()
    {
        var registry = CreateRegistry();

        var layouts = registry.Search(new OperationQuery(Capabilities: ["LAYOUTS"]));
        var both = registry.Search(new OperationQuery(Capabilities: ["maps", "layouts"]));

        Assert.Equal(["layout.export"], layouts.Select(hit => hit.Descriptor.Id));
        Assert.Empty(both);
    }

    private static OperationRegistry CreateRegistry()
    {
        var registry = new OperationRegistry();
        registry.Register(new StubOperation(OperationDescriptor.Create(
            "layer.list", "List layers", "Lists the layers of the active map.", JsonSchemas.EmptyObject,
            capabilities: ["maps"], tags: ["layer"])));
        registry.Register(new StubOperation(OperationDescriptor.Create(
            "layer.set-transparency", "Set layer transparency", "Sets the transparency of a layer in a map.", JsonSchemas.EmptyObject,
            risk: OperationRisk.SafeWrite, capabilities: ["maps"], tags: ["layer", "symbology"], aliases: ["make layer transparent"])));
        registry.Register(new StubOperation(OperationDescriptor.Create(
            "layer.remove", "Remove layer", "Removes a layer from the map.", JsonSchemas.EmptyObject,
            risk: OperationRisk.Destructive, requiresConfirmation: true, capabilities: ["maps"], tags: ["layer"])));
        registry.Register(new StubOperation(OperationDescriptor.Create(
            "layout.export", "Export layout", "Exports a layout to a PDF on disk.", JsonSchemas.EmptyObject,
            risk: OperationRisk.ExternalSideEffect, requiresConfirmation: true, capabilities: ["layouts"], tags: ["layout"])));
        return registry;
    }

    private sealed class StubOperation(OperationDescriptor descriptor) : IOperation
    {
        public OperationDescriptor Descriptor { get; } = descriptor;

        public Task<OperationResult> ExecuteAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Search tests never execute operations.");
    }
}
