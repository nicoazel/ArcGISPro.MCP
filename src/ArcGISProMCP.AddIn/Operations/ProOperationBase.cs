using System.Text.Json;
using ArcGISProMCP.AddIn.ArcGIS;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.AddIn.Operations;

internal abstract class ProOperationBase(OperationDescriptor descriptor) : IOperation
{
    public OperationDescriptor Descriptor { get; } = descriptor;

    public async Task<OperationResult> ExecuteAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Short SDK writes have no rollback contract. Once accepted, drain them rather than
        // abandoning a queued mutation when the transport caller cancels.
        var executionToken = Descriptor.Risk == OperationRisk.SafeWrite ? CancellationToken.None : cancellationToken;
        var result = await ExecuteCoreAsync(arguments, context, executionToken).ConfigureAwait(false);
        if (Descriptor.Risk != OperationRisk.ReadOnly && context.Workspace is ProWorkspaceStateProvider workspace)
        {
            workspace.AdvanceRevision();
            // A successful write must publish its new revision even if the request was cancelled
            // while the underlying host operation was running.
            // ArcGIS publishes map-member and layout-element events after many SDK
            // mutators return. Wait for those notifications to drain so the next
            // serialized workflow step receives the revision we actually publish.
            var snapshot = await workspace.GetSettledSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            result = result with { WorkspaceRevision = snapshot.Revision };
        }
        return result;
    }

    protected abstract Task<OperationResult> ExecuteCoreAsync(
        JsonElement arguments,
        OperationContext context,
        CancellationToken cancellationToken);

    protected static string RequiredString(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ArgumentException($"Argument '{name}' is required.");
        }

        return value.GetString()!;
    }

    protected static string? OptionalString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    protected static bool OptionalBoolean(JsonElement arguments, string name, bool defaultValue) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : defaultValue;

    protected static double OptionalDouble(JsonElement arguments, string name, double defaultValue) =>
        arguments.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : defaultValue;

    protected static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
}
