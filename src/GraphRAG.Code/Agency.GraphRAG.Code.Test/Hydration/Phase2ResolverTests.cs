using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Hydration;
using Agency.GraphRAG.Code.References;
using Agency.GraphRAG.Code.Storage;

namespace Agency.GraphRAG.Code.Test.Hydration;

/// <summary>
/// Tests for <see cref="Phase2Resolver"/>.
/// </summary>
public sealed class Phase2ResolverTests
{
    [Fact]
    public async Task ResolveAsync_AppliesScopeFilteringAndUpsertsIdempotentReferenceEdges()
    {
        Guid sourceFileId = Guid.NewGuid();
        Symbol sourceSymbol = CreateSymbol("Payments.Service.Submit");
        Symbol reachableTarget = CreateSymbol("Shared.Logging.ILogger.Log");
        Symbol outOfScopeTarget = CreateSymbol("Other.Logging.ILogger.Log");
        FakePhase2GraphStore store = new(sourceSymbol, reachableTarget, outOfScopeTarget, sourceFileId);
        ScopeResolver scopeResolver = new(
            store,
            (fileId, _) => Task.FromResult<IReadOnlyList<Symbol>>(fileId == sourceFileId ? [sourceSymbol] : []));
        ReferenceScorer scorer = new(new ExternalPackageHeuristic());
        Phase2Resolver resolver = new(store, scopeResolver, scorer);

        await resolver.ResolveAsync(sourceFileId, [CreatePackage("Shared.Logging")], TestContext.Current.CancellationToken);
        await resolver.ResolveAsync(sourceFileId, [CreatePackage("Shared.Logging")], TestContext.Current.CancellationToken);

        Edge edge = Assert.Single(store.ReferenceEdges.Values);
        Assert.Equal(sourceSymbol.Id, edge.SourceId);
        Assert.Equal(reachableTarget.Id, edge.TargetId);
        Assert.Equal(EdgeKind.References, edge.EdgeKind);
        Assert.Equal([Signal.NameMatch, Signal.LlmExtraction], edge.Signals);
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

    private static ExternalPackage CreatePackage(string name) =>
        new()
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Name = name,
            Version = "1.0.0",
            Ecosystem = "nuget",
            Scope = "runtime",
        };

    private sealed class FakePhase2GraphStore : IGraphStore
    {
        private readonly Symbol sourceSymbol;
        private readonly Symbol reachableTarget;
        private readonly Symbol outOfScopeTarget;
        private readonly Guid sourceFileId;

        public FakePhase2GraphStore(Symbol sourceSymbol, Symbol reachableTarget, Symbol outOfScopeTarget, Guid sourceFileId)
        {
            this.sourceSymbol = sourceSymbol;
            this.reachableTarget = reachableTarget;
            this.outOfScopeTarget = outOfScopeTarget;
            this.sourceFileId = sourceFileId;
        }

        public Dictionary<(Guid, Guid, EdgeKind), Edge> ReferenceEdges { get; } = [];

        public Task InitializeSchemaAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertRepoAsync(Repo repo, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertProjectAsync(Project project, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertExternalPackageBatchAsync(IReadOnlyList<ExternalPackage> packages, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertFileAsync(SourceFile file, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertModuleAsync(Module module, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertSymbolAsync(Symbol symbol, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertSymbolBatchAsync(IReadOnlyList<Symbol> symbols, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteSymbolsByFileAsync(Guid fileId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteFileAsync(Guid fileId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RenameFileAsync(Guid fileId, string newPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> LoadIndexedCommitAsync(Guid repoId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SetIndexedCommitAsync(Guid repoId, string indexedCommit, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<VectorSearchResult>> VectorSearchSymbolsAsync(string queryText, int topK, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<VectorSearchResult>>([]);
        public Task<IReadOnlyList<VectorSearchResult>> VectorSearchClustersAsync(string queryText, int topK, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<VectorSearchResult>>([]);
        public Task<Symbol?> GetSymbolByIdAsync(Guid symbolId, CancellationToken cancellationToken = default) => Task.FromResult<Symbol?>(null);
        public Task StageUnresolvedCallSiteBatchAsync(IReadOnlyList<UnresolvedCallSite> callSites, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ApplyClusterAssignmentsAsync(IReadOnlyDictionary<Guid, (Guid, string)> assignments, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReplaceClusterSummariesAtomicallyAsync(IReadOnlyList<Agency.GraphRAG.Code.Domain.Cluster> clusters, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyDictionary<string, IReadOnlyList<Symbol>>> GetSymbolsByPathsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<Symbol>>>(new Dictionary<string, IReadOnlyList<Symbol>>());
        public Task<SourceFile?> GetFileByPathAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult<SourceFile?>(null);

        public Task<IReadOnlyList<TraversalHop>> TraverseFromAsync(TraversalRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TraversalHop>>
            ([
                new TraversalHop { SymbolId = sourceSymbol.Id, Depth = 0, ViaEdge = null },
                new TraversalHop { SymbolId = reachableTarget.Id, Depth = 1, ViaEdge = null },
            ]);

        public Task<IReadOnlyList<Symbol>> FindSymbolsByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Symbol>>([reachableTarget, outOfScopeTarget]);

        public Task<IReadOnlyList<UnresolvedCallSite>> DrainUnresolvedCallSitesAsync(Guid? sourceFileId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UnresolvedCallSite>>
            (sourceFileId == this.sourceFileId
                ? [new UnresolvedCallSite
                {
                    Id = Guid.NewGuid(),
                    SourceSymbolId = sourceSymbol.Id,
                    SourceFileId = this.sourceFileId,
                    Identifier = "Log",
                    Scope = "Payments.Service",
                    LlmExtractedTarget = reachableTarget.FullyQualifiedName,
                }]
                : []);

        public Task UpsertEdgeBatchAsync(IReadOnlyList<Edge> edges, CancellationToken cancellationToken = default)
        {
            foreach (Edge edge in edges)
            {
                ReferenceEdges[(edge.SourceId, edge.TargetId, edge.EdgeKind)] = edge;
            }

            return Task.CompletedTask;
        }
    }
}
