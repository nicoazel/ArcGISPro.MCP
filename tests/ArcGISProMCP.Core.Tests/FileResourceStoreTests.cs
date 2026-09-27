using System.Text.Json;
using ArcGISProMCP.Core.Resources;

namespace ArcGISProMCP.Core.Tests;

public sealed class FileResourceStoreTests
{
    [Fact]
    public async Task Retention_leaves_unindexed_demo_files_untouched()
    {
        var root = CreateTempDirectory();
        var externalPath = Path.Combine(root, "demo-export.png");
        await File.WriteAllBytesAsync(externalPath, [9, 8, 7], TestContext.Current.CancellationToken);

        using var store = new FileResourceStore(root, new ResourceRetentionOptions(MaxCount: 1, MaxBytes: 1024));
        var first = await store.StoreAsync([1], "image/png", ".png", "first", CancellationToken.None);
        var second = await store.StoreAsync([2], "image/png", ".png", "second", CancellationToken.None);

        Assert.True(File.Exists(externalPath));
        Assert.False(store.TryGetLocalPath(first.Uri, out _));
        Assert.True(store.TryGetLocalPath(second.Uri, out _));
    }

    [Fact]
    public async Task Expired_owned_resources_are_unavailable_on_read_without_a_new_store()
    {
        var root = CreateTempDirectory();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        using var store = new FileResourceStore(
            root,
            new ResourceRetentionOptions(MaxCount: 10, MaxBytes: 1024, TimeToLive: TimeSpan.FromMinutes(5)),
            clock);
        var handle = await store.StoreAsync([1], "image/png", ".png", "expired", CancellationToken.None);
        clock.Now = clock.Now.AddMinutes(6);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.ReadAsync(handle.Uri, CancellationToken.None));
        Assert.False(store.TryGetLocalPath(handle.Uri, out _));
    }

    [Fact]
    public async Task Read_rejects_files_that_grow_beyond_the_observation_bound()
    {
        var root = CreateTempDirectory();
        using var store = new FileResourceStore(root);
        var handle = await store.StoreAsync([1], "image/png", ".png", "large", CancellationToken.None);
        Assert.True(store.TryGetLocalPath(handle.Uri, out var path));
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
            stream.SetLength(FileResourceStore.MaxResourceBytes + 1L);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync(handle.Uri, CancellationToken.None));
        Assert.False(store.TryGetLocalPath(handle.Uri, out _));
    }

    [Fact]
    public async Task Read_and_store_are_safe_under_concurrent_use()
    {
        var root = CreateTempDirectory();
        using var store = new FileResourceStore(root, new ResourceRetentionOptions(MaxCount: 64, MaxBytes: 64 * 1024));
        var handles = await Task.WhenAll(Enumerable.Range(0, 16).Select(async index =>
            await store.StoreAsync([(byte)index], "application/octet-stream", ".bin", $"r{index}", CancellationToken.None)));

        var resources = await Task.WhenAll(handles.Select(handle => store.ReadAsync(handle.Uri, CancellationToken.None)));
        Assert.Equal(handles.Length, resources.Length);
        Assert.All(resources, resource =>
        {
            Assert.Equal("base64", resource.GetProperty("encoding").GetString());
            Assert.True(resource.GetProperty("data").GetString()!.Length > 0);
        });
    }

    [Fact]
    public async Task Rejects_path_traversal_extensions()
    {
        var root = CreateTempDirectory();
        using var store = new FileResourceStore(root);

        await Assert.ThrowsAsync<ArgumentException>(() => store.StoreAsync(
            [1], "image/png", "../escape", "bad", CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(Directory.GetParent(root)!.FullName, "escape*"));
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ArcGISProMcpTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
