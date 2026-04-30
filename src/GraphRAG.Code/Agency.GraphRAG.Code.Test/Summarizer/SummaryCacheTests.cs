using Agency.GraphRAG.Code.Summarizer;

namespace Agency.GraphRAG.Code.Test.Summarizer;

/// <summary>
/// Tests for <see cref="SummaryCache"/>.
/// </summary>
public sealed class SummaryCacheTests
{
    [Fact]
    public void Set_ThenTryGet_WithSameHashAndTier_ReturnsCachedEntry()
    {
        SummaryCache cache = new();
        SummaryCacheEntry expected = new("One line", "Detailed summary", ["CallA", "CallB"]);

        cache.Set("chunk-hash-1", "strong", expected);

        bool found = cache.TryGet("chunk-hash-1", "strong", out SummaryCacheEntry? actual);

        Assert.True(found);
        Assert.NotNull(actual);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryGet_WithMissingKey_ReturnsFalse()
    {
        SummaryCache cache = new();

        bool found = cache.TryGet("missing-hash", "cheap", out SummaryCacheEntry? actual);

        Assert.False(found);
        Assert.Null(actual);
    }

    [Fact]
    public void Set_SameHashWithDifferentModelTiers_KeepsSeparateEntries()
    {
        SummaryCache cache = new();
        SummaryCacheEntry cheapest = new("Cheap", "Cheap detail", ["LeafCall"]);
        SummaryCacheEntry strong = new("Strong", "Strong detail", ["InterfaceCall"]);

        cache.Set("chunk-hash-1", "cheapest", cheapest);
        cache.Set("chunk-hash-1", "strong", strong);

        Assert.True(cache.TryGet("chunk-hash-1", "cheapest", out SummaryCacheEntry? cheapestActual));
        Assert.True(cache.TryGet("chunk-hash-1", "strong", out SummaryCacheEntry? strongActual));
        Assert.Equal(cheapest, cheapestActual);
        Assert.Equal(strong, strongActual);
    }

    [Fact]
    public void Set_SameModelTierWithDifferentHashes_KeepsSeparateEntries()
    {
        SummaryCache cache = new();
        SummaryCacheEntry first = new("First", "First detail", ["CallA"]);
        SummaryCacheEntry second = new("Second", "Second detail", ["CallB"]);

        cache.Set("chunk-hash-1", "cheap", first);
        cache.Set("chunk-hash-2", "cheap", second);

        Assert.True(cache.TryGet("chunk-hash-1", "cheap", out SummaryCacheEntry? firstActual));
        Assert.True(cache.TryGet("chunk-hash-2", "cheap", out SummaryCacheEntry? secondActual));
        Assert.Equal(first, firstActual);
        Assert.Equal(second, secondActual);
    }

    [Fact]
    public void Set_SameHashAndTier_Twice_ReplacesExistingEntry()
    {
        SummaryCache cache = new();
        SummaryCacheEntry original = new("Original", "Original detail", ["CallA"]);
        SummaryCacheEntry updated = new("Updated", "Updated detail", ["CallB"]);

        cache.Set("chunk-hash-1", "cheap", original);
        cache.Set("chunk-hash-1", "cheap", updated);

        Assert.True(cache.TryGet("chunk-hash-1", "cheap", out SummaryCacheEntry? actual));
        Assert.Equal(updated, actual);
    }
}
