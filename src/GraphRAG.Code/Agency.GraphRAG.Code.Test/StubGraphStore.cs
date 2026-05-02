using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Storage;
using ClusterRecord = Agency.GraphRAG.Code.Domain.Cluster;

namespace Agency.GraphRAG.Code.Test;

/// <summary>
/// Minimal graph-store test double with overridable query methods.
/// </summary>
public class StubGraphStore : IGraphStore
{
    public virtual Task InitializeSchemaAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public virtual Task UpsertRepoAsync(Repo repo, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public virtual Task UpsertProjectAsync(Project project, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public virtual Task UpsertExternalPackageBatchAsync(IReadOnlyList<ExternalPackage> packages, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public virtual Task UpsertFileAsync(SourceFile file, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public virtual Task UpsertModuleAsync(Module module, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public virtual Task UpsertSymbolAsync(Symbol symbol, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public virtual Task UpsertSymbolBatchAsync(IReadOnlyList<Symbol> symbols, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public virtual Task UpsertEdgeBatchAsync(IReadOnlyList<Edge> edges, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public virtual Task DeleteSymbolsByFileAsync(Guid fileId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public virtual Task DeleteFileAsync(Guid fileId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public virtual Task RenameFileAsync(Guid fileId, string newPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public virtual Task SetIndexedCommitAsync(Guid repoId, string indexedCommit, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public virtual Task<string?> LoadIndexedCommitAsync(Guid repoId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    public virtual Task<IReadOnlyList<VectorSearchResult>> VectorSearchSymbolsAsync(string queryText, int topK, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<VectorSearchResult>>([]);
    public virtual Task<IReadOnlyList<VectorSearchResult>> VectorSearchClustersAsync(string queryText, int topK, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<VectorSearchResult>>([]);
    public virtual Task<IReadOnlyList<TraversalHop>> TraverseFromAsync(TraversalRequest request, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TraversalHop>>([]);
    public virtual Task<Symbol?> GetSymbolByIdAsync(Guid symbolId, CancellationToken cancellationToken = default) => Task.FromResult<Symbol?>(null);
    public virtual Task<IReadOnlyList<Symbol>> FindSymbolsByNameAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Symbol>>([]);
    public virtual Task StageUnresolvedCallSiteBatchAsync(IReadOnlyList<UnresolvedCallSite> callSites, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public virtual Task<IReadOnlyList<UnresolvedCallSite>> DrainUnresolvedCallSitesAsync(Guid? sourceFileId = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<UnresolvedCallSite>>([]);
    public virtual Task ApplyClusterAssignmentsAsync(IReadOnlyDictionary<Guid, (Guid, string)> assignments, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public virtual Task ReplaceClusterSummariesAtomicallyAsync(IReadOnlyList<ClusterRecord> clusters, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public virtual Task<IReadOnlyDictionary<string, IReadOnlyList<Symbol>>> GetSymbolsByPathsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<Symbol>>>(new Dictionary<string, IReadOnlyList<Symbol>>());
    public virtual Task<SourceFile?> GetFileByPathAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult<SourceFile?>(null);
}
