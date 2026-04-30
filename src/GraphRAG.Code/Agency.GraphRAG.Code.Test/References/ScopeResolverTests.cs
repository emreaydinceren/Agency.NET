using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.References;
using Agency.GraphRAG.Code.Storage;

namespace Agency.GraphRAG.Code.Test.References;

/// <summary>
/// Tests for <see cref="ScopeResolver"/>.
/// </summary>
public sealed class ScopeResolverTests
{
    [Fact]
    public async Task ResolveAsync_ReturnsFileLocalAndImportReachableSymbols()
    {
        Guid sourceFileId = Guid.NewGuid();
        Symbol localA = CreateSymbol("Payments.Service.Submit");
        Symbol localB = CreateSymbol("Payments.Service.Validate");
        Symbol imported = CreateSymbol("Shared.Logging.ILogger");
        FakeGraphStore store = new()
        {
            Hops =
            [
                new TraversalHop { SymbolId = localA.Id, Depth = 0, ViaEdge = null },
                new TraversalHop
                {
                    SymbolId = imported.Id,
                    Depth = 1,
                    ViaEdge = new Edge
                    {
                        Id = Guid.NewGuid(),
                        SourceId = localA.Id,
                        SourceKind = "symbol",
                        TargetId = imported.Id,
                        TargetKind = "symbol",
                        EdgeKind = EdgeKind.Imports,
                        Confidence = 1.0d,
                        Signals = [],
                        Properties = new Dictionary<string, object?>(),
                    },
                },
            ],
        };
        ScopeResolver resolver = new(
            store,
            (fileId, _) => Task.FromResult<IReadOnlyList<Symbol>>(fileId == sourceFileId ? [localA, localB] : []));

        IReadOnlySet<Guid> reachable = await resolver.ResolveAsync(sourceFileId, TestContext.Current.CancellationToken);

        Assert.Equal(3, reachable.Count);
        Assert.Contains(localA.Id, reachable);
        Assert.Contains(localB.Id, reachable);
        Assert.Contains(imported.Id, reachable);
        TraversalRequest request = Assert.Single(store.Requests);
        Assert.Equal([localA.Id, localB.Id], request.SeedSymbolIds.OrderBy(static id => id).ToArray());
        Assert.Contains(EdgeKind.Imports, request.EdgeKinds);
    }

    private static Symbol CreateSymbol(string fullyQualifiedName) =>
        new()
        {
            Id = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            ModuleId = null,
            Name = fullyQualifiedName.Split('.').Last(),
            FullyQualifiedName = fullyQualifiedName,
            Kind = SymbolKind.Method,
            Signature = null,
            Summary = null,
            OneLineSummary = null,
            ContentHash = null,
            Embedding = null,
            IsUtility = false,
            SourceRangeStart = 1,
            SourceRangeEnd = 1,
        };

    private sealed class FakeGraphStore : IGraphStore
    {
        public List<TraversalRequest> Requests { get; } = [];

        public IReadOnlyList<TraversalHop> Hops { get; init; } = [];

        public Task InitializeSchemaAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertRepoAsync(Repo repo, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertProjectAsync(Project project, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertExternalPackageBatchAsync(IReadOnlyList<ExternalPackage> packages, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertFileAsync(SourceFile file, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertModuleAsync(Module module, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertSymbolAsync(Symbol symbol, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertSymbolBatchAsync(IReadOnlyList<Symbol> symbols, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertEdgeBatchAsync(IReadOnlyList<Edge> edges, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteSymbolsByFileAsync(Guid fileId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteFileAsync(Guid fileId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RenameFileAsync(Guid fileId, string newPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> LoadIndexedCommitAsync(Guid repoId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SetIndexedCommitAsync(Guid repoId, string indexedCommit, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<VectorSearchResult>> VectorSearchSymbolsAsync(string queryText, int topK, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<VectorSearchResult>>([]);
        public Task<IReadOnlyList<VectorSearchResult>> VectorSearchClustersAsync(string queryText, int topK, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<VectorSearchResult>>([]);
        public Task<Symbol?> GetSymbolByIdAsync(Guid symbolId, CancellationToken cancellationToken = default) => Task.FromResult<Symbol?>(null);
        public Task<IReadOnlyList<Symbol>> FindSymbolsByNameAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Symbol>>([]);
        public Task StageUnresolvedCallSiteBatchAsync(IReadOnlyList<UnresolvedCallSite> callSites, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<UnresolvedCallSite>> DrainUnresolvedCallSitesAsync(Guid? sourceFileId = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<UnresolvedCallSite>>([]);
        public Task ApplyClusterAssignmentsAsync(IReadOnlyDictionary<Guid, (Guid, string)> assignments, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReplaceClusterSummariesAtomicallyAsync(IReadOnlyList<Agency.GraphRAG.Code.Domain.Cluster> clusters, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<TraversalHop>> TraverseFromAsync(TraversalRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Hops);
        }
    }
}
