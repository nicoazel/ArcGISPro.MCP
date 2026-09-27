using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Registry;

namespace ArcGISProMCP.Evals;

/// <summary>
/// The search-relevant fields of one real operation descriptor, as captured in
/// <c>evals/fixtures/operation-descriptors.json</c>.
/// </summary>
/// <remarks>
/// Interim: the add-in's descriptors cannot be constructed in tests until the Phase 4 operations seam
/// (WU-4A) moves them out of the Esri-dependent assembly. Until then the fixture is extracted from the
/// add-in source and a drift test keeps it in sync. After the seam, build the registry from
/// <c>ProOperationCatalog</c> directly and delete the fixture.
/// </remarks>
public sealed record DescriptorFixtureEntry(
    string Id,
    string Title,
    string Summary,
    ImmutableArray<string> Tags,
    ImmutableArray<string> Aliases,
    ImmutableArray<string> Capabilities,
    string Risk,
    string Domain,
    bool RequiresConfirmation,
    bool ExecutesUserCode,
    string Source);

public sealed record DescriptorFixture(string Note, ImmutableArray<DescriptorFixtureEntry> Operations);

public static class DescriptorFixtures
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static DescriptorFixture Load(string path) =>
        JsonSerializer.Deserialize<DescriptorFixture>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"'{path}' is empty.");

    /// <summary>A real <see cref="OperationRegistry"/> holding one non-executable operation per fixture entry.</summary>
    public static OperationRegistry CreateRegistry(DescriptorFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var registry = new OperationRegistry();
        foreach (var entry in fixture.Operations)
            registry.Register(new FixtureOperation(ToDescriptor(entry)));
        return registry;
    }

    public static OperationDescriptor ToDescriptor(DescriptorFixtureEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return OperationDescriptor.Create(
            entry.Id,
            entry.Title,
            entry.Summary,
            JsonSchemas.EmptyObject,
            risk: Enum.Parse<OperationRisk>(entry.Risk, ignoreCase: false),
            capabilities: entry.Capabilities,
            tags: entry.Tags,
            aliases: entry.Aliases,
            requiresConfirmation: entry.RequiresConfirmation,
            executesUserCode: entry.ExecutesUserCode);
    }

    private sealed class FixtureOperation(OperationDescriptor descriptor) : IOperation
    {
        public OperationDescriptor Descriptor { get; } = descriptor;

        public Task<OperationResult> ExecuteAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Evaluation fixtures are searched, never executed.");
    }
}
