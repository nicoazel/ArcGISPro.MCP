namespace ArcGISProMCP.Core.Resources;

/// <summary>
/// Bounds the in-memory resource index and the files owned by a resource store.
/// Retention never applies to resources registered from an external path.
/// </summary>
public sealed record ResourceRetentionOptions(
    int MaxCount = 100,
    long MaxBytes = 64L * 1024 * 1024,
    TimeSpan? TimeToLive = null)
{
    public void Validate()
    {
        if (MaxCount < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxCount), "MaxCount must be at least one.");
        if (MaxBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxBytes), "MaxBytes must be positive.");
        if (TimeToLive is { } ttl && ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(TimeToLive), "TimeToLive must be positive when specified.");
    }
}

/// <param name="Id">Stable resource identifier within a store.</param>
/// <param name="Bytes">Current file size in bytes.</param>
/// <param name="CreatedAt">Creation timestamp used for deterministic age ordering.</param>
/// <param name="OwnsFile">Only owned files may be selected for eviction.</param>
public sealed record ResourceRetentionEntry(
    string Id,
    long Bytes,
    DateTimeOffset CreatedAt,
    bool OwnsFile);

/// <summary>
/// Pure, deterministic retention policy selection. File deletion remains the responsibility
/// of the owning store, which can handle platform-specific failures safely.
/// </summary>
public static class ResourceRetentionPlanner
{
    public static IReadOnlyList<string> SelectEvictions(
        IEnumerable<ResourceRetentionEntry> entries,
        ResourceRetentionOptions options,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var owned = entries
            .Where(entry => entry.OwnsFile)
            .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(entry => entry.CreatedAt)
            .ThenBy(entry => entry.Id, StringComparer.Ordinal)
            .ToArray();

        var evicted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (options.TimeToLive is { } ttl)
        {
            foreach (var entry in owned)
            {
                if (entry.CreatedAt <= now - ttl)
                    evicted.Add(entry.Id);
            }
        }

        var remainingCount = owned.Length - evicted.Count;
        var remainingBytes = owned
            .Where(entry => !evicted.Contains(entry.Id))
            .Sum(entry => entry.Bytes);

        foreach (var entry in owned)
        {
            if (remainingCount <= options.MaxCount && remainingBytes <= options.MaxBytes)
                break;
            if (!evicted.Add(entry.Id))
                continue;

            remainingCount--;
            remainingBytes -= entry.Bytes;
        }

        return owned
            .Where(entry => evicted.Contains(entry.Id))
            .Select(entry => entry.Id)
            .ToArray();
    }
}
