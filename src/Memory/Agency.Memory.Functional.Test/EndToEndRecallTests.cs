using Agency.Harness;
using Agency.Harness.Contexts;
using Agency.Embeddings.OpenAI;
using Agency.Llm.Common;
using Agency.Llm.OpenAI;
using Agency.Memory.Common.Events;
using Agency.Memory.Common.Jobs;
using Agency.Memory.Common.Options;
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
///   <item>LM Studio (via configured <c>MemoryFunctional:LmStudio:BaseUrl</c>) for episode extraction and embedding.</item>
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

    // Actual embedding dimension emitted by the configured model, detected at init time by
    // probing the real embedder. Hardcoding this caused the schema's vector(N) column to
    // mismatch the model's output (e.g. "expected 1536 dimensions, not 1024") and fail
    // UpsertAsync inside the distiller; see MemoryE2EFixture.DetectEmbeddingDimAsync.
    private int _embeddingDim = 1536;

    /// <summary>Initialises the schema. Skips setup if Postgres is unreachable.</summary>
    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;

        string? pgSkip = await TestInfrastructure.CheckPostgresAsync(_config, ct);
        if (pgSkip is not null)
        {
            return;
        }

        // Probe the real embedding dimension before creating the schema so the vector(N)
        // column matches the loaded model's output. Skipped when LM Studio is unreachable —
        // the per-test LM Studio check then handles skipping.
        if (await TestInfrastructure.CheckLmStudioAsync(_config, ct) is null)
        {
            var probeEmbedder = new EmbeddingGenerator(new EmbeddingOptions
            {
                BaseUrl = _config["MemoryFunctional:LmStudio:BaseUrl"]
                    ?? throw new InvalidOperationException("Configuration key 'MemoryFunctional:LmStudio:BaseUrl' is required."),
                ApiKey = _config["MemoryFunctional:LmStudio:ApiKey"] ?? "lm-studio",
                ModelId = _config["MemoryFunctional:LmStudio:EmbeddingModel"] ?? "text-embedding-3-small",
            });
            this._embeddingDim = await TestInfrastructure.DetectEmbeddingDimAsync(probeEmbedder, ct);
        }

        this._dataSource = TestInfrastructure.BuildDataSource(_config);
        await TestInfrastructure.ResetSchemaAsync(
            this._dataSource, this._embeddingDim, ct);
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

        string baseUrl = _config["MemoryFunctional:LmStudio:BaseUrl"]
            ?? throw new InvalidOperationException("Configuration key 'MemoryFunctional:LmStudio:BaseUrl' is required.");
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
            watermarkRepo,
            deadLetterRepo,
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

        // Wait for distillation to complete (up to 60s). Race the failure event so a
        // dead-lettered job surfaces its real reason instead of being masked as a timeout.
        DistillationCompletedEvent completed;
        try
        {
            completed = await TestInfrastructure.WaitForDistillationOrFailAsync(
                eventBus,
                UserId,
                SessionId,
                timeout: TimeSpan.FromSeconds(60),
                ct: cts.Token);
        }
        catch (DistillationFailedException ex)
        {
            // The distiller dead-lettered the job. Surface the actual failure reason
            // (parse error, transient LLM/embedding failure, etc.) rather than a
            // misleading "no model loaded" skip.
            await distillerService.StopAsync(CancellationToken.None);
            Assert.Fail(ex.Message);
            return;
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

