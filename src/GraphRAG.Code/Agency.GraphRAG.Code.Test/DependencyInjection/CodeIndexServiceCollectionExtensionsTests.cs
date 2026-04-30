using Agency.Embeddings.Common;
using Agency.GraphRAG.Code.DependencyInjection;
using Agency.GraphRAG.Code.Pipeline;
using Agency.GraphRAG.Code.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using AgencyEmbeddingGenerator = Agency.Embeddings.Common.IEmbeddingGenerator;

namespace Agency.GraphRAG.Code.Test.DependencyInjection;

/// <summary>
/// Smoke-tests the GraphRAG code-index DI registrations.
/// </summary>
public sealed class CodeIndexServiceCollectionExtensionsTests
{
    [Fact]
    public void AddCodeIndex_ResolvesGraphStoreAndIndexingPipeline_ForSqlite()
    {
        ServiceCollection services = new();
        services.AddSingleton<AgencyEmbeddingGenerator>(new TestEmbeddingGenerator());
        services.AddSingleton<IChatClient>(new TestChatClient());
        services.AddCodeIndex(options =>
        {
            options.Store = CodeIndexStore.Sqlite;
            options.SqlitePath = @"E:\Repos\Agency\src\GraphRAG.Code\Agency.GraphRAG.Code.Test\code-index-smoke.db";
            options.WorkingDirectory = @"E:\Repos\Agency\src\GraphRAG.Code\Agency.GraphRAG.Code.Test";
        });

        using ServiceProvider serviceProvider = services.BuildServiceProvider();

        Assert.IsAssignableFrom<IGraphStore>(serviceProvider.GetRequiredService<IGraphStore>());
        Assert.IsType<IndexingPipeline>(serviceProvider.GetRequiredService<IndexingPipeline>());
    }

    private sealed class TestEmbeddingGenerator : AgencyEmbeddingGenerator
    {
        public Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string input, CancellationToken cancellationToken = default) =>
            Task.FromResult(ReadOnlyMemory<float>.Empty);

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IEnumerable<string> inputs, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>([]);
    }

    private sealed class TestChatClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("TestChatClient", null, null);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? key = null) => null;

        public void Dispose()
        {
        }
    }
}
