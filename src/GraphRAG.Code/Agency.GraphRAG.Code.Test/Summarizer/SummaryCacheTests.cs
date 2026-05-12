using Agency.GraphRAG.Code.Summarizer;

namespace Agency.GraphRAG.Code.Test.Summarizer;

/// <summary>
/// Helpers for comparing <see cref="SummaryCacheEntry"/> values structurally.
/// The auto-generated record equality uses reference equality for
/// <see cref="IReadOnlyList{T}"/>, so we cannot rely on <c>Assert.Equal(entry, entry)</c>
/// across a serialize/deserialize round-trip.
/// </summary>
internal static class SummaryCacheEntryAssert
{
    public static void Equal(SummaryCacheEntry expected, SummaryCacheEntry? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.OneLine, actual.OneLine);
        Assert.Equal(expected.Detailed, actual.Detailed);
        Assert.Equal(expected.ProbableCallees, actual.ProbableCallees);
    }
}

/// <summary>
/// Tests for <see cref="SummaryCache"/>.
/// </summary>
public sealed class SummaryCacheTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"summary-cache-{Guid.NewGuid():N}.db");

    /// <summary>
    /// Removes the temp database (and any WAL/shm sidecars) created by the test.
    /// </summary>
    public void Dispose()
    {
        foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            string path = this._databasePath + suffix;
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; the temp directory will reclaim it eventually.
                }
            }
        }
    }

    [Fact]
    public void Set_ThenTryGet_WithSameHashAndTier_ReturnsCachedEntry()
    {
        using SummaryCache cache = new(this._databasePath);
        SummaryCacheEntry expected = new("One line", "Detailed summary", ["CallA", "CallB"]);

        cache.Set("chunk-hash-1", "strong", expected);

        bool found = cache.TryGet("chunk-hash-1", "strong", out SummaryCacheEntry? actual);

        Assert.True(found);
        SummaryCacheEntryAssert.Equal(expected, actual);
    }

    [Fact]
    public void TryGet_WithMissingKey_ReturnsFalse()
    {
        using SummaryCache cache = new(this._databasePath);

        bool found = cache.TryGet("missing-hash", "cheap", out SummaryCacheEntry? actual);

        Assert.False(found);
        Assert.Null(actual);
    }

    [Fact]
    public void Set_SameHashWithDifferentModelTiers_KeepsSeparateEntries()
    {
        using SummaryCache cache = new(this._databasePath);
        SummaryCacheEntry cheapest = new("Cheap", "Cheap detail", ["LeafCall"]);
        SummaryCacheEntry strong = new("Strong", "Strong detail", ["InterfaceCall"]);

        cache.Set("chunk-hash-1", "cheapest", cheapest);
        cache.Set("chunk-hash-1", "strong", strong);

        Assert.True(cache.TryGet("chunk-hash-1", "cheapest", out SummaryCacheEntry? cheapestActual));
        Assert.True(cache.TryGet("chunk-hash-1", "strong", out SummaryCacheEntry? strongActual));
        SummaryCacheEntryAssert.Equal(cheapest, cheapestActual);
        SummaryCacheEntryAssert.Equal(strong, strongActual);
    }

    [Fact]
    public void Set_SameModelTierWithDifferentHashes_KeepsSeparateEntries()
    {
        using SummaryCache cache = new(this._databasePath);
        SummaryCacheEntry first = new("First", "First detail", ["CallA"]);
        SummaryCacheEntry second = new("Second", "Second detail", ["CallB"]);

        cache.Set("chunk-hash-1", "cheap", first);
        cache.Set("chunk-hash-2", "cheap", second);

        Assert.True(cache.TryGet("chunk-hash-1", "cheap", out SummaryCacheEntry? firstActual));
        Assert.True(cache.TryGet("chunk-hash-2", "cheap", out SummaryCacheEntry? secondActual));
        SummaryCacheEntryAssert.Equal(first, firstActual);
        SummaryCacheEntryAssert.Equal(second, secondActual);
    }

    [Fact]
    public void Set_SameHashAndTier_Twice_ReplacesExistingEntry()
    {
        using SummaryCache cache = new(this._databasePath);
        SummaryCacheEntry original = new("Original", "Original detail", ["CallA"]);
        SummaryCacheEntry updated = new("Updated", "Updated detail", ["CallB"]);

        cache.Set("chunk-hash-1", "cheap", original);
        cache.Set("chunk-hash-1", "cheap", updated);

        Assert.True(cache.TryGet("chunk-hash-1", "cheap", out SummaryCacheEntry? actual));
        SummaryCacheEntryAssert.Equal(updated, actual);
    }

    /// <summary>
    /// The crash-resume guarantee: a summary written by one cache instance must be readable
    /// by a fresh instance opening the same database file (simulating a process restart).
    /// </summary>
    [Fact]
    public void Set_InOneInstance_TryGet_InNewInstance_ReturnsCachedEntry()
    {
        SummaryCacheEntry expected = new("One line", "Detailed summary", ["CallA", "CallB"]);

        using (SummaryCache writer = new(this._databasePath))
        {
            writer.Set("chunk-hash-1", "strong", expected);
        }

        using SummaryCache reader = new(this._databasePath);
        bool found = reader.TryGet("chunk-hash-1", "strong", out SummaryCacheEntry? actual);

        Assert.True(found);
        SummaryCacheEntryAssert.Equal(expected, actual);
    }

    [Fact]
    public void Set_PersistsEmptyProbableCalleesList()
    {
        SummaryCacheEntry expected = new("One line", "Detailed", []);

        using (SummaryCache writer = new(this._databasePath))
        {
            writer.Set("chunk-hash-1", "cheap", expected);
        }

        using SummaryCache reader = new(this._databasePath);
        Assert.True(reader.TryGet("chunk-hash-1", "cheap", out SummaryCacheEntry? actual));
        Assert.NotNull(actual);
        Assert.Empty(actual.ProbableCallees);
    }

    [Fact]
    public void TryGetAny_WithNoEntries_ReturnsFalse()
    {
        using SummaryCache cache = new(this._databasePath);

        bool found = cache.TryGetAny("missing-hash", out SummaryCacheEntry? actual);

        Assert.False(found);
        Assert.Null(actual);
    }

    [Fact]
    public void TryGetAny_WithSingleEntry_ReturnsIt()
    {
        using SummaryCache cache = new(this._databasePath);
        SummaryCacheEntry expected = new("One line", "Detailed", ["Call"]);
        cache.Set("hash-1", "Cheap", expected);

        bool found = cache.TryGetAny("hash-1", out SummaryCacheEntry? actual);

        Assert.True(found);
        SummaryCacheEntryAssert.Equal(expected, actual);
    }

    [Fact]
    public void TryGetAny_PrefersStrongerTier_WhenMultipleTiersStored()
    {
        using SummaryCache cache = new(this._databasePath);
        SummaryCacheEntry cheap = new("Cheap one-line", "Cheap detail", []);
        SummaryCacheEntry strong = new("Strong one-line", "Strong detail", ["InterfaceCall"]);
        cache.Set("hash-1", "Cheap", cheap);
        cache.Set("hash-1", "Strong", strong);

        bool found = cache.TryGetAny("hash-1", out SummaryCacheEntry? actual);

        Assert.True(found);
        SummaryCacheEntryAssert.Equal(strong, actual);
    }

    [Fact]
    public void TryGetAny_ReturnsHit_WhenExactTierIsDifferentFromCachedTier()
    {
        // Simulates the incremental-walk scenario: symbol cached as Standard on a full run,
        // but on a subsequent incremental run isLeaf changes and the code looks up Cheap.
        // TryGetAny should still find the Standard entry and avoid a redundant LLM call.
        using SummaryCache cache = new(this._databasePath);
        SummaryCacheEntry standard = new("Standard line", "Standard detail", ["Callee"]);
        cache.Set("hash-1", "Standard", standard);

        bool exactMiss = cache.TryGet("hash-1", "Cheap", out _);
        bool anyHit = cache.TryGetAny("hash-1", out SummaryCacheEntry? fallback);

        Assert.False(exactMiss);
        Assert.True(anyHit);
        SummaryCacheEntryAssert.Equal(standard, fallback);
    }

    [Fact]
    public void SetEmbedding_ThenTryGetEmbedding_ReturnsCachedEmbedding()
    {
        using SummaryCache cache = new(this._databasePath);
        float[] floats = [1.0f, 2.0f, 3.0f];

        cache.SetEmbedding("chunk-hash-1", "text-embedding-3-small", new ReadOnlyMemory<float>(floats));

        bool found = cache.TryGetEmbedding("chunk-hash-1", "text-embedding-3-small", out ReadOnlyMemory<float> actual);

        Assert.True(found);
        Assert.Equal(floats, actual.ToArray());
    }

    [Fact]
    public void TryGetEmbedding_WrongModel_ReturnsFalse()
    {
        using SummaryCache cache = new(this._databasePath);
        cache.SetEmbedding("chunk-hash-1", "model-a", new ReadOnlyMemory<float>([1.0f]));

        bool found = cache.TryGetEmbedding("chunk-hash-1", "model-b", out ReadOnlyMemory<float> embedding);

        Assert.False(found);
        Assert.Equal(default, embedding);
    }

    /// <summary>
    /// The crash-resume guarantee for embeddings: a vector written by one instance must be readable
    /// by a fresh instance opening the same file (simulating a process restart mid-hydration).
    /// </summary>
    [Fact]
    public void SetEmbedding_InOneInstance_TryGetEmbedding_InNewInstance_ReturnsCachedEmbedding()
    {
        float[] floats = [0.1f, 0.2f, 0.3f];

        using (SummaryCache writer = new(this._databasePath))
        {
            writer.SetEmbedding("chunk-hash-1", "text-embedding-3-small", new ReadOnlyMemory<float>(floats));
        }

        using SummaryCache reader = new(this._databasePath);
        bool found = reader.TryGetEmbedding("chunk-hash-1", "text-embedding-3-small", out ReadOnlyMemory<float> actual);

        Assert.True(found);
        Assert.Equal(floats, actual.ToArray());
    }
}
