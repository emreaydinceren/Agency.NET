using Agency.KeyValueStore.Common;

namespace Agency.Indexer;

/// <summary>The settings an index was built with, fixed on its first run.</summary>
/// <param name="Root">The full path of the indexed directory.</param>
/// <param name="Extensions">The selected extensions.</param>
/// <param name="Names">The selected extensionless file names.</param>
/// <param name="EmbeddingModel">The embedding model the chunks were embedded with.</param>
internal sealed record IndexConfig(string Root, IReadOnlyList<string> Extensions, IReadOnlyList<string> Names, string EmbeddingModel);

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
