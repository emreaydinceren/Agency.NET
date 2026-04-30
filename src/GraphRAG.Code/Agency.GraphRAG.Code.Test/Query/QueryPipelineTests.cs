using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Query;
using Agency.GraphRAG.Code.Storage;
using Microsoft.Extensions.AI;
using ClusterRecord = Agency.GraphRAG.Code.Domain.Cluster;

namespace Agency.GraphRAG.Code.Test.Query;

/// <summary>
/// Tests for <see cref="QueryPipeline"/>.
/// </summary>
public sealed class QueryPipelineTests
{
    [Fact]
    public async Task ExecuteAsync_WiresPlannerRetrieverAssemblerAndLlm_WithExpectedPromptStructure()
    {
        FakeChatClient chatClient = new();
        chatClient.EnqueueTextResponse("Local");
        chatClient.EnqueueTextResponse("The Submit method coordinates validation and persistence.");

        Symbol symbol = new()
        {
            Id = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            ModuleId = null,
            Name = "Submit",
            FullyQualifiedName = "Orders.OrderService.Submit",
            Kind = SymbolKind.Method,
            Signature = "Task Submit()",
            Summary = "Coordinates order submission.",
            OneLineSummary = "Submits the order.",
            ContentHash = null,
            Embedding = [1],
            IsUtility = false,
            SourceRangeStart = 10,
            SourceRangeEnd = 20,
        };
        HybridRetriever retriever = new(
            new SingleSymbolGraphStore(symbol),
            new EmptyClusterSource(),
            new StaticSymbolTextProvider("public Task Submit() => repository.SaveAsync();"));
        QueryPipeline pipeline = new(
            new QueryPlanner(new QueryClassifier(chatClient, new QueryOptions { CheapestModel = "gpt-cheapest" })),
            retriever,
            new ContextAssembler(),
            chatClient,
            new QueryOptions
            {
                CheapestModel = "gpt-cheapest",
                AnswerModel = "gpt-answer",
                ContextTokenBudget = 200,
            });

        QueryResponse response = await pipeline.ExecuteAsync("What does Submit do?", TestContext.Current.CancellationToken);

        Assert.Equal("The Submit method coordinates validation and persistence.", response.Answer);
        Assert.Equal(QueryCategory.Local, response.Plan.Category);
        Assert.Equal(["gpt-cheapest", "gpt-answer"], chatClient.ReceivedModelIds);
        Assert.Contains("fuzzy index", chatClient.ReceivedInstructions[1], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("low confidence", chatClient.ReceivedInstructions[1], StringComparison.OrdinalIgnoreCase);
        string finalPrompt = chatClient.ReceivedPrompts[1].ReplaceLineEndings("\n");
        Assert.Contains("Question:\nWhat does Submit do?", finalPrompt, StringComparison.Ordinal);
        Assert.Contains("Context:\nRelevant symbols:", finalPrompt, StringComparison.Ordinal);
        Assert.Contains("Raw code:", finalPrompt, StringComparison.Ordinal);
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new();

        public List<string> ReceivedPrompts { get; } = [];

        public List<string?> ReceivedModelIds { get; } = [];

        public List<string?> ReceivedInstructions { get; } = [];

        public ChatClientMetadata Metadata { get; } = new("FakeChatClient", null, null);

        public void EnqueueTextResponse(string text) =>
            _responses.Enqueue(new ChatResponse([new ChatMessage(ChatRole.Assistant, text)]));

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReceivedPrompts.Add(string.Concat(messages.SelectMany(static message => message.Contents.OfType<TextContent>()).Select(static content => content.Text)));
            ReceivedModelIds.Add(options?.ModelId);
            ReceivedInstructions.Add(options?.Instructions);
            return Task.FromResult(_responses.Dequeue());
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? key = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class SingleSymbolGraphStore(Symbol symbol) : IGraphStore
    {
        public Task<IReadOnlyList<VectorSearchResult>> VectorSearchSymbolsAsync(string queryText, int topK, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VectorSearchResult>>([new VectorSearchResult { Id = symbol.Id, Score = 0.9 }]);

        public Task<IReadOnlyList<TraversalHop>> TraverseFromAsync(TraversalRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TraversalHop>>(
            [
                new TraversalHop
                {
                    SymbolId = symbol.Id,
                    Depth = 0,
                    ViaEdge = null,
                },
                new TraversalHop
                {
                    SymbolId = symbol.Id,
                    Depth = 1,
                    ViaEdge = new Edge
                    {
                        Id = Guid.NewGuid(),
                        SourceId = symbol.Id,
                        SourceKind = "symbol",
                        TargetId = symbol.Id,
                        TargetKind = "symbol",
                        EdgeKind = EdgeKind.References,
                        Confidence = 0.4,
                        Signals = [Signal.Unresolved],
                        Properties = new Dictionary<string, object?>(),
                    },
                },
            ]);

        public Task<Symbol?> GetSymbolByIdAsync(Guid symbolId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Symbol?>(symbolId == symbol.Id ? symbol : null);

        public Task<IReadOnlyList<Symbol>> FindSymbolsByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Symbol>>([symbol]);

        public Task<IReadOnlyList<VectorSearchResult>> VectorSearchClustersAsync(string queryText, int topK, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<VectorSearchResult>>([]);

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
    }

    private sealed class EmptyClusterSource : IClusterQuerySource
    {
        public Task<IReadOnlyList<ClusterRecord>> GetClustersAsync(IReadOnlyList<Guid> clusterIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ClusterRecord>>([]);
    }

    private sealed class StaticSymbolTextProvider(string text) : ISymbolTextProvider
    {
        public Task<string?> LoadAsync(Symbol symbol, CancellationToken cancellationToken = default) => Task.FromResult<string?>(text);
    }
}
