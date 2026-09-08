using ArcGISProMCP.Core.Resources;

namespace ArcGISProMCP.Core.Tests;

public sealed class ResourceRetentionPlannerTests
{
    [Fact]
    public void Selects_expired_and_then_oldest_owned_entries_deterministically()
    {
        var now = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        var entries = new[]
        {
            new ResourceRetentionEntry("old", 2, now.AddHours(-3), true),
            new ResourceRetentionEntry("middle", 2, now.AddHours(-2), true),
            new ResourceRetentionEntry("new", 2, now.AddHours(-1), true),
        };

        var result = ResourceRetentionPlanner.SelectEvictions(
            entries,
            new ResourceRetentionOptions(MaxCount: 3, MaxBytes: 64, TimeToLive: TimeSpan.FromHours(4)),
            now);

        Assert.Empty(result);

        result = ResourceRetentionPlanner.SelectEvictions(
            entries,
            new ResourceRetentionOptions(MaxCount: 2, MaxBytes: 4),
            now);

        Assert.Equal(["old"], result);
    }

    [Fact]
    public void Ttl_eviction_precedes_size_eviction_and_external_paths_are_untouched()
    {
        var now = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        var entries = new[]
        {
            new ResourceRetentionEntry("external-demo", 10_000, now.AddDays(-30), false),
            new ResourceRetentionEntry("expired", 1, now.AddHours(-2), true),
            new ResourceRetentionEntry("recent", 100, now.AddMinutes(-1), true),
        };

        var result = ResourceRetentionPlanner.SelectEvictions(
            entries,
            new ResourceRetentionOptions(MaxCount: 10, MaxBytes: 10_000, TimeToLive: TimeSpan.FromHours(1)),
            now);

        Assert.Equal(["expired"], result);
        Assert.DoesNotContain("external-demo", result);
    }

    [Fact]
    public void Size_limit_evicts_oldest_until_both_bounds_are_satisfied()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new[]
        {
            new ResourceRetentionEntry("a", 4, now.AddMinutes(-3), true),
            new ResourceRetentionEntry("b", 3, now.AddMinutes(-2), true),
            new ResourceRetentionEntry("c", 2, now.AddMinutes(-1), true),
        };

        var result = ResourceRetentionPlanner.SelectEvictions(
            entries,
            new ResourceRetentionOptions(MaxCount: 2, MaxBytes: 4),
            now);

        Assert.Equal(["a", "b"], result);
    }

    [Fact]
    public void Equal_timestamps_use_ordinal_id_tiebreaker()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new[]
        {
            new ResourceRetentionEntry("z", 1, now, true),
            new ResourceRetentionEntry("a", 1, now, true),
        };

        var result = ResourceRetentionPlanner.SelectEvictions(
            entries,
            new ResourceRetentionOptions(MaxCount: 1, MaxBytes: 100),
            now);

        Assert.Equal(["a"], result);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void Rejects_non_positive_bounds(int maxCount, long maxBytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ResourceRetentionPlanner.SelectEvictions(
            [], new ResourceRetentionOptions(maxCount, maxBytes), DateTimeOffset.UtcNow));
    }
}
