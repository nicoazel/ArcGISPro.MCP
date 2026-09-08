using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.AddIn.Services;

internal sealed class FileResourceStore
{
    private const string SchemePrefix = "arcgis://resource/";
    private readonly string _root;
    private readonly ConcurrentDictionary<string, StoredResource> _resources = new(StringComparer.OrdinalIgnoreCase);

    public FileResourceStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public async Task<ResourceHandle> StoreAsync(byte[] data, string mimeType, string extension, string name, CancellationToken cancellationToken)
    {
        if (data.Length > 5 * 1024 * 1024)
            throw new InvalidOperationException("Observation exceeds the 5 MiB resource limit. Retry at a smaller image size.");
        var id = Guid.NewGuid().ToString("N");
        var safeExtension = extension.StartsWith('.') ? extension : "." + extension;
        var path = Path.Combine(_root, id + safeExtension);
        await File.WriteAllBytesAsync(path, data, cancellationToken).ConfigureAwait(false);
        _resources[id] = new StoredResource(path, mimeType, name, DateTimeOffset.UtcNow);
        return new ResourceHandle(SchemePrefix + id, mimeType, name);
    }

    public async Task<JsonElement> ReadAsync(string uri, CancellationToken cancellationToken)
    {
        if (!uri.StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase))
            throw new KeyNotFoundException("Unsupported ArcGIS resource URI.");
        var id = uri[SchemePrefix.Length..];
        if (!_resources.TryGetValue(id, out var resource))
            throw new KeyNotFoundException($"Resource '{uri}' is unavailable or expired.");
        if (!File.Exists(resource.Path))
            throw new KeyNotFoundException($"Resource '{uri}' is unavailable or expired.");
        var bytes = await File.ReadAllBytesAsync(resource.Path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new
        {
            uri, resource.MimeType, resource.Name, encoding = "base64",
            data = Convert.ToBase64String(bytes), resource.CreatedAt
        });
    }

    public bool TryGetLocalPath(string uri, out string path)
    {
        path = string.Empty;
        if (!uri.StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase)) return false;
        var id = uri[SchemePrefix.Length..];
        if (!_resources.TryGetValue(id, out var resource) || !File.Exists(resource.Path)) return false;
        path = resource.Path;
        return true;
    }

    private sealed record StoredResource(string Path, string MimeType, string Name, DateTimeOffset CreatedAt);
}
