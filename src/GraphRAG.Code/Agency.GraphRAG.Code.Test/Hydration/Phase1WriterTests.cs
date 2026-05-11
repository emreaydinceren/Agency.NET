using Agency.GraphRAG.Code.Chunker;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Hydration;
using Agency.GraphRAG.Code.Storage;
using Agency.GraphRAG.Code.Summarizer;
using Agency.GraphRAG.Code.Walker;

namespace Agency.GraphRAG.Code.Test.Hydration;

/// <summary>
/// Tests for <see cref="Phase1Writer"/>.
/// </summary>
public sealed class Phase1WriterTests
{
    [Fact]
    public async Task WriteAsync_WritesDefinitionsEdgesAndStagesCallSites_Idempotently()
    {
        RecordingGraphStore store = new();
        Phase1Writer writer = new(store, new StubEmbeddingGenerator());
        SourceFile file = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            RepoId = Guid.NewGuid(),
            Path = @"src\Payments\PaymentProcessor.cs",
            Language = "csharp",
            ContentHash = "file-hash",
        };
        Module module = new()
        {
            Id = Guid.NewGuid(),
            FileId = file.Id,
            Name = "Payments",
            Kind = "namespace",
        };
        Chunk typeChunk = ChunkBuilder.Build(
            path: file.Path,
            language: Language.CSharp,
            granularity: ChunkGranularity.Type,
            name: "PaymentProcessor",
            fullyQualifiedName: "Payments.PaymentProcessor",
            signature: "public sealed class PaymentProcessor",
            content: "public sealed class PaymentProcessor { }",
            range: new ChunkSourceRange(1, 0, 5, 0),
            symbolKind: SymbolKind.Class,
            importsInScope: [new ImportReference("System.Net.Http", [], false)]);
        Chunk methodChunk = ChunkBuilder.Build(
            path: file.Path,
            language: Language.CSharp,
            granularity: ChunkGranularity.Member,
            name: "ChargeAsync",
            fullyQualifiedName: "Payments.PaymentProcessor.ChargeAsync",
            signature: "public Task ChargeAsync(decimal amount)",
            content: "public Task ChargeAsync(decimal amount) => Task.CompletedTask;",
            range: new ChunkSourceRange(3, 4, 4, 0),
            symbolKind: SymbolKind.Method,
            importsInScope: [new ImportReference("System.Net.Http", [], false)],
            parentId: typeChunk.Id);
        UnresolvedCallSite callSite = new()
        {
            Id = Guid.NewGuid(),
            SourceSymbolId = HydrationIds.StableGuid(methodChunk.Id),
            SourceFileId = file.Id,
            Identifier = "SendAsync",
            Scope = "Payments.PaymentProcessor",
            LlmExtractedTarget = null,
        };
        Phase1WriteRequest request = new(
            file,
            module,
            [typeChunk, methodChunk],
            new Dictionary<string, SymbolSummary>(StringComparer.Ordinal)
            {
                [typeChunk.Id] = new("Processes payments.", "Handles payment coordination.", []),
                [methodChunk.Id] = new("Charges a payment.", "Executes the charge.", ["HttpClient.SendAsync"]),
            },
            [callSite]);

        await writer.WriteAsync(request, TestContext.Current.CancellationToken);
        await writer.WriteAsync(request, TestContext.Current.CancellationToken);

        Assert.Single(store.Files);
        Assert.Single(store.Modules);
        Assert.Equal(2, store.Symbols.Count);
        Assert.Equal(6, store.Edges.Count);
        Assert.Single(store.StagedCallSites);
        Assert.Contains(store.Edges.Values, edge => edge.EdgeKind == EdgeKind.Defines);
        Assert.Contains(store.Edges.Values, edge => edge.EdgeKind == EdgeKind.Imports);
        Assert.Contains(store.Edges.Values, edge => edge.EdgeKind == EdgeKind.Contains && edge.SourceKind == "module");
        Assert.Contains(store.Edges.Values, edge => edge.EdgeKind == EdgeKind.Contains && edge.SourceKind == "symbol");
    }

    private sealed class StubEmbeddingGenerator : Agency.Embeddings.Common.IEmbeddingGenerator
    {
        public Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string input, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReadOnlyMemory<float>(new float[1]));

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IEnumerable<string> inputs, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(inputs.Select(static _ => new ReadOnlyMemory<float>(new float[1])).ToArray());
    }

    private sealed class RecordingGraphStore : IGraphStore
    {
        public Dictionary<Guid, SourceFile> Files { get; } = [];
        public Dictionary<Guid, Module> Modules { get; } = [];
        public Dictionary<Guid, Symbol> Symbols { get; } = [];
        public Dictionary<(Guid, Guid, EdgeKind), Edge> Edges { get; } = [];
        public Dictionary<Guid, UnresolvedCallSite> StagedCallSites { get; } = [];

        public Task InitializeSchemaAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertRepoAsync(Repo repo, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertProjectAsync(Project project, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertExternalPackageBatchAsync(IReadOnlyList<ExternalPackage> packages, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertFileAsync(SourceFile file, CancellationToken cancellationToken = default) { Files[file.Id] = file; return Task.CompletedTask; }
        public Task UpsertModuleAsync(Module module, CancellationToken cancellationToken = default) { Modules[module.Id] = module; return Task.CompletedTask; }
        public Task UpsertSymbolAsync(Symbol symbol, CancellationToken cancellationToken = default) { Symbols[symbol.Id] = symbol; return Task.CompletedTask; }
        public Task UpsertSymbolBatchAsync(IReadOnlyList<Symbol> symbols, CancellationToken cancellationToken = default) { foreach (Symbol symbol in symbols) { Symbols[symbol.Id] = symbol; } return Task.CompletedTask; }
        public Task UpsertEdgeBatchAsync(IReadOnlyList<Edge> edges, CancellationToken cancellationToken = default) { foreach (Edge edge in edges) { Edges[(edge.SourceId, edge.TargetId, edge.EdgeKind)] = edge; } return Task.CompletedTask; }
        public Task DeleteSymbolsByFileAsync(Guid fileId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteFileAsync(Guid fileId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RenameFileAsync(Guid fileId, string newPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> LoadIndexedCommitAsync(Guid repoId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SetIndexedCommitAsync(Guid repoId, string indexedCommit, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<VectorSearchResult>> VectorSearchSymbolsAsync(string queryText, int topK, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<VectorSearchResult>>([]);
        public Task<IReadOnlyList<VectorSearchResult>> VectorSearchClustersAsync(string queryText, int topK, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<VectorSearchResult>>([]);
        public Task<IReadOnlyList<TraversalHop>> TraverseFromAsync(TraversalRequest request, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TraversalHop>>([]);
        public Task<Symbol?> GetSymbolByIdAsync(Guid symbolId, CancellationToken cancellationToken = default) => Task.FromResult(Symbols.TryGetValue(symbolId, out Symbol? symbol) ? symbol : null);
        public Task<IReadOnlyList<Symbol>> FindSymbolsByNameAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Symbol>>([]);
        public Task StageUnresolvedCallSiteBatchAsync(IReadOnlyList<UnresolvedCallSite> callSites, CancellationToken cancellationToken = default) { foreach (UnresolvedCallSite callSite in callSites) { StagedCallSites[callSite.Id] = callSite; } return Task.CompletedTask; }
        public Task<IReadOnlyList<UnresolvedCallSite>> DrainUnresolvedCallSitesAsync(Guid? sourceFileId = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<UnresolvedCallSite>>([]);
        public Task ApplyClusterAssignmentsAsync(IReadOnlyDictionary<Guid, (Guid, string)> assignments, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReplaceClusterSummariesAtomicallyAsync(IReadOnlyList<Agency.GraphRAG.Code.Domain.Cluster> clusters, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyDictionary<string, IReadOnlyList<Symbol>>> GetSymbolsByPathsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<Symbol>>>(new Dictionary<string, IReadOnlyList<Symbol>>());
        public Task<SourceFile?> GetFileByPathAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult<SourceFile?>(null);
    }
}
