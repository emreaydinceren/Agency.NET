using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Query;
using Agency.GraphRAG.Code.Storage;
using ClusterRecord = Agency.GraphRAG.Code.Domain.Cluster;

namespace Agency.GraphRAG.Code.Test.Query;

/// <summary>
/// Tests for <see cref="HybridRetriever"/>.
/// </summary>
public sealed class HybridRetrieverTests
{
    [Fact]
    public async Task RetrieveAsync_CombinesVectorSearch_Traversal_AndClusterSummaries()
    {
        Symbol entry = CreateSymbol("CheckoutService", 10);
        Symbol collaborator = CreateSymbol("PaymentGateway", 20);
        ClusterRecord businessCluster = new()
        {
            Id = Guid.NewGuid(),
            Label = "Checkout flow",
            Type = ClusterType.Business,
            CoherenceScore = 0.9,
            Summary = "Owns cart checkout orchestration.",
            Embedding = [1, 2],
        };
        ClusterRecord infrastructureCluster = new()
        {
            Id = Guid.NewGuid(),
            Label = "Telemetry",
            Type = ClusterType.Infrastructure,
            CoherenceScore = 0.8,
            Summary = "Provides logging and metrics.",
            Embedding = [3, 4],
        };
        FakeGraphStore graphStore = new(entry, collaborator, businessCluster.Id, infrastructureCluster.Id);
        FakeClusterQuerySource clusterSource = new([businessCluster, infrastructureCluster]);
        FakeSymbolTextProvider symbolTextProvider = new(new Dictionary<Guid, string>
        {
            [entry.Id] = "public sealed class CheckoutService { }",
            [collaborator.Id] = "public sealed class PaymentGateway { }",
        });
        HybridRetriever retriever = new(graphStore, clusterSource, symbolTextProvider);
        QueryPlan plan = new()
        {
            QueryText = "How does checkout work?",
            Category = QueryCategory.Subsystem,
            UseSymbolVectorSearch = true,
            UseClusterVectorSearch = true,
            UseTraversal = true,
            TraversalDirection = TraversalDirection.Outgoing,
            TraversalEdgeKinds = [EdgeKind.References],
            TraversalMaxHops = 2,
            PreferredClusterTypes = [ClusterType.Business],
            AggregateInfrastructureClusters = true,
        };

        QueryRetrievalResult result = await retriever.RetrieveAsync(plan, TestContext.Current.CancellationToken);

        Assert.Equal(["How does checkout work?"], graphStore.VectorSymbolQueries);
        Assert.Equal(["How does checkout work?"], graphStore.VectorClusterQueries);
        Assert.Equal(entry.Id, Assert.Single(graphStore.TraversalRequests).SeedSymbolIds.Single());
        Assert.Equal([businessCluster.Id], result.Clusters.Select(static cluster => cluster.Cluster.Id));
        Assert.Equal([infrastructureCluster.Id], result.InfrastructureClusters.Select(static cluster => cluster.Cluster.Id));
        Assert.Equal([entry.Id, collaborator.Id], result.Symbols.Select(static symbol => symbol.Symbol.Id));
        Assert.True(result.HasLowConfidenceReferences);
    }

    private static Symbol CreateSymbol(string name, int line) =>
        new()
        {
            Id = Guid.NewGuid(),
            FileId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ModuleId = null,
            Name = name,
            FullyQualifiedName = $"Example.{name}",
            Kind = SymbolKind.Class,
            Signature = $"class {name}",
            Summary = $"{name} summary",
            OneLineSummary = $"{name} one line",
            ContentHash = null,
            Embedding = [1, 2],
            IsUtility = false,
            SourceRangeStart = line,
            SourceRangeEnd = line + 5,
        };

    private sealed class FakeGraphStore(Symbol entry, Symbol collaborator, Guid businessClusterId, Guid infrastructureClusterId) : IGraphStore
    {
        public List<string> VectorSymbolQueries { get; } = [];
        public List<string> VectorClusterQueries { get; } = [];
        public List<TraversalRequest> TraversalRequests { get; } = [];

        public Task<IReadOnlyList<VectorSearchResult>> VectorSearchSymbolsAsync(string queryText, int topK, CancellationToken cancellationToken = default)
        {
            VectorSymbolQueries.Add(queryText);
            return Task.FromResult<IReadOnlyList<VectorSearchResult>>(
            [
                new VectorSearchResult { Id = entry.Id, Score = 0.95 },
            ]);
        }

        public Task<IReadOnlyList<VectorSearchResult>> VectorSearchClustersAsync(string queryText, int topK, CancellationToken cancellationToken = default)
        {
            VectorClusterQueries.Add(queryText);
            return Task.FromResult<IReadOnlyList<VectorSearchResult>>(
            [
                new VectorSearchResult { Id = businessClusterId, Score = 0.91 },
                new VectorSearchResult { Id = infrastructureClusterId, Score = 0.60 },
            ]);
        }

        public Task<IReadOnlyList<TraversalHop>> TraverseFromAsync(TraversalRequest request, CancellationToken cancellationToken = default)
        {
            TraversalRequests.Add(request);
            return Task.FromResult<IReadOnlyList<TraversalHop>>(
            [
                new TraversalHop
                {
                    SymbolId = entry.Id,
                    Depth = 0,
                    ViaEdge = null,
                },
                new TraversalHop
                {
                    SymbolId = collaborator.Id,
                    Depth = 1,
                    ViaEdge = new Edge
                    {
                        Id = Guid.NewGuid(),
                        SourceId = entry.Id,
                        SourceKind = "symbol",
                        TargetId = collaborator.Id,
                        TargetKind = "symbol",
                        EdgeKind = EdgeKind.References,
                        Confidence = 0.5,
                        Signals = [Signal.Unresolved],
                        Properties = new Dictionary<string, object?>(),
                    },
                },
            ]);
        }

        public Task<Symbol?> GetSymbolByIdAsync(Guid symbolId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Symbol?>(symbolId == entry.Id ? entry : symbolId == collaborator.Id ? collaborator : null);

        public Task<IReadOnlyList<Symbol>> FindSymbolsByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Symbol>>([]);

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
        public Task StageUnresolvedCallSiteBatchAsync(IReadOnlyList<UnresolvedCallSite> callSites, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<UnresolvedCallSite>> DrainUnresolvedCallSitesAsync(Guid? sourceFileId = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<UnresolvedCallSite>>([]);
        public Task ApplyClusterAssignmentsAsync(IReadOnlyDictionary<Guid, (Guid, string)> assignments, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReplaceClusterSummariesAtomicallyAsync(IReadOnlyList<ClusterRecord> clusters, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyDictionary<string, IReadOnlyList<Symbol>>> GetSymbolsByPathsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<Symbol>>>(new Dictionary<string, IReadOnlyList<Symbol>>());
        public Task<SourceFile?> GetFileByPathAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult<SourceFile?>(null);
    }

    private sealed class FakeClusterQuerySource(IReadOnlyList<ClusterRecord> clusters) : IClusterQuerySource
    {
        public Task<IReadOnlyList<ClusterRecord>> GetClustersAsync(IReadOnlyList<Guid> clusterIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ClusterRecord>>(clusters.Where(cluster => clusterIds.Contains(cluster.Id)).ToArray());
    }

    private sealed class FakeSymbolTextProvider(IReadOnlyDictionary<Guid, string> textById) : ISymbolTextProvider
    {
        public Task<string?> LoadAsync(Symbol symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult(textById.TryGetValue(symbol.Id, out string? value) ? value : null);
    }
}
