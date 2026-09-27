using System.Globalization;

namespace ArcGISProMCP.Core.Geoprocessing;

/// <summary>
/// Searchable index of geoprocessing tool metadata read from disk: the system toolboxes shipped with
/// ArcGIS Pro plus optional user toolboxes. The index is built lazily on first use and rebuilt only
/// when the root's or a user toolbox's last-write time changes. It never executes toolbox code.
/// </summary>
public sealed class ToolboxCatalog
{
    /// <summary>Overrides the system toolbox directory.</summary>
    public const string RootEnvironmentVariable = "ARCGIS_PRO_MCP_TOOLBOX_ROOT";

    /// <summary>Listing text for toolboxes whose tools cannot be read statically.</summary>
    public const string UnindexedUserCodeReason = "unindexed; executes user code";

    private static readonly Lazy<ToolboxCatalog> DefaultInstance = new(() => new ToolboxCatalog());
    private readonly Lock _gate = new();
    private (string Fingerprint, ToolboxIndex Index)? _cache;

    /// <param name="systemRoot">System toolbox directory; null resolves <see cref="ResolveDefaultRoot"/>.</param>
    /// <param name="userToolboxPaths">User <c>.atbx</c>, <c>.tbx</c> (directory or legacy file) and <c>.pyt</c> paths.</param>
    public ToolboxCatalog(string? systemRoot = null, IEnumerable<string>? userToolboxPaths = null)
    {
        var root = systemRoot ?? ResolveDefaultRoot();
        SystemRoot = string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(root);
        UserToolboxPaths = (userToolboxPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Shared catalog over the default system root (no user toolboxes).</summary>
    public static ToolboxCatalog Default => DefaultInstance.Value;

    public string? SystemRoot { get; }

    public IReadOnlyList<string> UserToolboxPaths { get; }

    /// <summary>Every toolbox found, indexed or not (Python toolboxes and legacy .tbx files are listed unindexed).</summary>
    public IReadOnlyList<GpToolboxInfo> Toolboxes => GetIndex().Toolboxes;

    /// <summary>Non-fatal problems met while indexing (missing root, unreadable tools, alias collisions).</summary>
    public IReadOnlyList<string> Warnings => GetIndex().Warnings;

    public int ToolCount => GetIndex().Tools.Count;

    /// <summary>Every indexed tool, in toolbox order.</summary>
    public IReadOnlyList<GpToolSummary> Tools => GetIndex().Summaries;

    /// <summary>
    /// The system toolbox directory: <see cref="RootEnvironmentVariable"/> when set, otherwise
    /// <c>&lt;dir of the process&gt;\..\Resources\ArcToolBox\toolboxes</c> (the add-in runs in ArcGISPro.exe).
    /// </summary>
    public static string? ResolveDefaultRoot()
    {
        var overridden = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden.Trim();
        var processDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        return processDirectory is null
            ? null
            : Path.GetFullPath(Path.Combine(processDirectory, "..", "Resources", "ArcToolBox", "toolboxes"));
    }

    /// <summary>Ranks tools by name, display name, keywords, toolbox and description.</summary>
    public IReadOnlyList<GpSearchHit> Search(string? query, int limit = 20)
    {
        var index = GetIndex();
        var take = Math.Clamp(limit, 1, 200);
        var terms = Tokenize(query);
        if (terms.Length == 0)
        {
            return index.Tools
                .Where(entry => !entry.Description.Tool.Deprecated)
                .OrderBy(entry => entry.Description.Tool.ExecutionName, StringComparer.OrdinalIgnoreCase)
                .Take(take)
                .Select(entry => new GpSearchHit(entry.Description.Tool, 0, []))
                .ToArray();
        }

        var normalizedQuery = query!.Trim();
        return index.Tools
            .Select(entry => Score(entry, normalizedQuery, terms))
            .Where(hit => hit is not null)
            .Select(hit => hit!)
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Tool.Name.Length)
            .ThenBy(hit => hit.Tool.ExecutionName, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .ToArray();
    }

    /// <summary>
    /// Describes a tool by execution name (<c>alias.Name</c>, case-insensitive; the arcpy
    /// <c>Name_alias</c> form is also accepted). Returns null when the tool is not indexed.
    /// </summary>
    public GpToolDescription? Describe(string executionName)
    {
        if (string.IsNullOrWhiteSpace(executionName)) return null;
        var index = GetIndex();
        var key = executionName.Trim();
        if (index.ByExecutionName.TryGetValue(key, out var entry)) return entry.Description;
        var underscore = key.LastIndexOf('_');
        if (underscore > 0 && underscore < key.Length - 1 &&
            index.ByExecutionName.TryGetValue($"{key[(underscore + 1)..]}.{key[..underscore]}", out entry))
            return entry.Description;
        return null;
    }

    /// <summary>Discards the cached index; the next call rebuilds it.</summary>
    public void Invalidate()
    {
        lock (_gate) _cache = null;
    }

    private ToolboxIndex GetIndex()
    {
        var fingerprint = Fingerprint();
        var cache = _cache;
        if (cache is { } current && current.Fingerprint == fingerprint) return current.Index;
        lock (_gate)
        {
            if (_cache is { } built && built.Fingerprint == fingerprint) return built.Index;
            var index = ToolboxIndex.Build(SystemRoot, UserToolboxPaths);
            _cache = (fingerprint, index);
            return index;
        }
    }

    private string Fingerprint()
    {
        static string Stamp(string? path) =>
            path is null ? "-" :
            Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture) :
            File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture) :
            "missing";

        return string.Join('|', new[] { $"{SystemRoot}={Stamp(SystemRoot)}" }
            .Concat(UserToolboxPaths.Select(path => $"{path}={Stamp(path)}")));
    }

    private static GpSearchHit? Score(IndexEntry entry, string query, string[] terms)
    {
        var tool = entry.Description.Tool;
        double score = 0;
        var matched = new List<string>();
        if (string.Equals(tool.ExecutionName, query, StringComparison.OrdinalIgnoreCase)) score += 100;
        if (string.Equals(tool.Name, query.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase)) score += 30;
        if (string.Equals(tool.DisplayName, query, StringComparison.OrdinalIgnoreCase)) score += 25;

        foreach (var term in terms)
        {
            double termScore = 0;
            if (string.Equals(tool.Name, term, StringComparison.OrdinalIgnoreCase)) termScore = Math.Max(termScore, 20);
            else if (tool.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase)) termScore = Math.Max(termScore, 14);
            else if (tool.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) termScore = Math.Max(termScore, 12);
            if (entry.DisplayWords.Contains(term)) termScore = Math.Max(termScore, 10);
            else if (tool.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)) termScore = Math.Max(termScore, 8);
            if (entry.Keywords.Contains(term)) termScore = Math.Max(termScore, 7);
            else if (entry.Description.Keywords.Any(keyword => keyword.Contains(term, StringComparison.OrdinalIgnoreCase))) termScore = Math.Max(termScore, 5);
            if (string.Equals(tool.ToolboxAlias, term, StringComparison.OrdinalIgnoreCase) ||
                tool.Toolbox.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (tool.Toolset?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
                termScore = Math.Max(termScore, 4);
            if (tool.Summary?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) termScore = Math.Max(termScore, 3);
            if (termScore <= 0) continue;
            matched.Add(term);
            score += termScore;
        }

        if (matched.Count == 0 && score <= 0) return null;
        if (terms.Length > 1 && matched.Count == terms.Length) score += 5;
        if (tool.Deprecated) score *= 0.5;
        return new GpSearchHit(tool, score, matched);
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "about", "at", "by", "for", "from", "in", "into", "of", "on", "onto", "to", "with", "tool"
    };

    private static string[] Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var tokens = text.Split([' ', '\t', '\r', '\n', '.', '_', '-', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var meaningful = tokens.Where(token => !StopWords.Contains(token)).ToArray();
        return meaningful.Length == 0 ? tokens : meaningful;
    }

    private sealed record IndexEntry(GpToolDescription Description, HashSet<string> DisplayWords, HashSet<string> Keywords);

    private sealed record ToolboxIndex(
        IReadOnlyList<GpToolboxInfo> Toolboxes,
        IReadOnlyList<IndexEntry> Tools,
        IReadOnlyDictionary<string, IndexEntry> ByExecutionName,
        IReadOnlyList<string> Warnings)
    {
        public IReadOnlyList<GpToolSummary> Summaries { get; } = Tools.Select(entry => entry.Description.Tool).ToArray();

        public static ToolboxIndex Build(string? systemRoot, IReadOnlyList<string> userPaths)
        {
            var warnings = new List<string>();
            var candidates = new List<(string Path, bool IsSystem, GpToolboxKind Kind)>();
            var deprecated = GpDeprecationList.Empty;

            if (systemRoot is null || !Directory.Exists(systemRoot))
            {
                warnings.Add($"System toolbox directory not found: '{systemRoot ?? "(unresolved)"}'. Set {RootEnvironmentVariable} to override.");
            }
            else
            {
                deprecated = GpDeprecationList.Load(Path.Combine(systemRoot, "deprecated.list"));
                foreach (var path in Directory.EnumerateFileSystemEntries(systemRoot).Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (Classify(path) is { } kind) candidates.Add((path, true, kind));
                }
            }

            foreach (var path in userPaths)
            {
                if (Classify(path) is { } kind) candidates.Add((path, false, kind));
                else warnings.Add($"User toolbox '{path}' does not exist or is not a .atbx, .tbx or .pyt toolbox.");
            }

            var results = new ToolboxReadResult?[candidates.Count];
            var failures = new string?[candidates.Count];
            Parallel.For(0, candidates.Count, i =>
            {
                var (path, isSystem, kind) = candidates[i];
                if (kind is GpToolboxKind.PythonToolbox or GpToolboxKind.LegacyBinary) return;
                try
                {
                    IToolboxSource source = kind == GpToolboxKind.Archive ? new ZipToolboxSource(path) : new DirectoryToolboxSource(path, isSystem ? systemRoot : null);
                    results[i] = ToolboxMetadataReader.Read(source, isSystem, isSystem ? deprecated : null);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
                {
                    failures[i] = $"Toolbox '{path}' could not be read: {exception.Message}";
                }
            });

            var toolboxes = new List<GpToolboxInfo>();
            var tools = new List<IndexEntry>();
            var byName = new Dictionary<string, IndexEntry>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < candidates.Count; i++)
            {
                var (path, isSystem, kind) = candidates[i];
                if (failures[i] is { } failure)
                {
                    warnings.Add(failure);
                    continue;
                }

                if (results[i] is not { } result)
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    var reason = kind == GpToolboxKind.PythonToolbox
                        ? UnindexedUserCodeReason
                        : "unindexed; legacy binary toolbox (may execute user code)";
                    toolboxes.Add(new GpToolboxInfo(path, name, null, name, null, kind, isSystem, false, 0, reason, true));
                    continue;
                }

                warnings.AddRange(result.Warnings);
                toolboxes.Add(result.Toolbox);
                foreach (var tool in result.Tools)
                {
                    var entry = new IndexEntry(
                        tool,
                        new HashSet<string>(Tokenize(tool.Tool.DisplayName), StringComparer.OrdinalIgnoreCase),
                        new HashSet<string>(tool.Keywords, StringComparer.OrdinalIgnoreCase));
                    if (!byName.TryAdd(tool.Tool.ExecutionName, entry))
                    {
                        warnings.Add($"Duplicate tool '{tool.Tool.ExecutionName}' in '{path}' ignored; the first toolbox with that alias wins.");
                        continue;
                    }

                    tools.Add(entry);
                }
            }

            return new ToolboxIndex(toolboxes, tools, byName, warnings);
        }

        private static GpToolboxKind? Classify(string path)
        {
            var extension = Path.GetExtension(path);
            if (Directory.Exists(path))
                return extension.Equals(".tbx", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(path, "toolbox.content"))
                    ? GpToolboxKind.Directory
                    : null;
            if (!File.Exists(path)) return null;
            if (extension.Equals(".atbx", StringComparison.OrdinalIgnoreCase)) return GpToolboxKind.Archive;
            if (extension.Equals(".pyt", StringComparison.OrdinalIgnoreCase)) return GpToolboxKind.PythonToolbox;
            if (extension.Equals(".tbx", StringComparison.OrdinalIgnoreCase)) return GpToolboxKind.LegacyBinary;
            return null;
        }
    }
}
