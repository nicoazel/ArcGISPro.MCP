using System.Collections.Concurrent;
using System.Text.Json;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.Core.Resources;

/// <summary>
/// Portable file-backed resource store used by the bridge and host facade.
/// Only files written by <see cref="StoreAsync"/> are owned and eligible for retention.
/// Files not created through <see cref="StoreAsync"/> are outside retention scope.
/// Retention keeps the indexed set bounded; physical deletion is best effort when the OS denies access.
/// </summary>
public class FileResourceStore : IDisposable
{
    public const int MaxResourceBytes = 5 * 1024 * 1024;
    public const string SchemePrefix = "arcgis://resource/";
    // Same camelCase wire casing as every other bridge result.
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    private readonly string _root;
    private readonly ResourceRetentionOptions _retention;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<string, StoredResource> _resources = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public FileResourceStore(string root, ResourceRetentionOptions? retention = null)
        : this(root, retention, (Func<DateTimeOffset>?)null)
    {
    }

    public FileResourceStore(string root, ResourceRetentionOptions? retention, TimeProvider timeProvider)
        : this(root, retention, () => (timeProvider ?? throw new ArgumentNullException(nameof(timeProvider))).GetUtcNow())
    {
    }
    internal FileResourceStore(
        string root,
        ResourceRetentionOptions? retention,
        Func<DateTimeOffset>? clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        _retention = retention ?? new ResourceRetentionOptions();
        _retention.Validate();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        Directory.CreateDirectory(_root);
    }

    public async Task<ResourceHandle> StoreAsync(
        byte[] data,
        string mimeType,
        string extension,
        string name,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        var safeExtension = NormalizeExtension(extension);
        if (data.Length > Math.Min(MaxResourceBytes, _retention.MaxBytes))
            throw new InvalidOperationException("Observation exceeds the resource byte budget (at most 5 MiB). Retry at a smaller image size.");

        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(_root, id + safeExtension);
        var createdAt = _clock();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.WriteAllBytesAsync(path, data, cancellationToken).ConfigureAwait(false);
            _resources[id] = new StoredResource(path, mimeType, name, createdAt, data.LongLength, true);
            CleanupOwnedFilesLocked(createdAt);
            return new ResourceHandle(SchemePrefix + id, mimeType, name);
        }
        catch
        {
            // A cancelled or failed write must not leave an owned orphan behind.
            TryDelete(path);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }
    public async Task<JsonElement> ReadAsync(string uri, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!TryGetId(uri, out var id))
            throw new KeyNotFoundException("Unsupported ArcGIS resource URI.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_resources.TryGetValue(id, out var resource) || !File.Exists(resource.Path))
                throw new KeyNotFoundException($"Resource '{uri}' is unavailable or expired.");
            if (resource.OwnsFile && IsExpired(resource, _clock()))
            {
                TryDelete(resource.Path);
                if (!File.Exists(resource.Path))
                    _resources.TryRemove(id, out _);
                throw new KeyNotFoundException($"Resource '{uri}' is unavailable or expired.");
            }
            await using var file = new FileStream(resource.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length > MaxResourceBytes)
                throw new InvalidOperationException("Resource exceeds the 5 MiB resource limit.");
            // Bound allocation from the opened handle and deny concurrent writers, not a racy path stat.
            var bytes = new byte[checked((int)file.Length)];
            await file.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(new
            {
                uri,
                resource.MimeType,
                resource.Name,
                encoding = "base64",
                data = Convert.ToBase64String(bytes),
                resource.CreatedAt
            }, WireOptions);
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool TryGetLocalPath(string uri, out string path)
    {
        path = string.Empty;
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!TryGetId(uri, out var id))
            return false;
        _gate.Wait();
        try
        {
            if (!_resources.TryGetValue(id, out var resource) || !File.Exists(resource.Path))
                return false;
            if (resource.OwnsFile && IsExpired(resource, _clock()))
            {
                TryDelete(resource.Path);
                if (!File.Exists(resource.Path))
                    _resources.TryRemove(id, out _);
                return false;
            }
            if (new FileInfo(resource.Path).Length > MaxResourceBytes)
                return false;
            path = resource.Path;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private bool IsExpired(StoredResource resource, DateTimeOffset now) =>
        _retention.TimeToLive is { } ttl && resource.CreatedAt <= now - ttl;
    private void CleanupOwnedFilesLocked(DateTimeOffset now)
    {
        var entries = _resources.Select(pair => new ResourceRetentionEntry(
            pair.Key, pair.Value.Bytes, pair.Value.CreatedAt, pair.Value.OwnsFile));
        foreach (var id in ResourceRetentionPlanner.SelectEvictions(entries, _retention, now))
        {
            if (!_resources.TryGetValue(id, out var resource) || !resource.OwnsFile)
                continue;
            TryDelete(resource.Path);
            // Remove the entry even when the OS temporarily denies deletion so logical
            // count/byte bounds remain enforced. Unindexed orphans are not automatically deleted.
            if (_resources.TryGetValue(id, out var current) && ReferenceEquals(current, resource))
                _resources.TryRemove(id, out _);
        }
    }

    private static string NormalizeExtension(string extension)
    {
        var value = extension.Trim();
        if (!value.StartsWith('.'))
            value = "." + value;
        if (value.Length > 16 || value.IndexOfAny(['/', '\\', ':']) >= 0 || value[1..].Contains('.'))
            throw new ArgumentException("Resource extension must be a single safe file extension.", nameof(extension));
        return value;
    }

    private static bool TryGetId(string? uri, out string id)
    {
        id = string.Empty;
        if (string.IsNullOrWhiteSpace(uri) || !uri.StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase))
            return false;
        id = uri[SchemePrefix.Length..];
        if (id.Length == 0 || id.Contains('/') || id.Contains('\\') || id.Contains("..", StringComparison.Ordinal))
        {
            id = string.Empty;
            return false;
        }
        return true;
    }
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Physical retention is best effort; never infer ownership on a later host run.
        }
        catch (UnauthorizedAccessException)
        {
            // Never escalate cleanup into a request failure.
        }
    }

    private sealed record StoredResource(
        string Path,
        string MimeType,
        string Name,
        DateTimeOffset CreatedAt,
        long Bytes,
        bool OwnsFile);
}
