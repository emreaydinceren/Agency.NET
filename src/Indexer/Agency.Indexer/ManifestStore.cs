using Agency.KeyValueStore.Common;

namespace Agency.Indexer;

/// <summary>The settings an index was built with, fixed on its first run.</summary>
/// <param name="Root">The full path of the indexed directory.</param>
/// <param name="Extensions">The selected extensions.</param>
/// <param name="Names">The selected extensionless file names.</param>
/// <param name="EmbeddingModel">The embedding model the chunks were embedded with.</param>
/// <param name="Excludes">Globs of files and folders left out, or <see langword="null"/> for none (also what indexes created before this existed read as).</param>
/// <param name="Calibration">The noise floor measured by <c>calibrate --save</c>, if any.</param>
/// <param name="FormatVersion">1 for chunk-level indexes (what indexes created before this existed read as), 2 for passage-level ones.</param>
/// <param name="PassageLines">The most lines in a passage of a format 2 index, or 0.</param>
/// <param name="PassageOverlap">The lines consecutive passages share in a format 2 index.</param>
/// <param name="Noise">The score distribution of unrelated queries, measured after each index run, used to normalize scores.</param>
/// <param name="CommonTerms">Words found in most files (a project name), left out of the embedded query.</param>
internal sealed record IndexConfig(
    string Root,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> Names,
    string EmbeddingModel,
    IReadOnlyList<string>? Excludes = null,
    Calibration? Calibration = null,
    int FormatVersion = 1,
    int PassageLines = 0,
    int PassageOverlap = 0,
    NoiseStats? Noise = null,
    IReadOnlyList<string>? CommonTerms = null);

/// <summary>The score threshold measured for an index.</summary>
/// <param name="NoiseCeiling">The best score unrelated queries reached.</param>
/// <param name="SuggestedMinScore">A threshold just above <paramref name="NoiseCeiling"/>.</param>
/// <param name="AnswerFloor">The 10th percentile score of the expected pages of real questions, when <c>calibrate --questions</c> was used.</param>
internal sealed record Calibration(double NoiseCeiling, double SuggestedMinScore, double? AnswerFloor = null);

/// <summary>The best scores of unrelated queries: what "no match" looks like for this model and corpus.</summary>
/// <param name="Mean">The mean best score.</param>
/// <param name="StdDev">The standard deviation of the best scores.</param>
/// <param name="Ceiling">The highest best score.</param>
internal sealed record NoiseStats(double Mean, double StdDev, double Ceiling)
{
    /// <summary>The smallest deviation used, so a very tight distribution does not make every score look extreme.</summary>
    public const double MinStdDev = 0.005;

    /// <summary>How many standard deviations <paramref name="score"/> sits above the noise mean.</summary>
    public double Normalize(double score) => (score - this.Mean) / Math.Max(this.StdDev, MinStdDev);
}

/// <summary>The index format versions.</summary>
internal static class IndexFormat
{
    /// <summary>Chunk-level (the splitter's chunks).</summary>
    public const int Chunks = 1;

    /// <summary>Passage-level (a few whole lines each, embedded with their heading path).</summary>
    public const int Passages = 2;

    /// <summary>The format new and rebuilt indexes use.</summary>
    public const int Current = Passages;
}

/// <summary>
/// Persists index configurations and per-file manifests in an <see cref="IKVStore"/>. Each index's file
/// entries live under the session <c>files:&lt;index&gt;</c>, keyed by full path; configurations live under
/// the session <c>indexes</c>, keyed by index name.
/// </summary>
internal sealed class ManifestStore(IKVStore store)
{
    private const string ConfigSession = "indexes";

    /// <summary>Returns the configuration of <paramref name="index"/>, or <see langword="null"/> if it has never been indexed.</summary>
    public async Task<IndexConfig?> GetConfigAsync(string index, CancellationToken ct)
    {
        var hits = await store.SearchAsync<IndexConfig>(new Query(IndexService.UserId, ConfigSession, index, null, Limit: 1), ct);
        return hits.Count > 0 ? hits[0].Value : null;
    }

    /// <summary>Returns the configuration of every index, keyed by index name.</summary>
    public async Task<IReadOnlyList<(string Index, IndexConfig Config)>> GetAllConfigsAsync(CancellationToken ct)
    {
        var hits = await store.SearchAsync<IndexConfig>(new Query(IndexService.UserId, ConfigSession, null, null, Limit: int.MaxValue), ct);
        return hits.Select(h => (h.Key, h.Value)).OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>Stores the configuration of <paramref name="index"/>.</summary>
    public Task SaveConfigAsync(string index, IndexConfig config, CancellationToken ct) =>
        store.UpsertAsync(IndexService.UserId, ConfigSession, index, config, cancellationToken: ct);

    /// <summary>Returns every manifest entry of <paramref name="index"/>.</summary>
    public async Task<IReadOnlyList<ManifestEntry>> GetEntriesAsync(string index, CancellationToken ct)
    {
        var hits = await store.SearchAsync<ManifestEntry>(new Query(IndexService.UserId, FilesSession(index), null, null, Limit: int.MaxValue), ct);
        return hits.Select(h => h.Value).OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>Records <paramref name="entry"/> as the indexed state of its file.</summary>
    public Task SaveEntryAsync(string index, ManifestEntry entry, CancellationToken ct) =>
        store.UpsertAsync(IndexService.UserId, FilesSession(index), entry.Path, entry, cancellationToken: ct);

    /// <summary>Removes the manifest entry for <paramref name="path"/>.</summary>
    public Task DeleteEntryAsync(string index, string path, CancellationToken ct) =>
        store.DeleteAsync(IndexService.UserId, FilesSession(index), path, ct);

    /// <summary>Removes the configuration and every manifest entry of <paramref name="index"/>.</summary>
    public async Task DeleteIndexAsync(string index, CancellationToken ct)
    {
        foreach (ManifestEntry entry in await this.GetEntriesAsync(index, ct))
        {
            await this.DeleteEntryAsync(index, entry.Path, ct);
        }

        await store.DeleteAsync(IndexService.UserId, ConfigSession, index, ct);
    }

    private static string FilesSession(string index) => $"files:{index}";
}
