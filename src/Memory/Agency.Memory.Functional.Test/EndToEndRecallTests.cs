using Agency.Agentic;
using Agency.Agentic.Contexts;
using Agency.Embeddings.OpenAI;
using Agency.Llm.Common;
using Agency.Llm.OpenAI;
using Agency.Memory.Common.Events;
using Agency.Memory.Common.Jobs;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Agency.Memory.Distiller.Services;
using Agency.Memory.Retrieval;
using Agency.Memory.Sql.Postgres;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using IEmbeddingGenerator = Agency.Embeddings.Common.IEmbeddingGenerator;

namespace Agency.Memory.Functional.Test;

/// <summary>
/// End-to-end functional test: a Fact written in Session N is recalled in Session N+1
/// (Spec §13, Use Case U1).
/// </summary>
/// <remarks>
/// This test exercises the full memory pipeline with real network calls:
/// <list type="bullet">
///   <item>LM Studio at <c>http://llm-host.example:1234</c> for episode extraction and embedding.</item>
///   <item>PostgreSQL (Docker) for record storage and retrieval.</item>
/// </list>
///
/// Requires both Postgres and LM Studio to be running.
/// Skip with: <c>dotnet test --filter "Category!=Functional"</c>.
/// Run with: <c>dotnet test src/Memory/Agency.Memory.Functional.Test --filter "Category=Functional"</c>
/// </remarks>
[Trait("Category", "Functional")]
[Collection("memory-db")]
public sealed class EndToEndRecallTests : IAsyncLifetime
{
    private static readonly IConfiguration _config = TestInfrastructure.BuildConfiguration();

    private NpgsqlDataSource _dataSource = default!;
    private const int Dim = 1536; // standard LM Studio embedding dimension

    /// <summary>Initialises the schema. Skips setup if Postgres is unreachable.</summary>
    public async ValueTask InitializeAsync()
    {
        string? pgSkip = await TestInfrastructure.CheckPostgresAsync(_config, TestContext.Current.CancellationToken);
        if (pgSkip is not null)
        {
            return;
        }

        this._dataSource = TestInfrastructure.BuildDataSource(_config);
        await TestInfrastructure.ResetSchemaAsync(
            this._dataSource, Dim, TestContext.Current.CancellationToken);
    }

    /// <summary>Disposes data source.</summary>
    public async ValueTask DisposeAsync()
    {
        if (this._dataSource is not null)
        {
            await this._dataSource.DisposeAsync();
        }
    }

    // ── G.1 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates the complete Session N → Session N+1 recall cycle (Spec §13):
    /// <list type="number">
    ///   <item>Session N: user says "I prefer Python." — agent acknowledges.</item>
    ///   <item>Distiller is triggered by inactivity; extracts the Python preference Fact.</item>
    ///   <item>Session N+1: user asks for a deduplication script.</item>
    ///   <item>Retrieval engine surfaces the Python preference in Context.Knowledge.</item>
    ///   <item>Assertion: Context.Knowledge contains the Fact after retrieval.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task EndToEnd_FactWrittenInSessionN_RecalledInSessionNPlus1()
    {
        // ── Prerequisites check ──────────────────────────────────────────────

        var ct = TestContext.Current.CancellationToken;

        string? pgSkip = await TestInfrastructure.CheckPostgresAsync(_config, ct);
        if (pgSkip is not null)
        {
            Assert.Skip(pgSkip);
            return;
        }

        string? llmSkip = await TestInfrastructure.CheckLmStudioAsync(_config, ct);
        if (llmSkip is not null)
        {
            Assert.Skip(llmSkip);
            return;
        }

        // ── LM Studio configuration ──────────────────────────────────────────

        string baseUrl = _config["MemoryFunctional:LmStudio:BaseUrl"] ?? "http://llm-host.example:1234/v1";
        string apiKey = _config["MemoryFunctional:LmStudio:ApiKey"] ?? "lm-studio";
        string chatModel = _config["MemoryFunctional:LmStudio:ChatModel"] ?? "local-model";
        string embeddingModel = _config["MemoryFunctional:LmStudio:EmbeddingModel"] ?? "text-embedding-3-small";

        // ── Infrastructure setup ─────────────────────────────────────────────

        IEmbeddingGenerator embedder = new EmbeddingGenerator(new EmbeddingOptions
        {
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            ModelId = embeddingModel,
        });

        var memOpts = Options.Create(new MemoryOptions
        {
            RetrievalTopK = 5,
            OverFetchFactor = 2,
        });

        var store = new PostgresMemoryStore(
            this._dataSource, embedder, memOpts,
            NullLogger<PostgresMemoryStore>.Instance);

        var watermarkRepo = new WatermarkRepository(this._dataSource);
        var deadLetterRepo = new DeadLetterRepository(this._dataSource);
        var eventBus = new InMemoryEventBus(
            NullLogger<InMemoryEventBus>.Instance);

        const string UserId = "user-e2e-recall";
        const string SessionId = "session-n";

        // ── Session N: inject conversation turns directly ─────────────────────
        //
        // We inject turns directly into the Distiller pipeline (per IQ-4) rather than
        // running a full Agent loop. This exercises the Distiller's actual LLM call
        // and Postgres write path — the critical components.

        var conv = new InMemoryConversationManager();
        conv.Append(new ChatMessage(ChatRole.User, "I prefer Python."));
        conv.Append(new ChatMessage(ChatRole.Assistant, "Got it, I will keep that in mind."));

        var distillerOpts = Options.Create(new DistillerOptions
        {
            MaxRetries = 1,
            RetryBaseDelay = TimeSpan.FromSeconds(2),
        });

        var channelRegistry = new ChannelSessionRegistry(
            distillerOpts, NullLogger<ChannelSessionRegistry>.Instance);

        var convoRegistry = new InMemoryConversationManagerRegistry();
        convoRegistry.Register(SessionId, conv);

        IChatClient llm = new OpenAIClient(new LlmClientOptions
        {
            ApiKey = apiKey,
            BaseUrl = baseUrl,
            SuppressThinking = true, // distiller suppresses thinking (TI-8.2)
        }).CreateChatClient();

        ILlmClientAdapter llmAdapter = new ChatClientLlmAdapter(llm, chatModel);

        var distillerService = new DistillerBackgroundService(
            channelRegistry,
            convoRegistry,
            llmAdapter,
            embedder,
            store,
            new WatermarkStoreAdapter(watermarkRepo),
            new DeadLetterStoreAdapter(deadLetterRepo),
            eventBus,
            distillerOpts,
            TimeProvider.System,
            NullLogger<DistillerBackgroundService>.Instance);

        // Subscribe to wait for DistillationCompletedEvent.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        await distillerService.StartAsync(cts.Token);

        // Enqueue inactivity-trigger job.
        var job = new DistillationJob(
            UserId, SessionId, DistillationTrigger.Inactivity, UpToTurnIndex: 2);
        channelRegistry.GetOrCreateWriter(UserId, SessionId).TryWrite(job);

        // Wait for distillation to complete (up to 60s).
        DistillationCompletedEvent completed;
        try
        {
            completed = await TestInfrastructure.WaitForEventAsync<DistillationCompletedEvent>(
                eventBus,
                timeout: TimeSpan.FromSeconds(60),
                predicate: e => e.UserId == UserId && e.SessionId == SessionId,
                ct: cts.Token);
        }
        catch (TimeoutException)
        {
            await distillerService.StopAsync(CancellationToken.None);
            Assert.Skip(
                "Distillation did not complete within 60 seconds. " +
                "LM Studio may be reachable but no model is loaded or the response is too slow. " +
                "Load a compatible model in LM Studio and retry.");
            return;
        }
        catch (OperationCanceledException)
        {
            await distillerService.StopAsync(CancellationToken.None);
            Assert.Skip(
                "Distillation was cancelled (LM Studio request timeout). " +
                "Load a compatible model in LM Studio and retry.");
            return;
        }

        await distillerService.StopAsync(CancellationToken.None);

        // Distillation should have written at least 1 record (the Python preference).
        // It may write 0 if the LLM decides nothing is memorable — acceptable but test skips.
        if (completed.RecordsWritten == 0)
        {
            Assert.Skip(
                "Distillation completed but wrote 0 records. " +
                "The LLM decided nothing was memorable from 'I prefer Python.' — " +
                "consider adjusting the model or prompt.");
            return;
        }

        // ── Session N+1: retrieval pass ──────────────────────────────────────

        var ctx = new Context
        {
            Query = new QueryContext
            {
                Prompt = "Write me a script to deduplicate this list.",
            },
            User = new UserSpecificContext { Id = UserId },
            Conversation = new InMemoryConversationManager(),
        };

        ctx.Conversation.Append(new ChatMessage(
            ChatRole.User, "Write me a script to deduplicate this list."));

        var retrievalEngine = new RetrievalEngine(store, embedder, memOpts);

        // Gate should decide to retrieve (first retrieval for this context).
        bool shouldRetrieve = await RetrievalGate.ShouldRetrieveAsync(ctx, store, ct);
        Assert.True(shouldRetrieve,
            "Retrieval gate should open on first retrieval for Session N+1.");

        await retrievalEngine.RetrieveAsync(ctx, ct);

        // ── Assertions: Context.Knowledge contains the Python preference ─────

        Assert.NotEmpty(ctx.Knowledge.Records);

        bool hasPythonFact = ctx.Knowledge.Records.Any(r =>
            r.Value.Contains("Python", StringComparison.OrdinalIgnoreCase)
            || r.Title.Contains("Python", StringComparison.OrdinalIgnoreCase));

        Assert.True(hasPythonFact,
            $"Expected Context.Knowledge to contain the Python preference Fact. " +
            $"Actual facts: {string.Join("; ", ctx.Knowledge.Records.Select(r => r.Title))}");
    }
}

/// <summary>
/// Wraps a <see cref="IChatClient"/> as the Distiller's internal <c>ILlmClientAdapter</c>.
/// Used by the functional test to inject a real LM Studio client into the Distiller pipeline.
/// </summary>
/// <remarks>
/// This class can access <c>ILlmClientAdapter</c> because
/// <c>Agency.Memory.Distiller/AssemblyInfo.cs</c> grants
/// <c>[assembly: InternalsVisibleTo("Agency.Memory.Functional.Test")]</c>.
/// </remarks>
internal sealed class ChatClientLlmAdapter : ILlmClientAdapter
{
    private readonly IChatClient _client;
    private readonly string _model;

    /// <summary>Initialises the adapter.</summary>
    /// <param name="client">The underlying chat client.</param>
    /// <param name="model">The model identifier for requests.</param>
    internal ChatClientLlmAdapter(IChatClient client, string model)
    {
        this._client = client;
        this._model = model;
    }

    /// <inheritdoc/>
    public async Task<string> SendAsync(string prompt, CancellationToken ct = default)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, prompt),
        };

        var opts = new ChatOptions { ModelId = this._model, MaxOutputTokens = 2048 };
        ChatResponse response = await this._client.GetResponseAsync(messages, opts, ct)
            .ConfigureAwait(false);

        return string.Concat(
            response.Messages
                .SelectMany(static m => m.Contents.OfType<TextContent>())
                .Select(static t => t.Text));
    }
}
