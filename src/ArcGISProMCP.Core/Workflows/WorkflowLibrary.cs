using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.Core.Workflows;

public interface IWorkflowLibrary
{
    Task<IReadOnlyList<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken);

    Task<WorkflowDefinition?> GetAsync(string id, string? version, CancellationToken cancellationToken);

    Task<WorkflowValidationResult> ValidateAsync(WorkflowDefinition workflow, CancellationToken cancellationToken);

    Task SaveAsync(WorkflowDefinition workflow, CancellationToken cancellationToken);

    Task RecordRunAsync(WorkflowRunSummary summary, CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkflowRanking>> RankAsync(CancellationToken cancellationToken);
}

public sealed class FileWorkflowLibrary : IWorkflowLibrary, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions JsonLinesOptions = new(JsonOptions) { WriteIndented = false };
    private readonly string _root;
    private readonly string _runsPath;
    private readonly IOperationRegistry _registry;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public FileWorkflowLibrary(string root, IOperationRegistry registry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        _runsPath = Path.Combine(_root, "runs.jsonl");
        _registry = registry;
        Directory.CreateDirectory(_root);
    }

    public async Task<IReadOnlyList<WorkflowDefinition>> ListAsync(CancellationToken cancellationToken)
    {
        var workflows = new List<WorkflowDefinition>();
        foreach (var path in Directory.EnumerateFiles(_root, "*.workflow.json", SearchOption.TopDirectoryOnly))
        {
            var workflow = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            if (workflow is not null) workflows.Add(workflow);
        }

        return workflows
            .OrderBy(workflow => workflow.Id, StringComparer.Ordinal)
            .ThenByDescending(workflow => ParseVersion(workflow.Version))
            .ToArray();
    }

    public async Task<WorkflowDefinition?> GetAsync(string id, string? version, CancellationToken cancellationToken)
    {
        var workflows = await ListAsync(cancellationToken).ConfigureAwait(false);
        return workflows.FirstOrDefault(workflow =>
            string.Equals(workflow.Id, id, StringComparison.OrdinalIgnoreCase) &&
            (version is null || string.Equals(workflow.Version, version, StringComparison.OrdinalIgnoreCase)));
    }

    public Task<WorkflowValidationResult> ValidateAsync(WorkflowDefinition workflow, CancellationToken cancellationToken)
    {
        var issues = ImmutableArray.CreateBuilder<WorkflowValidationIssue>();
        if (string.IsNullOrWhiteSpace(workflow.Id)) issues.Add(new("workflow_id_required", "Workflow id is required."));
        if (!Version.TryParse(workflow.Version, out _)) issues.Add(new("invalid_version", "Workflow version must be semantic numeric form."));

        var stepIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in workflow.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!stepIds.Add(step.Id)) issues.Add(new("duplicate_step", $"Duplicate step id '{step.Id}'.", step.Id));
            if (!_registry.TryGet(step.Operation, out _)) issues.Add(new("unknown_operation", $"Unknown operation '{step.Operation}'.", step.Id));
            if (step.Arguments.ValueKind != JsonValueKind.Object) issues.Add(new("invalid_arguments", "Step arguments must be a JSON object.", step.Id));
        }

        foreach (var step in workflow.Steps)
        {
            foreach (var dependency in step.DependsOn)
            {
                if (!stepIds.Contains(dependency)) issues.Add(new("missing_dependency", $"Step '{step.Id}' depends on missing step '{dependency}'.", step.Id));
            }
        }

        if (HasCycle(workflow.Steps)) issues.Add(new("dependency_cycle", "Workflow dependency graph contains a cycle."));
        return Task.FromResult(new WorkflowValidationResult(issues.Count == 0, issues.ToImmutable()));
    }

    public async Task SaveAsync(WorkflowDefinition workflow, CancellationToken cancellationToken)
    {
        var validation = await ValidateAsync(workflow, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(string.Join(" ", validation.Issues.Select(issue => issue.Message)));
        }

        var persisted = workflow with { ContentHash = ComputeContentHash(workflow) };
        var fileName = $"{SafeSegment(workflow.Id)}@{SafeSegment(workflow.Version)}.workflow.json";
        var destination = ResolveInsideRoot(fileName);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(destination))
            {
                var existing = await ReadAsync(destination, cancellationToken).ConfigureAwait(false);
                if (existing?.ContentHash == persisted.ContentHash) return;
                throw new InvalidOperationException($"Workflow '{workflow.Id}' version '{workflow.Version}' is immutable. Save a new version.");
            }

            await WriteAtomicAsync(destination, JsonSerializer.Serialize(persisted, JsonOptions), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task RecordRunAsync(WorkflowRunSummary summary, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(summary, JsonLinesOptions) + Environment.NewLine;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(_runsPath, line, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<WorkflowRanking>> RankAsync(CancellationToken cancellationToken)
    {
        string[] lines;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_runsPath)) return [];
            lines = await File.ReadAllLinesAsync(_runsPath, cancellationToken).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
        var summaries = new List<WorkflowRunSummary>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var summary = JsonSerializer.Deserialize<WorkflowRunSummary>(line, JsonOptions);
            if (summary is not null) summaries.Add(summary);
        }

        return summaries
            .GroupBy(run => (run.WorkflowId, run.WorkflowVersion))
            .Select(group =>
            {
                var successes = group.Count(run => string.Equals(run.Outcome, "succeeded", StringComparison.OrdinalIgnoreCase));
                var failures = group.Count() - successes;
                var corrections = group.Count(run => !string.IsNullOrWhiteSpace(run.UserCorrection));
                var score = (successes + 1d) / (successes + failures + 2d) - corrections * 0.05d;
                return new WorkflowRanking(group.Key.WorkflowId, group.Key.WorkflowVersion, successes, failures, corrections, score);
            })
            .OrderByDescending(ranking => ranking.Score)
            .ThenBy(ranking => ranking.WorkflowId, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<WorkflowDefinition?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Workflow path escaped the configured library root.");
        }

        await using var stream = File.OpenRead(fullPath);
        return await JsonSerializer.DeserializeAsync<WorkflowDefinition>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Computes the content hash that <see cref="SaveAsync"/> persists: SHA-256 of the canonical
    /// JSON with <see cref="WorkflowDefinition.ContentHash"/> cleared.
    /// </summary>
    internal static string ComputeContentHash(WorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var json = JsonSerializer.Serialize(workflow with { ContentHash = null }, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static async Task WriteAtomicAsync(string destination, string content, CancellationToken cancellationToken)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination, false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private string ResolveInsideRoot(string fileName)
    {
        var path = Path.GetFullPath(Path.Combine(_root, fileName));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Workflow path escaped the configured library root.");
        }

        return path;
    }

    private static string SafeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '-' or '_')))
        {
            throw new ArgumentException($"'{value}' is not a safe workflow id or version.");
        }

        return value;
    }

    private static Version ParseVersion(string value) => Version.TryParse(value, out var version) ? version : new Version(0, 0);

    private static bool HasCycle(ImmutableArray<WorkflowStep> steps)
    {
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byId = steps.GroupBy(step => step.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        bool Visit(string id)
        {
            if (state.TryGetValue(id, out var value)) return value == 1;
            state[id] = 1;
            if (byId.TryGetValue(id, out var step))
            {
                foreach (var dependency in step.DependsOn)
                {
                    if (byId.ContainsKey(dependency) && Visit(dependency)) return true;
                }
            }

            state[id] = 2;
            return false;
        }

        return steps.Any(step => Visit(step.Id));
    }

    public void Dispose() => _writeGate.Dispose();
}
