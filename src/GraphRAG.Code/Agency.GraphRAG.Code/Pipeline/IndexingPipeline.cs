using Agency.GraphRAG.Code.ChangeDetector;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Hydration;
using Agency.GraphRAG.Code.Storage;
using Agency.GraphRAG.Code.Walker;

namespace Agency.GraphRAG.Code.Pipeline;

/// <summary>
/// Orchestrates the top-level indexing flow from repository walk through hydration and checkpoint update.
/// </summary>
public sealed class IndexingPipeline(
    IGraphStore graphStore,
    Func<Repo, CancellationToken, Task<WalkResult>> walkAsync,
    Func<Repo, WalkResult, Action<string>?, CancellationToken, Task> parseManifestsAsync,
    Func<Repo, WalkResult, Action<string>?, CancellationToken, Task<IReadOnlyDictionary<string, Phase1WriteRequest>>> buildWriteRequestsAsync,
    Func<IReadOnlyList<Phase1WriteRequest>, Action<string>?, CancellationToken, Task> summarizeAsync,
    ChangeDetector.ChangeDetector changeDetector,
    Func<Repo, WalkResult, IReadOnlyDictionary<string, Phase1WriteRequest>, CancellationToken, Task<ChangeSet>> detectChangesAsync,
    IncrementalHydrator incrementalHydrator,
    Func<IReadOnlyDictionary<string, Phase1WriteRequest>, IReadOnlyDictionary<Guid, IReadOnlyList<ExternalPackage>>> buildPackagesByFileId)
{
    /// <summary>
    /// Runs the indexing flow for the supplied repository.
    /// </summary>
    /// <param name="repo">The repository to index.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="onProgress">Optional callback for progress updates.</param>
    public async Task RunAsync(Repo repo, CancellationToken cancellationToken = default, Action<string>? onProgress = null)
    {
        ArgumentNullException.ThrowIfNull(repo);

        Guid stableId = HydrationIds.StableGuid(repo.LocalPath);
        string? indexedCommit = await graphStore.LoadIndexedCommitAsync(stableId, cancellationToken).ConfigureAwait(false);
        repo = repo with { Id = stableId, IndexedCommit = indexedCommit };

        onProgress?.Invoke("Walking repository...");
        WalkResult walkResult = await walkAsync(repo, cancellationToken).ConfigureAwait(false);
        onProgress?.Invoke($"Found {walkResult.Files.Count} files ({walkResult.Mode} scan)");

        onProgress?.Invoke("Parsing manifests...");
        await parseManifestsAsync(repo, walkResult, onProgress, cancellationToken).ConfigureAwait(false);

        onProgress?.Invoke("Building write requests...");
        IReadOnlyDictionary<string, Phase1WriteRequest> writeRequests = await buildWriteRequestsAsync(repo, walkResult, onProgress, cancellationToken).ConfigureAwait(false);
        onProgress?.Invoke($"Generated {writeRequests.Count} write requests");

        _ = changeDetector;
        onProgress?.Invoke("Detecting changes...");
        ChangeSet changeSet = await detectChangesAsync(repo, walkResult, writeRequests, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<Phase1WriteRequest> changedRequests = changeSet.AddedFiles
            .Concat(changeSet.ModifiedFiles.Select(static c => c.Path))
            .Concat(changeSet.RenamedFiles.Select(static c => c.NewPath))
            .Distinct(StringComparer.Ordinal)
            .Where(writeRequests.ContainsKey)
            .Select(p => writeRequests[p])
            .ToArray();

        int totalChunks = changedRequests.Sum(static r => r.Chunks.Count);
        onProgress?.Invoke($"Summarizing {totalChunks} symbols (this may take a while)...");
        await summarizeAsync(changedRequests, onProgress, cancellationToken).ConfigureAwait(false);
        onProgress?.Invoke($"Summarization complete for {totalChunks} symbols");

        onProgress?.Invoke("Hydrating graph...");
        await incrementalHydrator.HydrateAsync(
            changeSet,
            writeRequests,
            buildPackagesByFileId(writeRequests),
            onProgress,
            cancellationToken).ConfigureAwait(false);

        onProgress?.Invoke("Updating checkpoint...");
        if (!string.IsNullOrWhiteSpace(walkResult.HeadCommit))
        {
            await graphStore.SetIndexedCommitAsync(repo.Id, walkResult.HeadCommit, cancellationToken).ConfigureAwait(false);
        }
    }
}
