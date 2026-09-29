using System.Collections.Immutable;
using System.Text.Json;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Registry;

namespace ArcGISProMCP.Evals;

/// <summary>
/// Reads the descriptor dump in <c>tests/ArcGISProMCP.Operations.Tests/Fixtures/operation-descriptors.json</c>:
/// every operation descriptor the add-in registers (id to full descriptor), dumped from the built add-in
/// and held identical to the portable operations by <c>DescriptorGuardTests</c>. It is the one descriptor
/// source for the evals.
/// </summary>
public static class OperationDescriptorDump
{
    public static ImmutableArray<OperationDescriptor> Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"'{path}' must be an object keyed by operation id.");

        var descriptors = ImmutableArray.CreateBuilder<OperationDescriptor>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var descriptor = ToDescriptor(property.Value);
            if (!string.Equals(descriptor.Id, property.Name, StringComparison.Ordinal))
                throw new InvalidDataException($"'{path}': key '{property.Name}' holds descriptor '{descriptor.Id}'.");
            descriptors.Add(descriptor);
        }

        return descriptors.OrderBy(descriptor => descriptor.Id, StringComparer.Ordinal).ToImmutableArray();
    }

    /// <summary>A real <see cref="OperationRegistry"/> holding one non-executable operation per descriptor.</summary>
    public static OperationRegistry CreateRegistry(IEnumerable<OperationDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var registry = new OperationRegistry();
        foreach (var descriptor in descriptors)
            registry.Register(new DumpedOperation(descriptor));
        return registry;
    }

    public static OperationDescriptor ToDescriptor(JsonElement entry) =>
        OperationDescriptor.Create(
            entry.GetProperty("id").GetString()!,
            entry.GetProperty("title").GetString()!,
            entry.GetProperty("summary").GetString()!,
            entry.GetProperty("inputSchema"),
            risk: Enum.Parse<OperationRisk>(entry.GetProperty("risk").GetString()!, ignoreCase: false),
            executionTarget: Enum.Parse<ExecutionTarget>(entry.GetProperty("executionTarget").GetString()!, ignoreCase: false),
            capabilities: Strings(entry, "requiredCapabilities"),
            tags: Strings(entry, "tags"),
            aliases: Strings(entry, "aliases"),
            examples: Strings(entry, "examples"),
            related: Strings(entry, "relatedOperations"),
            requiresConfirmation: entry.GetProperty("requiresConfirmation").GetBoolean(),
            undoable: entry.GetProperty("undoable").GetBoolean(),
            version: entry.GetProperty("version").GetString()!,
            outputSchema: entry.GetProperty("outputSchema") is { ValueKind: JsonValueKind.Object } output ? output : null,
            typicalDuration: entry.GetProperty("typicalDuration").GetString(),
            executesUserCode: entry.GetProperty("executesUserCode").GetBoolean());

    private static string[] Strings(JsonElement entry, string name) =>
        entry.GetProperty(name).EnumerateArray().Select(item => item.GetString()!).ToArray();

    private sealed class DumpedOperation(OperationDescriptor descriptor) : IOperation
    {
        public OperationDescriptor Descriptor { get; } = descriptor;

        public Task<OperationResult> ExecuteAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Evaluation descriptors are searched, never executed.");
    }
}
