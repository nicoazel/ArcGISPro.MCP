using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;

namespace ArcGISProMCP.Core.Workflows;

/// <summary>Outcome of seeding one bundled workflow resource.</summary>
public enum WorkflowSeedOutcome
{
    /// <summary>The workflow version was not installed and has been written.</summary>
    Seeded,

    /// <summary>The same workflow id and version with identical content is already installed.</summary>
    AlreadyPresent,

    /// <summary>A different workflow with the same id and version is installed; it was left untouched.</summary>
    Conflict,

    /// <summary>The bundled workflow could not be read or failed validation.</summary>
    Failed
}

public sealed record WorkflowSeedItem(
    string ResourceName,
    string? WorkflowId,
    string? Version,
    WorkflowSeedOutcome Outcome,
    string? Message = null);

public sealed record WorkflowSeedReport(ImmutableArray<WorkflowSeedItem> Items)
{
    public int SeededCount => Items.Count(item => item.Outcome == WorkflowSeedOutcome.Seeded);

    public IEnumerable<WorkflowSeedItem> Skipped =>
        Items.Where(item => item.Outcome is WorkflowSeedOutcome.Conflict or WorkflowSeedOutcome.Failed);
}

/// <summary>
/// Installs the workflows embedded in this assembly into a workflow library. Seeding never
/// overwrites an installed workflow version: existing versions with different content are
/// reported as conflicts and left untouched.
/// </summary>
public static class WorkflowSeeder
{
    public const string ResourcePrefix = "workflows/";
    public const string ResourceSuffix = ".workflow.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static Assembly BundleAssembly => typeof(WorkflowSeeder).Assembly;

    public static IReadOnlyList<string> BundledResourceNames { get; } = BundleAssembly
        .GetManifestResourceNames()
        .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) &&
                       name.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase))
        .Order(StringComparer.Ordinal)
        .ToArray();

    public static async Task<WorkflowSeedReport> SeedAsync(IWorkflowLibrary library, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(library);
        var items = ImmutableArray.CreateBuilder<WorkflowSeedItem>(BundledResourceNames.Count);
        foreach (var resourceName in BundledResourceNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(await SeedOneAsync(library, resourceName, cancellationToken).ConfigureAwait(false));
        }

        return new WorkflowSeedReport(items.ToImmutable());
    }

    private static async Task<WorkflowSeedItem> SeedOneAsync(IWorkflowLibrary library, string resourceName, CancellationToken cancellationToken)
    {
        WorkflowDefinition? workflow;
        try
        {
            await using var stream = BundleAssembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException("Embedded workflow resource is missing.");
            workflow = await JsonSerializer.DeserializeAsync<WorkflowDefinition>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or IOException)
        {
            return new(resourceName, null, null, WorkflowSeedOutcome.Failed, exception.Message);
        }

        if (workflow is null)
            return new(resourceName, null, null, WorkflowSeedOutcome.Failed, "Embedded workflow resource is empty.");

        // The bundled file never carries an authoritative hash; the library computes it on save.
        workflow = workflow with { ContentHash = null };
        try
        {
            var existing = await library.GetAsync(workflow.Id, workflow.Version, cancellationToken).ConfigureAwait(false);
            try
            {
                // SaveAsync is a no-op for identical content and throws for a different
                // installed version, so user files are never overwritten.
                await library.SaveAsync(workflow, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                return new(resourceName, workflow.Id, workflow.Version,
                    existing is null ? WorkflowSeedOutcome.Failed : WorkflowSeedOutcome.Conflict,
                    exception.Message);
            }

            return new(resourceName, workflow.Id, workflow.Version,
                existing is null ? WorkflowSeedOutcome.Seeded : WorkflowSeedOutcome.AlreadyPresent);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(resourceName, workflow.Id, workflow.Version, WorkflowSeedOutcome.Failed, exception.Message);
        }
    }
}
