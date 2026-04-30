using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Agency.GraphRAG.Code.Summarizer;

/// <summary>
/// Caches generated summaries by chunk content hash and model tier.
/// </summary>
public sealed class SummaryCache
{
    private readonly ConcurrentDictionary<SummaryCacheKey, SummaryCacheEntry> _entries = new();

    /// <summary>
    /// Attempts to read a cached summary.
    /// </summary>
    /// <param name="chunkContentHash">The chunk content hash.</param>
    /// <param name="modelTier">The model tier used to generate the summary.</param>
    /// <param name="entry">The cached summary when present.</param>
    /// <returns><see langword="true"/> when a matching entry exists; otherwise <see langword="false"/>.</returns>
    public bool TryGet(string chunkContentHash, string modelTier, [NotNullWhen(true)] out SummaryCacheEntry? entry)
    {
        SummaryCacheKey key = CreateKey(chunkContentHash, modelTier);
        return this._entries.TryGetValue(key, out entry);
    }

    /// <summary>
    /// Stores or replaces a cached summary.
    /// </summary>
    /// <param name="chunkContentHash">The chunk content hash.</param>
    /// <param name="modelTier">The model tier used to generate the summary.</param>
    /// <param name="entry">The summary to cache.</param>
    public void Set(string chunkContentHash, string modelTier, SummaryCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        SummaryCacheKey key = CreateKey(chunkContentHash, modelTier);
        this._entries[key] = entry;
    }

    private static SummaryCacheKey CreateKey(string chunkContentHash, string modelTier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkContentHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelTier);

        return new SummaryCacheKey(chunkContentHash, modelTier);
    }

    private readonly record struct SummaryCacheKey(string ChunkContentHash, string ModelTier);
}

/// <summary>
/// Represents a cached summarization result.
/// </summary>
/// <param name="OneLine">The one-line summary.</param>
/// <param name="Detailed">The detailed summary.</param>
/// <param name="ProbableCallees">The probable callees extracted from the summary.</param>
public sealed record SummaryCacheEntry
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SummaryCacheEntry"/> class.
    /// </summary>
    /// <param name="oneLine">The one-line summary.</param>
    /// <param name="detailed">The detailed summary.</param>
    /// <param name="probableCallees">The probable callees extracted from the summary.</param>
    public SummaryCacheEntry(string oneLine, string detailed, IReadOnlyList<string> probableCallees)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oneLine);
        ArgumentException.ThrowIfNullOrWhiteSpace(detailed);
        ArgumentNullException.ThrowIfNull(probableCallees);

        this.OneLine = oneLine;
        this.Detailed = detailed;
        this.ProbableCallees = probableCallees.ToArray();
    }

    /// <summary>
    /// Gets the one-line summary.
    /// </summary>
    public string OneLine { get; }

    /// <summary>
    /// Gets the detailed summary.
    /// </summary>
    public string Detailed { get; }

    /// <summary>
    /// Gets the probable callees.
    /// </summary>
    public IReadOnlyList<string> ProbableCallees { get; }
}
