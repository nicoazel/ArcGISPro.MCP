using ArcGISProMCP.Evals;

namespace ArcGISProMCP.Evals.Tests;

/// <summary>
/// The evals search the descriptor dump that <c>DescriptorGuardTests</c> (Operations.Tests) holds identical
/// to every portable operation. The operations that stay in the add-in cannot be constructed in tests, so
/// their descriptor sources are read here and compared with the dump.
/// </summary>
public sealed class DescriptorDumpTests
{
    [Fact]
    public void Dump_matches_every_descriptor_source()
    {
        var root = EvalPaths.RepositoryRoot;
        var sources = new[]
            {
                Path.Combine(root, "src", "ArcGISProMCP.Operations"),
                Path.Combine(root, "src", "ArcGISProMCP.AddIn", "Operations")
            }
            .SelectMany(directory => DescriptorSourceExtractor.Extract(directory, root))
            .ToDictionary(source => source.Id, StringComparer.Ordinal);
        var dump = OperationDescriptorDump.Load(EvalPaths.DescriptorDump).ToDictionary(descriptor => descriptor.Id, StringComparer.Ordinal);

        Assert.Equal(sources.Keys.Order(StringComparer.Ordinal), dump.Keys.Order(StringComparer.Ordinal));
        Assert.All(sources.Values, source =>
        {
            var descriptor = dump[source.Id];
            var context = $"{source.Id} ({source.Source}) no longer matches {EvalPaths.DescriptorDumpRelative}.";
            Assert.True(source.Title == descriptor.Title, context + " title");
            Assert.True(source.Summary == descriptor.Summary, context + " summary");
            Assert.True(source.Aliases.SequenceEqual(descriptor.Aliases), context + " aliases");
            Assert.True(descriptor.Tags.SetEquals(source.Tags), context + " tags");
            Assert.True(descriptor.RequiredCapabilities.SetEquals(source.Capabilities), context + " capabilities");
            Assert.True(source.Risk == descriptor.Risk.ToString(), context + " risk");
            Assert.True(source.RequiresConfirmation == descriptor.RequiresConfirmation, context + " requiresConfirmation");
            Assert.True(source.ExecutesUserCode == descriptor.ExecutesUserCode, context + " executesUserCode");
        });
    }

    [Fact]
    public void Dump_builds_a_real_registry()
    {
        var descriptors = OperationDescriptorDump.Load(EvalPaths.DescriptorDump);

        var registry = OperationDescriptorDump.CreateRegistry(descriptors);

        Assert.Equal(41, registry.Descriptors.Count);
    }
}
