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

        // One snapshot of the installed library; each bundled workflow is then matched in memory.
        IReadOnlyList<WorkflowDefinition> installed;
        try
        {
            installed = await library.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            foreach (var resourceName in BundledResourceNames)
                items.Add(new(resourceName, null, null, WorkflowSeedOutcome.Failed, exception.Message));
            return new WorkflowSeedReport(items.ToImmutable());
        }

        foreach (var resourceName in BundledResourceNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(await SeedOneAsync(library, installed, resourceName, cancellationToken).ConfigureAwait(false));
        }

        return new WorkflowSeedReport(items.ToImmutable());
    }

    private static async Task<WorkflowSeedItem> SeedOneAsync(
        IWorkflowLibrary library,
        IReadOnlyList<WorkflowDefinition> installed,
        string resourceName,
        CancellationToken cancellationToken)
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
            // Decide from the snapshot before saving: an installed workflow with the same id and
            // version may live under a different file name, which SaveAsync would not detect.
            var matches = installed
                .Where(candidate =>
                    string.Equals(candidate.Id, workflow.Id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.Version, workflow.Version, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length > 0)
            {
                var bundledHash = FileWorkflowLibrary.ComputeContentHash(workflow);
                return matches.Any(candidate => string.Equals(
                        FileWorkflowLibrary.ComputeContentHash(candidate), bundledHash, StringComparison.Ordinal))
                    ? new(resourceName, workflow.Id, workflow.Version, WorkflowSeedOutcome.AlreadyPresent)
                    : new(resourceName, workflow.Id, workflow.Version, WorkflowSeedOutcome.Conflict,
                        $"Workflow '{workflow.Id}' version '{workflow.Version}' is already installed with different content; it was left untouched.");
            }

            try
            {
                // SaveAsync throws rather than overwrite, so user files are never replaced.
                await library.SaveAsync(workflow, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                return new(resourceName, workflow.Id, workflow.Version, WorkflowSeedOutcome.Failed, exception.Message);
            }

            return new(resourceName, workflow.Id, workflow.Version, WorkflowSeedOutcome.Seeded);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(resourceName, workflow.Id, workflow.Version, WorkflowSeedOutcome.Failed, exception.Message);
        }
    }
}