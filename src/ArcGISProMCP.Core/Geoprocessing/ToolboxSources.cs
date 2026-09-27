using System.IO.Compression;
using System.Text;

namespace ArcGISProMCP.Core.Geoprocessing;

/// <summary>
/// Read-only view over a Pro-format toolbox (a <c>.tbx</c> directory or an <c>.atbx</c> zip). Paths are
/// relative to the toolbox root and use '/' separators, e.g. <c>Buffer.tool/tool.content</c>.
/// </summary>
public interface IToolboxSource
{
    /// <summary>The toolbox path on disk (directory or archive file).</summary>
    string Path { get; }

    /// <summary>The storage kind (<see cref="GpToolboxKind.Directory"/> or <see cref="GpToolboxKind.Archive"/>).</summary>
    GpToolboxKind Kind { get; }

    /// <summary>Returns the text of a file, or null when it does not exist.</summary>
    string? ReadText(string relativePath);

    /// <summary>True when the file exists.</summary>
    bool FileExists(string relativePath);
}

/// <summary>A toolbox stored as a directory (all system toolboxes in Pro 3.x).</summary>
public sealed class DirectoryToolboxSource : IToolboxSource
{
    /// <param name="path">The toolbox directory.</param>
    /// <param name="containmentRoot">
    /// Directory that relative tool folders may not escape. Defaults to <paramref name="path"/>; the
    /// catalog passes the system toolbox root because system toolboxes list tools of sibling toolboxes
    /// (e.g. <c>AggregatePolygons:..\Cartography Tools.tbx\AggregatePolygons.tool</c>).
    /// </param>
    public DirectoryToolboxSource(string path, string? containmentRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        var root = System.IO.Path.GetFullPath(containmentRoot ?? Path);
        _containmentRoot = root.EndsWith(System.IO.Path.DirectorySeparatorChar) ? root : root + System.IO.Path.DirectorySeparatorChar;
    }

    private readonly string _containmentRoot;

    public string Path { get; }

    public GpToolboxKind Kind => GpToolboxKind.Directory;

    public string? ReadText(string relativePath)
    {
        var full = Resolve(relativePath);
        return full is not null && File.Exists(full) ? File.ReadAllText(full, Encoding.UTF8) : null;
    }

    public bool FileExists(string relativePath) => Resolve(relativePath) is { } full && File.Exists(full);

    private string? Resolve(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').Replace('/', System.IO.Path.DirectorySeparatorChar);
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, normalized));
        // Toolbox content names folders; never follow a crafted name outside the containment root.
        return full.StartsWith(_containmentRoot, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}

/// <summary>
/// A toolbox stored as an <c>.atbx</c> zip. Text entries are loaded once at construction (archives are
/// small; this avoids holding a file handle on the user's toolbox). Entry names are case-insensitive.
/// </summary>
public sealed class ZipToolboxSource : IToolboxSource
{
    private const long MaximumEntryBytes = 16 * 1024 * 1024;
    private readonly Dictionary<string, string> _entries = new(StringComparer.OrdinalIgnoreCase);

    public ZipToolboxSource(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        using var archive = ZipFile.OpenRead(Path);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.Length > MaximumEntryBytes || !IsMetadataEntry(entry.Name))
                continue;
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            _entries[Normalize(entry.FullName)] = reader.ReadToEnd();
        }
    }

    public string Path { get; }

    public GpToolboxKind Kind => GpToolboxKind.Archive;

    public string? ReadText(string relativePath) =>
        _entries.TryGetValue(Normalize(relativePath), out var text) ? text : null;

    public bool FileExists(string relativePath) => _entries.ContainsKey(Normalize(relativePath));

    // Only the files the reader uses (and script bodies, whose presence marks a script tool).
    private static bool IsMetadataEntry(string name) =>
        name.EndsWith(".content", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".rc", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".py", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".link", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}
