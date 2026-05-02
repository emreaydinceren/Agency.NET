using Agency.GraphRAG.Code.Domain;

namespace Agency.GraphRAG.Code.Storage;

/// <summary>
/// Defines the persistence operations required by the GraphRAG code indexer.
/// </summary>
public interface IGraphStore
{
    /// <summary>Initializes or migrates the backing schema.</summary>
    Task InitializeSchemaAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates or updates a repository record.</summary>
    Task UpsertRepoAsync(Repo repo, CancellationToken cancellationToken = default);

    /// <summary>Creates or updates a project record.</summary>
    Task UpsertProjectAsync(Project project, CancellationToken cancellationToken = default);

    /// <summary>Creates or updates a batch of external package records.</summary>
    Task UpsertExternalPackageBatchAsync(
        IReadOnlyList<ExternalPackage> packages,
        CancellationToken cancellationToken = default);

    /// <summary>Creates or updates a file record.</summary>
    Task UpsertFileAsync(SourceFile file, CancellationToken cancellationToken = default);

    /// <summary>Creates or updates a module record.</summary>
    Task UpsertModuleAsync(Module module, CancellationToken cancellationToken = default);

    /// <summary>Creates or updates a symbol record.</summary>
    Task UpsertSymbolAsync(Symbol symbol, CancellationToken cancellationToken = default);

    /// <summary>Creates or updates a batch of symbol records.</summary>
    Task UpsertSymbolBatchAsync(IReadOnlyList<Symbol> symbols, CancellationToken cancellationToken = default);

    /// <summary>Creates or updates a batch of edge records.</summary>
    Task UpsertEdgeBatchAsync(IReadOnlyList<Edge> edges, CancellationToken cancellationToken = default);

    /// <summary>Deletes all symbols owned by the specified file.</summary>
    Task DeleteSymbolsByFileAsync(Guid fileId, CancellationToken cancellationToken = default);

    /// <summary>Deletes the specified file and any owned graph data.</summary>
    Task DeleteFileAsync(Guid fileId, CancellationToken cancellationToken = default);

    /// <summary>Renames the specified file without changing its identity.</summary>
    Task RenameFileAsync(Guid fileId, string newPath, CancellationToken cancellationToken = default);

    /// <summary>Loads the last indexed commit SHA for a repository.</summary>
    Task<string?> LoadIndexedCommitAsync(Guid repoId, CancellationToken cancellationToken = default);

    /// <summary>Stores the last indexed commit SHA for a repository.</summary>
    Task SetIndexedCommitAsync(Guid repoId, string indexedCommit, CancellationToken cancellationToken = default);

    /// <summary>Runs vector similarity search over indexed symbols.</summary>
    Task<IReadOnlyList<VectorSearchResult>> VectorSearchSymbolsAsync(
        string queryText,
        int topK,
        CancellationToken cancellationToken = default);

    /// <summary>Runs vector similarity search over cluster summaries.</summary>
    Task<IReadOnlyList<VectorSearchResult>> VectorSearchClustersAsync(
        string queryText,
        int topK,
        CancellationToken cancellationToken = default);

    /// <summary>Traverses the graph from one or more seed symbols.</summary>
    Task<IReadOnlyList<TraversalHop>> TraverseFromAsync(
        TraversalRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Gets a symbol by its unique identifier.</summary>
    Task<Symbol?> GetSymbolByIdAsync(Guid symbolId, CancellationToken cancellationToken = default);

    /// <summary>Finds symbols by simple name.</summary>
    Task<IReadOnlyList<Symbol>> FindSymbolsByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Stages a batch of unresolved call sites for later resolution.</summary>
    Task StageUnresolvedCallSiteBatchAsync(
        IReadOnlyList<UnresolvedCallSite> callSites,
        CancellationToken cancellationToken = default);

    /// <summary>Drains unresolved call sites globally or for a specific file scope.</summary>
    Task<IReadOnlyList<UnresolvedCallSite>> DrainUnresolvedCallSitesAsync(
        Guid? sourceFileId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Applies symbol-to-cluster membership assignments keyed by symbol ID.</summary>
    Task ApplyClusterAssignmentsAsync(
        IReadOnlyDictionary<Guid, ValueTuple<Guid, string>> assignments,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces the cluster summary set in a single atomic operation.</summary>
    Task ReplaceClusterSummariesAtomicallyAsync(
        IReadOnlyList<Agency.GraphRAG.Code.Domain.Cluster> clusters,
        CancellationToken cancellationToken = default);

    /// <summary>Returns symbols grouped by their file's repo-relative path for the given set of paths.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<Symbol>>> GetSymbolsByPathsAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the file record whose Path matches, or null if not found.</summary>
    Task<SourceFile?> GetFileByPathAsync(
        string path,
        CancellationToken cancellationToken = default);
}
