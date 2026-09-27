using System.Text.Json;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Registry;

namespace ArcGISProMCP.Core.Tests;

public sealed class RegistrySearchTests
{
    private static readonly string[] RemovedWords = ["to", "the", "in", "on"];

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

    [Fact]
    public void Articles_and_prepositions_do_not_count_as_search_terms()
    {
        var registry = CreateRegistry();

        var noisy = registry.Search(new OperationQuery("set the transparency of a layer"));
        var plain = registry.Search(new OperationQuery("set transparency layer"));

        Assert.Equal(plain.Select(hit => (hit.Descriptor.Id, hit.Score)), noisy.Select(hit => (hit.Descriptor.Id, hit.Score)));
        Assert.Equal("layer.set-transparency", noisy[0].Descriptor.Id);
        Assert.All(noisy, hit => Assert.DoesNotContain(hit.MatchedTerms, term => term is "the" or "of" or "a"));
    }

    [Fact]
    public void Stop_words_do_not_match_unrelated_operations()
    {
        var registry = CreateRegistry();

        // Without stop-word removal "a" and "to" substring-match most of the catalog.
        var hits = registry.Search(new OperationQuery("export a layout to disk"));

        var top = Assert.Single(hits);
        Assert.Equal("layout.export", top.Descriptor.Id);
        Assert.Equal(["disk", "export", "layout"], top.MatchedTerms.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("zoom to the layer")]
    [InlineData("layer in map")]
    [InlineData("layer on the map")]
    public void Domain_words_survive_stop_word_removal(string query)
    {
        var registry = CreateRegistry();

        var hits = registry.Search(new OperationQuery(query));

        Assert.NotEmpty(hits);
        Assert.All(hits, hit => Assert.All(hit.MatchedTerms, term =>
            Assert.DoesNotContain(term, RemovedWords)));
        Assert.Contains(hits, hit => hit.MatchedTerms.Contains("layer"));
    }

    [Fact]
    public void Query_of_only_stop_words_is_not_treated_as_empty()
    {
        var registry = CreateRegistry();

        var hits = registry.Search(new OperationQuery("the"));

        // An empty query would list every operation; "the" still has to match text.
        Assert.DoesNotContain(hits, hit => hit.Descriptor.Id == "layout.export");
        Assert.NotEmpty(hits);
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
