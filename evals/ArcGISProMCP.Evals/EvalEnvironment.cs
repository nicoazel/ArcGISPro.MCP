using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace ArcGISProMCP.Evals;

/// <summary>The commit a scorecard describes. <see cref="Dirty"/> ignores <c>evals/results</c> itself.</summary>
public sealed record EvalCommit(string Sha, bool Dirty, string Date)
{
    /// <summary><c>evals/results/&lt;yyyy-MM-dd&gt;-&lt;sha7&gt;</c> under <paramref name="repositoryRoot"/>.</summary>
    public string ResultsDirectory(string repositoryRoot) =>
        Path.Combine(repositoryRoot, "evals", "results", $"{Date}-{Sha}");
}

public static class EvalEnvironment
{
    /// <summary>Set to <c>1</c> to write the scorecard of a test run.</summary>
    public const string WriteResultsVariable = "EVALS_WRITE_RESULTS";

    public const string SolutionFileName = "ArcGISPro.MCP.slnx";

    public static bool WriteResults =>
        string.Equals(Environment.GetEnvironmentVariable(WriteResultsVariable), "1", StringComparison.Ordinal);

    /// <summary>Walks up from <paramref name="start"/> (default: the test output directory) to the directory holding the solution.</summary>
    public static string FindRepositoryRoot(string? start = null)
    {
        var directory = new DirectoryInfo(start ?? AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"No {SolutionFileName} above '{start ?? AppContext.BaseDirectory}'.");
    }

    /// <summary>Reads HEAD from git; falls back to <c>unknown</c> when git is unavailable.</summary>
    public static EvalCommit ResolveCommit(string repositoryRoot, DateTimeOffset? now = null)
    {
        var date = (now ?? DateTimeOffset.Now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var sha = Git(repositoryRoot, "rev-parse --short=7 HEAD")?.Trim();
        var status = Git(repositoryRoot, "status --porcelain -- . \":(exclude)evals/results\"");
        return new EvalCommit(string.IsNullOrEmpty(sha) ? "unknown" : sha, !string.IsNullOrWhiteSpace(status), date);
    }

    private static string? Git(string workingDirectory, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

/// <summary>Committed reference metrics per suite (<c>evals/baseline.json</c>).</summary>
public sealed record EvalBaseline(string Note, IReadOnlyDictionary<string, BaselineMetrics> Suites)
{
    /// <summary>How far recall@5 may fall below the baseline before the eval fails.</summary>
    public const double RecallAt5Tolerance = 0.05;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public static EvalBaseline Load(string path) =>
        JsonSerializer.Deserialize<EvalBaseline>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"'{path}' is empty.");

    /// <summary>Null when the suite is within tolerance; otherwise the reason it regressed.</summary>
    public string? CheckRegression(EvalSuiteResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!Suites.TryGetValue(result.Suite, out var baseline))
            return $"Suite '{result.Suite}' has no baseline in evals/baseline.json.";
        if (baseline.Tasks != result.Metrics.Tasks)
            return $"{result.Suite}: {result.Metrics.Tasks} tasks but the baseline was measured on {baseline.Tasks}; re-measure evals/baseline.json.";
        var floor = baseline.RecallAt5 - RecallAt5Tolerance;
        return result.Metrics.RecallAt5 + 1e-9 < floor
            ? string.Create(CultureInfo.InvariantCulture,
                $"{result.Suite}: recall@5 {result.Metrics.RecallAt5:0.000} fell below baseline {baseline.RecallAt5:0.000} - {RecallAt5Tolerance:0.00}.")
            : null;
    }
}

public sealed record BaselineMetrics(
    [property: System.Text.Json.Serialization.JsonPropertyName("recall@1")] double RecallAt1,
    [property: System.Text.Json.Serialization.JsonPropertyName("recall@5")] double RecallAt5,
    [property: System.Text.Json.Serialization.JsonPropertyName("mrr")] double Mrr,
    [property: System.Text.Json.Serialization.JsonPropertyName("tasks")] int Tasks);
