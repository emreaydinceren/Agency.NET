using Agency.GraphRAG.Code.Cluster;
using Agency.GraphRAG.Code.Domain;
using Microsoft.Extensions.AI;

namespace Agency.GraphRAG.Code.Test.Cluster;

/// <summary>
/// Tests for <see cref="ClusterSummarizer"/>.
/// </summary>
public sealed class ClusterSummarizerTests
{
    [Fact]
    public async Task SummarizeAsync_PrimaryAndUtilityClusters_UseDifferentPromptsAndPersistClassification()
    {
        FakeChatClient chatClient = new();
        chatClient.EnqueueTextResponse(
            """
            Summary: Handles payment authorization and capture.
            Type: business
            Coherence: 4
            """);
        chatClient.EnqueueTextResponse(
            """
            Summary: Provides logging and retry helpers.
            Type: mixed
            Coherence: 5
            """);
        RecordingEmbeddingGenerator embeddingGenerator = new();
        ClusterSummarizer summarizer = new(chatClient, embeddingGenerator);
        IReadOnlyList<ClusterSummaryRequest> requests =
        [
            new(Guid.NewGuid(), "Payments.Auth", ClusterMembershipKind.Primary, [Symbol("Payments.Auth.Authorize"), Symbol("Payments.Auth.Capture")]),
            new(Guid.NewGuid(), "Infrastructure (project-a)", ClusterMembershipKind.Utility, [Symbol("Payments.Shared.Logger"), Symbol("Payments.Shared.Retry")]),
        ];

        IReadOnlyList<Agency.GraphRAG.Code.Domain.Cluster> clusters = await summarizer.SummarizeAsync(requests, TestContext.Current.CancellationToken);

        Assert.Contains("decision procedure", chatClient.ReceivedPrompts[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cross-cutting code", chatClient.ReceivedPrompts[1], StringComparison.OrdinalIgnoreCase);

        Assert.Equal(ClusterType.Business, clusters[0].Type);
        Assert.Equal(0.8d, clusters[0].CoherenceScore, precision: 6);
        Assert.Equal(ClusterType.Infrastructure, clusters[1].Type);
        Assert.Equal(1d, clusters[1].CoherenceScore, precision: 6);
        Assert.Equal(
            ["Handles payment authorization and capture.", "Provides logging and retry helpers."],
            embeddingGenerator.RequestedInputs);
    }

    private static Symbol Symbol(string fullyQualifiedName) =>
        new()
        {
            Id = Guid.NewGuid(),
            FileId = Guid.NewGuid(),
            Name = fullyQualifiedName.Split('.').Last(),
            FullyQualifiedName = fullyQualifiedName,
            Kind = SymbolKind.Class,
            IsUtility = fullyQualifiedName.Contains(".Shared.", StringComparison.Ordinal),
            SourceRangeStart = 1,
            SourceRangeEnd = 2,
        };

    private sealed class RecordingEmbeddingGenerator : Agency.Embeddings.Common.IEmbeddingGenerator
    {
        public List<string> RequestedInputs { get; } = [];

        public Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string input, CancellationToken cancellationToken = default)
        {
            RequestedInputs.Add(input);
            return Task.FromResult<ReadOnlyMemory<float>>(new float[] { input.Length });
        }

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IEnumerable<string> inputs, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new();

        public List<string> ReceivedPrompts { get; } = [];

        public ChatClientMetadata Metadata { get; } = new("FakeClusterChatClient", null, null);

        public void EnqueueTextResponse(string text) =>
            _responses.Enqueue(new ChatResponse([new ChatMessage(ChatRole.Assistant, text)]));

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            string prompt = string.Concat(messages.SelectMany(static message => message.Contents.OfType<TextContent>()).Select(static content => content.Text));
            ReceivedPrompts.Add(prompt);
            return Task.FromResult(_responses.Dequeue());
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose()
        {
        }
    }
}
