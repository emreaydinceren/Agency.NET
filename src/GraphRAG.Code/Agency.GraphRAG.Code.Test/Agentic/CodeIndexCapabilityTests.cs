using Agency.GraphRAG.Code.Agentic;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Query;
using Agency.GraphRAG.Code.Storage;
using Microsoft.Extensions.AI;
using ClusterRecord = Agency.GraphRAG.Code.Domain.Cluster;

namespace Agency.GraphRAG.Code.Test.Agentic;

/// <summary>
/// Verifies the <see cref="ICodeIndex"/> capability contract.
/// </summary>
public sealed class CodeIndexCapabilityTests
{
    [Fact]
    public async Task AskAsync_ForwardsQuestionToQueryPipeline()
    {
        RecordingGraphStore graphStore = new();
        FakeChatClient chatClient = new();
        chatClient.EnqueueTextResponse("Local");
        chatClient.EnqueueTextResponse("Agent coordinates chat-agent execution.");
        QueryPipeline pipeline = new(
            new QueryPlanner(new QueryClassifier(chatClient, new QueryOptions { CheapestModel = "cheap" })),
            new HybridRetriever(graphStore, new EmptyClusterSource(), new StaticSymbolTextProvider()),
            new ContextAssembler(),
            chatClient,
            new QueryOptions
            {
                CheapestModel = "cheap",
                AnswerModel = "answer",
                ContextTokenBudget = 200,
            });
        ICodeIndex capability = new CodeIndexCapability(pipeline);

        string answer = await capability.AskAsync("find Agent", 3, TestContext.Current.CancellationToken);

        Assert.Equal("find Agent", graphStore.LastQueryText);
        Assert.Equal(5, graphStore.LastTopK);
        Assert.Equal("Agent coordinates chat-agent execution.", answer);
    }

    private sealed class RecordingGraphStore : StubGraphStore
    {
        public string? LastQueryText { get; private set; }

        public int LastTopK { get; private set; }

        public override Task<IReadOnlyList<VectorSearchResult>> VectorSearchSymbolsAsync(string queryText, int topK, CancellationToken cancellationToken = default)
        {
            LastQueryText = queryText;
            LastTopK = topK;
            return Task.FromResult<IReadOnlyList<VectorSearchResult>>(
            [
                new VectorSearchResult
                {
                    Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    Score = 0.9d,
                },
            ]);
        }

        public override Task<Symbol?> GetSymbolByIdAsync(Guid symbolId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Symbol?>(new Symbol
            {
                Id = symbolId,
                FileId = Guid.NewGuid(),
                ModuleId = null,
                Name = "Agent",
                FullyQualifiedName = "Agency.Agentic.Agent",
                Kind = SymbolKind.Class,
                Signature = "public sealed class Agent",
                Summary = "Coordinates chat-agent execution.",
                OneLineSummary = "Coordinates chat-agent execution.",
                ContentHash = null,
                Embedding = null,
                IsUtility = false,
                SourceRangeStart = 1,
                SourceRangeEnd = 10,
            });

        public override Task<IReadOnlyList<TraversalHop>> TraverseFromAsync(TraversalRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TraversalHop>>([]);
    }

    private sealed class EmptyClusterSource : IClusterQuerySource
    {
        public Task<IReadOnlyList<ClusterRecord>> GetClustersAsync(IReadOnlyList<Guid> clusterIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ClusterRecord>>([]);
    }

    private sealed class StaticSymbolTextProvider : ISymbolTextProvider
    {
        public Task<string?> LoadAsync(Symbol symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("public sealed class Agent {}");
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new();

        public ChatClientMetadata Metadata { get; } = new("FakeChatClient", null, null);

        public void EnqueueTextResponse(string text) =>
            _responses.Enqueue(new ChatResponse([new ChatMessage(ChatRole.Assistant, text)]));

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(_responses.Dequeue());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? key = null) => null;

        public void Dispose()
        {
        }
    }
}
