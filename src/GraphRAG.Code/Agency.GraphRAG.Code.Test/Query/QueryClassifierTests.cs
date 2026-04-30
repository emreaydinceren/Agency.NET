using Agency.GraphRAG.Code.Query;
using Microsoft.Extensions.AI;

namespace Agency.GraphRAG.Code.Test.Query;

/// <summary>
/// Tests for <see cref="QueryClassifier"/>.
/// </summary>
public sealed class QueryClassifierTests
{
    [Fact]
    public async Task ClassifyAsync_SendsExpectedPrompt_AndParsesCategory()
    {
        FakeChatClient chatClient = new();
        chatClient.EnqueueTextResponse("Global");
        QueryClassifier classifier = new(chatClient, new QueryOptions { CheapestModel = "gpt-cheapest" });

        QueryCategory category = await classifier.ClassifyAsync("What does this codebase do?", TestContext.Current.CancellationToken);

        Assert.Equal(QueryCategory.Global, category);
        Assert.Equal("gpt-cheapest", Assert.Single(chatClient.ReceivedModelIds));
        string prompt = Assert.Single(chatClient.ReceivedPrompts).ReplaceLineEndings("\n");
        Assert.Contains("- Local", prompt, StringComparison.Ordinal);
        Assert.Contains("- Subsystem", prompt, StringComparison.Ordinal);
        Assert.Contains("- Global", prompt, StringComparison.Ordinal);
        Assert.Contains("- Impact", prompt, StringComparison.Ordinal);
        Assert.Contains("- Dependency", prompt, StringComparison.Ordinal);
        Assert.Contains("What does this codebase do?", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClassifyAsync_InvalidCategory_Throws()
    {
        FakeChatClient chatClient = new();
        chatClient.EnqueueTextResponse("Unknown");
        QueryClassifier classifier = new(chatClient, new QueryOptions());

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => classifier.ClassifyAsync("What does auth do?", TestContext.Current.CancellationToken));

        Assert.Contains("Unknown", exception.Message, StringComparison.Ordinal);
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new();

        public List<string> ReceivedPrompts { get; } = [];

        public List<string?> ReceivedModelIds { get; } = [];

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
}
