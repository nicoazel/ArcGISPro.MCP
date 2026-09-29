using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArcGISProMCP.Evals;

/// <summary>
/// One retrieval task: a natural-language request and the result ids that count as a correct answer.
/// Any one of <see cref="Expected"/> appearing in the ranked list is a hit (the ids are alternatives,
/// not a set that must all be retrieved).
/// </summary>
public sealed record EvalTask(
    string Id,
    string Task,
    ImmutableArray<string> Expected,
    int K = 5,
    string? Group = null,
    EvalFilters? Filters = null,
    string? Notes = null);

/// <summary>Optional registry search filters, mirroring the <c>registry_search</c> tool arguments.</summary>
public sealed record EvalFilters(
    string? Domain = null,
    ImmutableArray<string>? Capabilities = null,
    string? MaxRisk = null);

/// <summary>Reads task files: one JSON object per line; blank lines and lines starting with <c>//</c> are skipped.</summary>
public static class EvalTaskLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static ImmutableArray<EvalTask> LoadJsonl(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var tasks = ImmutableArray.CreateBuilder<EvalTask>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal)) continue;

            EvalTask? task;
            try
            {
                task = JsonSerializer.Deserialize<EvalTask>(trimmed, Options);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"{path}:{lineNumber}: {exception.Message}", exception);
            }

            if (task is null || string.IsNullOrWhiteSpace(task.Id) || string.IsNullOrWhiteSpace(task.Task))
                throw new InvalidDataException($"{path}:{lineNumber}: a task needs an id and a task text.");
            if (task.Expected.IsDefaultOrEmpty || task.Expected.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException($"{path}:{lineNumber}: task '{task.Id}' needs at least one expected id.");
            if (task.K < 1)
                throw new InvalidDataException($"{path}:{lineNumber}: task '{task.Id}' has k < 1.");
            if (!ids.Add(task.Id))
                throw new InvalidDataException($"{path}:{lineNumber}: duplicate task id '{task.Id}'.");
            tasks.Add(task);
        }

        return tasks.ToImmutable();
    }
}
