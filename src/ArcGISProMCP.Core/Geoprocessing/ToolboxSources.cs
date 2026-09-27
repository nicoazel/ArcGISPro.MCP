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
/// The archive is untrusted: every metadata entry is read through a stream that stops at
/// <see cref="MaximumEntryBytes"/> whatever size the entry declares, the archive may hold at most
/// <see cref="MaximumEntries"/> entries and <see cref="MaximumTotalBytes"/> of metadata text, and
/// script (<c>.py</c>) entries are only recorded as present, never read. Exceeding a limit throws
/// <see cref="InvalidDataException"/>, so the catalog reports the toolbox as unreadable.
/// </summary>
public sealed class ZipToolboxSource : IToolboxSource
{
    /// <summary>Largest metadata entry read, in decompressed bytes.</summary>
    public const long MaximumEntryBytes = 16 * 1024 * 1024;

    /// <summary>Largest total of decompressed metadata bytes read from one archive.</summary>
    public const long MaximumTotalBytes = 64 * 1024 * 1024;

    /// <summary>Most entries (files and folders) one archive may contain.</summary>
    public const int MaximumEntries = 10_000;

    private readonly Dictionary<string, string> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _presentOnly = new(StringComparer.OrdinalIgnoreCase);

    public ZipToolboxSource(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        using var archive = ZipFile.OpenRead(Path);
        if (archive.Entries.Count > MaximumEntries)
            throw new InvalidDataException($"The archive has {archive.Entries.Count} entries; at most {MaximumEntries} are read.");
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            if (IsScriptEntry(entry.Name))
            {
                // Only the presence of a script body matters (it marks a script tool).
                _presentOnly.Add(Normalize(entry.FullName));
                continue;
            }
            if (!IsMetadataEntry(entry.Name)) continue;
            // Declared sizes are untrusted, so the bound applies to the bytes actually decompressed.
            var budget = Math.Min(MaximumEntryBytes, MaximumTotalBytes - total);
            using var bounded = new BoundedReadStream(entry.Open(), budget, entry.FullName);
            using var reader = new StreamReader(bounded, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            _entries[Normalize(entry.FullName)] = reader.ReadToEnd();
            total += bounded.BytesRead;
        }
    }

    public string Path { get; }

    public GpToolboxKind Kind => GpToolboxKind.Archive;

    public string? ReadText(string relativePath) =>
        _entries.TryGetValue(Normalize(relativePath), out var text) ? text : null;

    public bool FileExists(string relativePath)
    {
        var key = Normalize(relativePath);
        return _entries.ContainsKey(key) || _presentOnly.Contains(key);
    }

    // Only the files the reader uses.
    private static bool IsMetadataEntry(string name) =>
        name.EndsWith(".content", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".rc", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".link", StringComparison.OrdinalIgnoreCase);

    private static bool IsScriptEntry(string name) => name.EndsWith(".py", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    /// <summary>Read-only pass-through that throws once more than <c>limit</c> bytes come out of the inner stream.</summary>
    private sealed class BoundedReadStream(Stream inner, long limit, string entryName) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            // Ask for one byte past the limit so an over-long entry is detected, not silently truncated.
            var allowed = (int)Math.Min(buffer.Length, limit - BytesRead + 1);
            var read = inner.Read(buffer[..allowed]);
            BytesRead += read;
            if (BytesRead > limit)
                throw new InvalidDataException($"Archive entry '{entryName}' exceeds the read limit ({MaximumEntryBytes} bytes per entry, {MaximumTotalBytes} bytes per archive).");
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
