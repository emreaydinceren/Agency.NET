using System.Security.Cryptography;
using System.Text;
using Agency.GraphRAG.Code.Chunker;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Summarizer;
using Agency.GraphRAG.Code.Walker;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Agency.GraphRAG.Code.Test.Summarizer;

/// <summary>
/// Tests for <see cref="SymbolSummarizer"/>.
/// </summary>
public sealed class SymbolSummarizerTests
{
    private static readonly SummarizerOptions DefaultOptions = new()
    {
        StrongModel = "gpt-strong",
        StandardModel = "gpt-standard",
        CheapModel = "gpt-cheap",
        CheapestModel = "gpt-cheapest",
    };

    [Fact]
    public async Task SummarizeAsync_OrdersParentsFirst_AndUsesParentContextAndExpectedModels()
    {
        FakeChatClient chatClient = new();
        chatClient.EnqueueTextResponse("Defines the payment processing contract.");
        chatClient.EnqueueTextResponse(
            """
            Describes the payment processing contract for implementations.
            Probable callees:
            - AuthorizeAsync
            - CaptureAsync
            """);
        chatClient.EnqueueTextResponse("Provides shared payment workflow behavior.");
        chatClient.EnqueueTextResponse(
            """
            Centralizes retries and telemetry for payment processors.
            Probable callees: RetryAsync, RecordTelemetry
            """);
        chatClient.EnqueueTextResponse("Handles Stripe payment processing.");
        chatClient.EnqueueTextResponse(
            """
            Implements Stripe-specific authorization and capture behavior.
            Probable callees:
            - StripeSdk.AuthorizeAsync
            - StripeSdk.CaptureAsync
            """);

        RecordingEmbeddingGenerator embeddingGenerator = new();
        SymbolSummarizer summarizer = CreateSummarizer(chatClient, embeddingGenerator);
        Chunk contract = CreateTypeChunk(
            path: @"src\Contracts\IPaymentProcessor.cs",
            line: 0,
            name: "IPaymentProcessor",
            fullyQualifiedName: "Payments.Contracts.IPaymentProcessor",
            symbolKind: SymbolKind.Interface,
            content: "public interface IPaymentProcessor { Task ChargeAsync(decimal amount); }");
        Chunk baseClass = CreateTypeChunk(
            path: @"src\Contracts\PaymentProcessorBase.cs",
            line: 0,
            name: "PaymentProcessorBase",
            fullyQualifiedName: "Payments.Contracts.PaymentProcessorBase",
            signature: "public abstract class PaymentProcessorBase : IPaymentProcessor",
            content: "public abstract class PaymentProcessorBase : IPaymentProcessor { protected Task RetryAsync() => Task.CompletedTask; }",
            implements: ["IPaymentProcessor"]);
        Chunk implementation = CreateTypeChunk(
            path: @"src\Stripe\StripePaymentProcessor.cs",
            line: 0,
            name: "StripePaymentProcessor",
            fullyQualifiedName: "Payments.Stripe.StripePaymentProcessor",
            signature: "public sealed class StripePaymentProcessor : PaymentProcessorBase, IPaymentProcessor",
            content: "public sealed class StripePaymentProcessor : PaymentProcessorBase, IPaymentProcessor { public Task ChargeAsync(decimal amount) => Task.CompletedTask; }",
            inherits: ["PaymentProcessorBase"],
            implements: ["IPaymentProcessor"]);

        SummarizationResult result = await summarizer.SummarizeAsync(
            [implementation, baseClass, contract],
            TestContext.Current.CancellationToken);
        IReadOnlyDictionary<string, SymbolSummary> summaries = result.Summaries;

        Assert.Equal(6, chatClient.GetResponseCallCount);
        Assert.Equal(
            ["gpt-cheapest", "gpt-strong", "gpt-cheapest", "gpt-strong", "gpt-cheapest", "gpt-cheap"],
            chatClient.ReceivedModelIds);

        string implementationDetailedPrompt = chatClient.ReceivedPrompts[5].ReplaceLineEndings("\n");
        Assert.Contains("Parent context:", implementationDetailedPrompt, StringComparison.Ordinal);
        Assert.Contains("Describes the payment processing contract for implementations.", implementationDetailedPrompt, StringComparison.Ordinal);
        Assert.Contains("Centralizes retries and telemetry for payment processors.", implementationDetailedPrompt, StringComparison.Ordinal);

        Assert.Equal(
            ["Defines the payment processing contract.", "Provides shared payment workflow behavior.", "Handles Stripe payment processing."],
            embeddingGenerator.RequestedInputs);

        SymbolSummary implementationSummary = summaries[implementation.Id];
        Assert.Equal("Handles Stripe payment processing.", implementationSummary.OneLine);
        Assert.Equal("Implements Stripe-specific authorization and capture behavior.", implementationSummary.Detailed);
        Assert.Equal(["StripeSdk.AuthorizeAsync", "StripeSdk.CaptureAsync"], implementationSummary.ProbableCallees);
        Assert.Equal(RecordingEmbeddingGenerator.CreateEmbedding("Handles Stripe payment processing.").ToArray(), implementationSummary.OneLineEmbedding.ToArray());
    }

    [Fact]
    public async Task SummarizeAsync_CacheHit_AvoidsLlmCalls()
    {
        FakeChatClient chatClient = new();
        RecordingEmbeddingGenerator embeddingGenerator = new();
        SummaryCache cache = new(":memory:");
        Chunk chunk = CreateTypeChunk(
            path: @"src\Services\Worker.cs",
            line: 0,
            name: "Worker",
            fullyQualifiedName: "Example.Services.Worker",
            symbolKind: SymbolKind.Method,
            content: "private void Work() { Save(); }");
        cache.Set(
            ComputeContentHash(chunk.Content),
            ModelTierSelector.ModelTier.Cheap.ToString(),
            new SummaryCacheEntry("Executes the worker action.", "Performs the work operation.", ["Save"]));
        SymbolSummarizer summarizer = CreateSummarizer(chatClient, embeddingGenerator, cache);

        SummarizationResult result = await summarizer.SummarizeAsync([chunk], TestContext.Current.CancellationToken);
        IReadOnlyDictionary<string, SymbolSummary> summaries = result.Summaries;

        Assert.Equal(0, chatClient.GetResponseCallCount);
        Assert.Equal(["Executes the worker action."], embeddingGenerator.RequestedInputs);
        Assert.Equal("Performs the work operation.", summaries[chunk.Id].Detailed);
        Assert.Equal(["Save"], summaries[chunk.Id].ProbableCallees);
    }

    [Fact]
    public async Task SummarizeAsync_ExtractsProbableCalleesFromInlineAndBulletFormats()
    {
        FakeChatClient chatClient = new();
        chatClient.EnqueueTextResponse("Coordinates order submission.");
        chatClient.EnqueueTextResponse(
            """
            Coordinates order submission and logging.
            Probable callees:
            - Repository.SaveAsync
            - Logger.LogInformation
            - Repository.SaveAsync
            """);
        RecordingEmbeddingGenerator embeddingGenerator = new();
        SymbolSummarizer summarizer = CreateSummarizer(chatClient, embeddingGenerator);
        Chunk chunk = CreateTypeChunk(
            path: @"src\Orders\OrderService.cs",
            line: 0,
            name: "Submit",
            fullyQualifiedName: "Orders.OrderService.Submit",
            symbolKind: SymbolKind.Method,
            content: "public Task Submit() { return Task.CompletedTask; }");

        SummarizationResult result = await summarizer.SummarizeAsync([chunk], TestContext.Current.CancellationToken);

        Assert.Equal("Coordinates order submission and logging.", result.Summaries[chunk.Id].Detailed);
        Assert.Equal(["Repository.SaveAsync", "Logger.LogInformation"], result.Summaries[chunk.Id].ProbableCallees);
    }

    private static SymbolSummarizer CreateSummarizer(
        FakeChatClient chatClient,
        RecordingEmbeddingGenerator embeddingGenerator,
        SummaryCache? cache = null) =>
        new(
            chatClient,
            embeddingGenerator,
            cache ?? new SummaryCache(":memory:"),
            new ModelTierSelector(Options.Create(DefaultOptions)),
            new SummarizationPromptBuilder(),
            Options.Create(DefaultOptions));

    private static Chunk CreateTypeChunk(
        string path,
        int line,
        string name,
        string fullyQualifiedName,
        SymbolKind symbolKind = SymbolKind.Class,
        string? signature = null,
        string? content = null,
        IReadOnlyList<string>? inherits = null,
        IReadOnlyList<string>? implements = null)
    {
        return ChunkBuilder.Build(
            path: path,
            language: Language.CSharp,
            granularity: ChunkGranularity.Type,
            name: name,
            fullyQualifiedName: fullyQualifiedName,
            signature: signature,
            content: content ?? $"type {name}",
            range: new ChunkSourceRange(line, 0, line + 1, 0),
            symbolKind: symbolKind,
            importsInScope: [],
            inherits: inherits,
            implements: implements);
    }

    private static string ComputeContentHash(string content)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(hash);
    }

    private sealed class RecordingEmbeddingGenerator : Agency.Embeddings.Common.IEmbeddingGenerator
    {
        public List<string> RequestedInputs { get; } = [];

        public static ReadOnlyMemory<float> CreateEmbedding(string input) =>
            new([input.Length, input.Count(static c => c == ' ')]);

        public Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string input, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedInputs.Add(input);
            return Task.FromResult(CreateEmbedding(input));
        }

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IEnumerable<string> inputs, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new();

        public int GetResponseCallCount { get; private set; }

        public List<string> ReceivedPrompts { get; } = [];

        public List<string?> ReceivedModelIds { get; } = [];

        public ChatClientMetadata Metadata { get; } = new("FakeChatClient", null, null);

        public void EnqueueTextResponse(string text)
        {
            _responses.Enqueue(new ChatResponse([new ChatMessage(ChatRole.Assistant, text)])
            {
                FinishReason = ChatFinishReason.Stop,
            });
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetResponseCallCount++;

            IReadOnlyList<ChatMessage> snapshot = messages.ToList();
            string prompt = string.Concat(
                snapshot.SelectMany(static message => message.Contents.OfType<TextContent>())
                    .Select(static content => content.Text));
            ReceivedPrompts.Add(prompt);
            ReceivedModelIds.Add(options?.ModelId);

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException($"FakeChatClient has no response for call #{GetResponseCallCount}.");
            }

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
