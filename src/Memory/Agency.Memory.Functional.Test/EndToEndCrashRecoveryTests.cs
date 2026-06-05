using Agency.Harness;
using Agency.Memory.Common.Events;
using Agency.Memory.Common.Jobs;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Agency.Memory.Distiller.Services;
using Agency.Memory.Sql.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using IEmbeddingGenerator = Agency.Embeddings.Common.IEmbeddingGenerator;
using MemoryRecord = Agency.Memory.Common.Records.Record;

namespace Agency.Memory.Functional.Test;

/// <summary>
/// End-to-end functional test: the Distiller recovers from a crash by reprocessing turns
/// exactly once using the watermark (Spec §12.2, §6.2 Constraints — idempotency).
/// </summary>
/// <remarks>
/// Uses stub LLM and embedder — no real LLM or embedding service required.
/// Requires a running PostgreSQL instance (see README.md).
/// Skip with: <c>dotnet test --filter "Category!=Functional"</c>.
/// </remarks>
[Trait("Category", "Functional")]
[Collection("memory-db")]
public sealed class EndToEndCrashRecoveryTests : IAsyncLifetime
{
    private static readonly IConfiguration _config = TestInfrastructure.BuildConfiguration();

    private NpgsqlDataSource _dataSource = default!;
    private const int Dim = 4;

    private const string FactJson = """
        {"records":[{"ContentType":"Fact","Title":"Python preference","Domain":"Preferences",
        "Key":"Language","Tags":["python"],"Scope":"Global","Importance":0.7,
        "Value":"User prefers Python."}]}
        """;

    /// <summary>Initialises Postgres and resets schema.</summary>
    public async ValueTask InitializeAsync()
    {
        string? skip = await TestInfrastructure.CheckPostgresAsync(_config, TestContext.Current.CancellationToken);
        if (skip is not null)
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

    // ── G.4 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies crash-recovery semantics: when the LLM throws on the first attempt,
    /// the watermark must not advance. A second run (with a working LLM) must succeed,
    /// advance the watermark, and produce exactly one record in the store.
    /// Re-processing after watermark is advanced must be a no-op (idempotent).
    /// </summary>
    [Fact]
    public async Task EndToEnd_DistillerCrash_RecoversFromWatermark()
    {
        string? skip = await TestInfrastructure.CheckPostgresAsync(_config, TestContext.Current.CancellationToken);
        if (skip is not null)
        {
            Assert.Skip(skip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        const string UserId = "user-crash";
        const string SessionId = "session-crash";
        const int UpToTurn = 2;

        IEmbeddingGenerator embedder = TestInfrastructure.DeterministicEmbedder(Dim);
        var memOpts = Options.Create(new MemoryOptions());
        var logger = NullLogger<PostgresMemoryStore>.Instance;
        var store = new PostgresMemoryStore(this._dataSource, embedder, memOpts, logger);
        var watermarkRepo = new WatermarkRepository(this._dataSource);
        var deadLetterRepo = new DeadLetterRepository(this._dataSource);
        var eventBus = BuildEventBus();

        var distillerOpts = Options.Create(new DistillerOptions
        {
            MaxRetries = 0, // no retries so the first throw immediately exhausts
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        });

        var channelRegistry = new ChannelSessionRegistry(
            distillerOpts, NullLogger<ChannelSessionRegistry>.Instance);

        var convoRegistry = new InMemoryConversationManagerRegistry();
        var convo = new InMemoryConversationManager();
        convo.Append(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.User, "I prefer Python."));
        convo.Append(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.Assistant, "Got it, I'll keep that in mind."));
        convoRegistry.Register(SessionId, convo);

        // ── Run 1: LLM throws on first call ─────────────────────────────────

        var throwOnceLlm = new ThrowOnceLlmAdapter(FactJson);
        var distillerService1 = BuildDistillerService(
            channelRegistry, convoRegistry, throwOnceLlm, embedder,
            store, watermarkRepo, deadLetterRepo, eventBus, distillerOpts);

        var job = new DistillationJob(UserId, SessionId, DistillationTrigger.Inactivity, UpToTurn);
        channelRegistry.GetOrCreateWriter(UserId, SessionId).TryWrite(job);

        // The MaxRetries=0 means the first (throwing) attempt goes straight to dead-letter.
        // Wait for the event (completed with 0 records, or failed event).
        DistillationCompletedEvent? completedEvt1 = null;
        DistillationFailedEvent? failedEvt1 = null;

        using var sub1a = eventBus.Subscribe<DistillationCompletedEvent>(async (e, _) =>
        {
            completedEvt1 = e;
            await Task.CompletedTask;
        });

        using var sub1b = eventBus.Subscribe<DistillationFailedEvent>(async (e, _) =>
        {
            failedEvt1 = e;
            await Task.CompletedTask;
        });

        using var cts1 = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts1.CancelAfter(TimeSpan.FromSeconds(15));
        await distillerService1.StartAsync(cts1.Token);

        // Poll until event received or timeout.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (completedEvt1 is null && failedEvt1 is null && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50, ct);
        }

        await distillerService1.StopAsync(CancellationToken.None);

        // Either a failed event or a completed-with-0-records event.
        bool gotEvent1 = completedEvt1 is not null || failedEvt1 is not null;
        Assert.True(gotEvent1, "Expected either DistillationCompletedEvent or DistillationFailedEvent after first run.");

        // ── Assert watermark NOT advanced after failure ───────────────────────

        int watermarkAfterRun1 = await watermarkRepo.GetAsync(UserId, SessionId, ct);
        Assert.Equal(0, watermarkAfterRun1);

        // ── Assert no records in store after failure ─────────────────────────

        IReadOnlyList<MemoryRecord> recordsAfterRun1 = await store.GetAllForUserAsync(UserId, ct);
        Assert.Empty(recordsAfterRun1);

        // ── Run 2: LLM works on this call (second call in throwOnceLlm succeeds) ──

        // Build a fresh service with the same registry (watermark is still 0).
        var distillerService2 = BuildDistillerService(
            channelRegistry, convoRegistry, throwOnceLlm, embedder,
            store, watermarkRepo, deadLetterRepo, eventBus, distillerOpts);

        DistillationCompletedEvent? completedEvt2 = null;
        using var sub2 = eventBus.Subscribe<DistillationCompletedEvent>(async (e, _) =>
        {
            completedEvt2 = e;
            await Task.CompletedTask;
        });

        // Enqueue the same job again (as if the process restarted and re-derived from watermark).
        channelRegistry.GetOrCreateWriter(UserId, SessionId).TryWrite(job);

        using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts2.CancelAfter(TimeSpan.FromSeconds(15));
        await distillerService2.StartAsync(cts2.Token);

        deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (completedEvt2 is null && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50, ct);
        }

        await distillerService2.StopAsync(CancellationToken.None);

        Assert.NotNull(completedEvt2);
        Assert.Equal(1, completedEvt2.RecordsWritten);

        // ── Assert watermark advanced ─────────────────────────────────────────

        int watermarkAfterRun2 = await watermarkRepo.GetAsync(UserId, SessionId, ct);
        Assert.Equal(UpToTurn, watermarkAfterRun2);

        // ── Assert exactly 1 record in store ─────────────────────────────────

        IReadOnlyList<MemoryRecord> recordsAfterRun2 = await store.GetAllForUserAsync(UserId, ct);
        Assert.Single(recordsAfterRun2);
        Assert.Equal("Python preference", recordsAfterRun2[0].Title);

        // ── Run 3: idempotency — same job again must be a no-op ───────────────

        var distillerService3 = BuildDistillerService(
            channelRegistry, convoRegistry, throwOnceLlm, embedder,
            store, watermarkRepo, deadLetterRepo, eventBus,
            distillerOpts);

        DistillationCompletedEvent? completedEvt3 = null;
        using var sub3 = eventBus.Subscribe<DistillationCompletedEvent>(async (e, _) =>
        {
            completedEvt3 = e;
            await Task.CompletedTask;
        });

        channelRegistry.GetOrCreateWriter(UserId, SessionId).TryWrite(job);

        using var cts3 = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts3.CancelAfter(TimeSpan.FromSeconds(10));
        await distillerService3.StartAsync(cts3.Token);

        deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (completedEvt3 is null && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50, ct);
        }

        await distillerService3.StopAsync(CancellationToken.None);

        Assert.NotNull(completedEvt3);
        Assert.Equal(0, completedEvt3.RecordsWritten); // no-op due to watermark

        IReadOnlyList<MemoryRecord> recordsAfterRun3 = await store.GetAllForUserAsync(UserId, ct);
        Assert.Single(recordsAfterRun3); // still exactly 1 record
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static IAsyncEventBus BuildEventBus()
    {
        // Use the internal InMemoryEventBus via reflection on the logger.
        // The InMemoryEventBus ctor is internal; accessible via InternalsVisibleTo granted to this project.
        return new Agency.Memory.Common.Events.InMemoryEventBus(
            NullLogger<Agency.Memory.Common.Events.InMemoryEventBus>.Instance);
    }

    private static DistillerBackgroundService BuildDistillerService(
        ChannelSessionRegistry channelRegistry,
        InMemoryConversationManagerRegistry convoRegistry,
        ILlmClientAdapter llm,
        IEmbeddingGenerator embedder,
        PostgresMemoryStore store,
        WatermarkRepository watermarkRepo,
        DeadLetterRepository deadLetterRepo,
        IAsyncEventBus eventBus,
        IOptions<DistillerOptions> opts) =>
        new DistillerBackgroundService(
            channelRegistry,
            convoRegistry,
            llm,
            embedder,
            store,
            watermarkRepo,
            deadLetterRepo,
            eventBus,
            opts,
            TimeProvider.System,
            NullLogger<DistillerBackgroundService>.Instance);
}
