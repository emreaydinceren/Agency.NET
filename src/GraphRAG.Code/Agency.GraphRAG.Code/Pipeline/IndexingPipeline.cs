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
    Func<Repo, WalkResult, CancellationToken, Task> parseManifestsAsync,
    Func<Repo, WalkResult, CancellationToken, Task<IReadOnlyDictionary<string, Phase1WriteRequest>>> buildWriteRequestsAsync,
    Func<IReadOnlyList<Phase1WriteRequest>, CancellationToken, Task> summarizeAsync,
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
    public async Task RunAsync(Repo repo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);

        WalkResult walkResult = await walkAsync(repo, cancellationToken).ConfigureAwait(false);
        await parseManifestsAsync(repo, walkResult, cancellationToken).ConfigureAwait(false);

        IReadOnlyDictionary<string, Phase1WriteRequest> writeRequests = await buildWriteRequestsAsync(repo, walkResult, cancellationToken).ConfigureAwait(false);
        await summarizeAsync(writeRequests.Values.ToArray(), cancellationToken).ConfigureAwait(false);

        _ = changeDetector;
        ChangeSet changeSet = await detectChangesAsync(repo, walkResult, writeRequests, cancellationToken).ConfigureAwait(false);
        await incrementalHydrator.HydrateAsync(
            changeSet,
            writeRequests,
            buildPackagesByFileId(writeRequests),
            cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(walkResult.HeadCommit))
        {
            await graphStore.SetIndexedCommitAsync(repo.Id, walkResult.HeadCommit, cancellationToken).ConfigureAwait(false);
        }
    }
}
