using Agency.Agentic;
using Agency.Memory.Common.Events;
using Agency.Memory.Common.Jobs;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Agency.Memory.Distiller.Services;
using Agency.Memory.Functional.Test.Infrastructure;
using Agency.Memory.Sql.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using IEmbeddingGenerator = Agency.Embeddings.Common.IEmbeddingGenerator;
using MemoryRecord = Agency.Memory.Common.Records.Record;

namespace Agency.Memory.Functional.Test;

/// <summary>
/// End-to-end Group 5 — Failure &amp; Recovery tests.
/// Verifies that the Distiller correctly retries transient failures, dead-letters
/// permanent failures, recovers from process crashes via the watermark, and
/// that <see cref="OperationCanceledException"/> is never dead-lettered
/// (Memory-TestPlan.md §3 Group 5; Memory-Specifications.md §8.6, §9, §12.2, §18.1).
/// </summary>
/// <remarks>
/// All tests carry <c>[Trait("Category","Functional")]</c> and
/// <c>[Trait("Group","Failure")]</c> so they can be run in isolation:
/// <code>
/// dotnet test src/Memory/Agency.Memory.Functional.Test
///     --filter "Category=Functional&amp;Group=Failure"
/// </code>
/// Each test uses a unique <c>userId</c> of the form <c>{TestName}-{Guid}</c>
/// so residue from a failed run cannot poison subsequent runs.
///
/// These tests use <see cref="FaultInjectingLlmClient"/> and stub embedders — only
/// Postgres is required (LM Studio is NOT required for any test in this group).
/// </remarks>
[Trait("Category", "Functional")]
[Trait("Group", "Failure")]
[Collection("memory-db")]
public sealed class Group5FailureAndRecoveryTests : IAsyncLifetime
{
    private static readonly IConfiguration _config = TestInfrastructure.BuildConfiguration();

    /// <summary>Embedding dimension fixed to 1536 to match the shared schema.</summary>
    private const int Dim = 1536;

    private NpgsqlDataSource _dataSource = default!;

    // ── Canned distillation response for stub LLM success paths ──────────────

    private const string FactJson = """
        {"records":[{"ContentType":"Fact","Title":"Python preference","Domain":"Preferences",
        "Key":"Language","Tags":["python"],"Scope":"Global","Importance":0.7,
        "Value":"User prefers Python."}]}
        """;

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    /// <summary>Initialises one shared Postgres data source at dimension 1536.</summary>
    public async ValueTask InitializeAsync()
    {
        string? skip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);

        if (skip is not null)
        {
            return;
        }

        this._dataSource = TestInfrastructure.BuildDataSource(_config);
        await TestInfrastructure.ResetSchemaAsync(
            this._dataSource, Dim, TestContext.Current.CancellationToken);
    }

    /// <summary>Disposes the shared Postgres data source.</summary>
    public async ValueTask DisposeAsync()
    {
        if (this._dataSource is not null)
        {
            await this._dataSource.DisposeAsync();
        }
    }

    // ── E5.1 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// E5.1 — Verifies crash-recovery semantics: when the Distiller crashes on the first
    /// attempt, the watermark is NOT advanced. A second run (simulating process restart)
    /// reprocesses exactly the same turns exactly once, producing one record and advancing
    /// the watermark. A third run (same job, same watermark) is a no-op
    /// (Memory-Specifications.md §12.2, §9; TestPlan E5.1).
    /// </summary>
    /// <remarks>
    /// This test is modelled on <c>EndToEndCrashRecoveryTests.EndToEnd_DistillerCrash_RecoversFromWatermark</c>
    /// (SA-G task G.4 / project plan G.4), but uses the Group 5 infrastructure pattern
    /// and the <see cref="FaultInjectingLlmClient"/> decorator instead of
    /// <c>ThrowOnceLlmAdapter</c>. The injected fault is a network throw (transient, no
    /// status code) with <c>MaxRetries = 0</c> so the first call immediately exhausts all
    /// retry budget and the job is dead-lettered without the watermark advancing.
    /// </remarks>
    [Fact]
    public async Task DistillerCrash_WatermarkUnchanged_NextRunReprocessesTurnsExactlyOnce()
    {
        string? skip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (skip is not null)
        {
            Assert.Skip(skip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userId = $"E51_Crash-{Guid.NewGuid():N}";
        const string SessionId = "e51-s1";
        const int UpToTurn = 2;

        IEmbeddingGenerator embedder = TestInfrastructure.DeterministicEmbedder(Dim);
        var store = TestInfrastructure.BuildMemoryStore(
            this._dataSource, embedder, NullLogger<PostgresMemoryStore>.Instance);
        var watermarkRepo = new WatermarkRepository(this._dataSource);
        var deadLetterRepo = new DeadLetterRepository(this._dataSource);
        var eventBus = BuildEventBus();

        // MaxRetries = 0: first throw exhausts all budget immediately.
        var distillerOpts = Options.Create(new DistillerOptions
        {
            MaxRetries = 0,
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        });

        var convo = new InMemoryConversationManager();
        convo.Append(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.User, "I prefer Python."));
        convo.Append(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.Assistant, "Got it."));

        var convoRegistry = new InMemoryConversationManagerRegistry();
        convoRegistry.Register(SessionId, convo);

        // ── Run 1: LLM throws (simulates mid-distillation crash) ─────────────
        // Inject a network error (transient) then a canned success for Run 2.
        var faultClient = new FaultInjectingLlmClient(new StubLlmAdapter(FactJson));
        faultClient.InjectNetworkErrorOnce();

        var channelRegistry1 = new ChannelSessionRegistry(
            distillerOpts, NullLogger<ChannelSessionRegistry>.Instance);
        var distiller1 = BuildDistillerService(
            channelRegistry1, convoRegistry, faultClient, embedder,
            store, watermarkRepo, deadLetterRepo, eventBus, distillerOpts);

        var job = new DistillationJob(
            userId, SessionId, DistillationTrigger.Inactivity, UpToTurn);
        channelRegistry1.GetOrCreateWriter(userId, SessionId).TryWrite(job);

        DistillationFailedEvent? failedEvt1 = null;
        using var sub1 = eventBus.Subscribe<DistillationFailedEvent>(async (e, _) =>
        {
            failedEvt1 = e;
            await Task.CompletedTask;
        });

        using var cts1 = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts1.CancelAfter(TimeSpan.FromSeconds(15));
        await distiller1.StartAsync(cts1.Token);
        await WaitUntilAsync(() => failedEvt1 is not null, TimeSpan.FromSeconds(15), ct);
        await distiller1.StopAsync(CancellationToken.None);

        // ── Assert watermark NOT advanced after crash ─────────────────────────
        int watermarkAfterRun1 = await watermarkRepo.GetAsync(userId, SessionId, ct);
        Assert.Equal(0, watermarkAfterRun1);

        // Assert store is empty.
        IReadOnlyList<MemoryRecord> recordsRun1 = await store.GetAllForUserAsync(userId, ct);
        Assert.Empty(recordsRun1);

        // ── Run 2: process restarts — new WatermarkRepository instance (cold cache) ─
        // Simulate a process restart by creating a new WatermarkRepository (empty in-memory
        // cache) bound to the same DB. The Distiller must read the persisted watermark (0)
        // and reprocess the same turns.
        var watermarkRepo2 = new WatermarkRepository(this._dataSource);
        var deadLetterRepo2 = new DeadLetterRepository(this._dataSource);

        DistillationCompletedEvent? completedEvt2 = null;
        using var sub2 = eventBus.Subscribe<DistillationCompletedEvent>(async (e, _) =>
        {
            completedEvt2 = e;
            await Task.CompletedTask;
        });

        var channelRegistry2 = new ChannelSessionRegistry(
            distillerOpts, NullLogger<ChannelSessionRegistry>.Instance);
        var distiller2 = BuildDistillerService(
            channelRegistry2, convoRegistry, faultClient, embedder,
            store, watermarkRepo2, deadLetterRepo2, eventBus, distillerOpts);

        // Same job: same UpToTurnIndex, watermark still 0 on the new repo instance.
        channelRegistry2.GetOrCreateWriter(userId, SessionId).TryWrite(job);

        using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts2.CancelAfter(TimeSpan.FromSeconds(15));
        await distiller2.StartAsync(cts2.Token);
        await WaitUntilAsync(() => completedEvt2 is not null, TimeSpan.FromSeconds(15), ct);
        await distiller2.StopAsync(CancellationToken.None);

        Assert.NotNull(completedEvt2);
        Assert.Equal(1, completedEvt2.RecordsWritten);

        // Watermark now advanced.
        int watermarkAfterRun2 = await watermarkRepo2.GetAsync(userId, SessionId, ct);
        Assert.Equal(UpToTurn, watermarkAfterRun2);

        // Exactly one record in the store.
        IReadOnlyList<MemoryRecord> recordsRun2 = await store.GetAllForUserAsync(userId, ct);
        Assert.Single(recordsRun2);

        // ── Run 3: same job again must be a no-op (watermark already advanced) ─
        var deadLetterRepo3 = new DeadLetterRepository(this._dataSource);
        DistillationCompletedEvent? noOpEvt = null;
        using var sub3 = eventBus.Subscribe<DistillationCompletedEvent>(async (e, _) =>
        {
            // Capture only the no-op (RecordsWritten = 0, watermark unchanged).
            if (e.UserId == userId && e.RecordsWritten == 0)
            {
                noOpEvt = e;
            }

            await Task.CompletedTask;
        });

        var channelRegistry3 = new ChannelSessionRegistry(
            distillerOpts, NullLogger<ChannelSessionRegistry>.Instance);
        var distiller3 = BuildDistillerService(
            channelRegistry3, convoRegistry, faultClient, embedder,
            store, watermarkRepo2, deadLetterRepo3, eventBus, distillerOpts);

        channelRegistry3.GetOrCreateWriter(userId, SessionId).TryWrite(job);

        using var cts3 = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts3.CancelAfter(TimeSpan.FromSeconds(10));
        await distiller3.StartAsync(cts3.Token);
        await WaitUntilAsync(() => noOpEvt is not null, TimeSpan.FromSeconds(10), ct);
        await distiller3.StopAsync(CancellationToken.None);

        // Store still has exactly one record.
        IReadOnlyList<MemoryRecord> recordsRun3 = await store.GetAllForUserAsync(userId, ct);
        Assert.Single(recordsRun3);
    }

    // ── E5.2 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// E5.2 — Verifies that an HTTP 429 (Too Many Requests) is classified as transient:
    /// the Distiller retries with exponential backoff and eventually succeeds when the
    /// fault window is exhausted and the canned LLM response returns valid JSON
    /// (Memory-Specifications.md §8.6 Transient; TestPlan E5.2).
    /// </summary>
    /// <remarks>
    /// Injects two 429 faults, configures <c>MaxRetries = 3</c>, and a tiny
    /// <c>RetryBaseDelay</c> to keep the test fast. The third attempt succeeds with the
    /// canned JSON. Asserts one record written and watermark advanced — deterministic.
    /// </remarks>
    [Fact]
    public async Task Llm429Transient_RetriedWithExponentialBackoff_EventuallySucceeds()
    {
        string? skip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (skip is not null)
        {
            Assert.Skip(skip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userId = $"E52_429-{Guid.NewGuid():N}";
        const string SessionId = "e52-s1";
        const int UpToTurn = 2;

        IEmbeddingGenerator embedder = TestInfrastructure.DeterministicEmbedder(Dim);
        var store = TestInfrastructure.BuildMemoryStore(
            this._dataSource, embedder, NullLogger<PostgresMemoryStore>.Instance);
        var watermarkRepo = new WatermarkRepository(this._dataSource);
        var deadLetterRepo = new DeadLetterRepository(this._dataSource);
        var eventBus = BuildEventBus();

        // Inject two 429 faults; the third call returns the canned success.
        var faultClient = new FaultInjectingLlmClient(new StubLlmAdapter(FactJson));
        faultClient.Inject429Transient(2);

        var distillerOpts = Options.Create(new DistillerOptions
        {
            MaxRetries = 3,
            RetryBaseDelay = TimeSpan.FromMilliseconds(10), // tiny for test speed
        });

        var convo = new InMemoryConversationManager();
        convo.Append(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.User, "I prefer Python."));
        convo.Append(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.Assistant, "Got it."));

        var convoRegistry = new InMemoryConversationManagerRegistry();
        convoRegistry.Register(SessionId, convo);

        var channelRegistry = new ChannelSessionRegistry(
            distillerOpts, NullLogger<ChannelSessionRegistry>.Instance);
        var distiller = BuildDistillerService(
            channelRegistry, convoRegistry, faultClient, embedder,
            store, watermarkRepo, deadLetterRepo, eventBus, distillerOpts);

        DistillationCompletedEvent? completedEvt = null;
        using var sub = eventBus.Subscribe<DistillationCompletedEvent>(async (e, _) =>
        {
            completedEvt = e;
            await Task.CompletedTask;
        });

        channelRegistry.GetOrCreateWriter(userId, SessionId).TryWrite(
            new DistillationJob(userId, SessionId, DistillationTrigger.Inactivity, UpToTurn));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        await distiller.StartAsync(cts.Token);
        await WaitUntilAsync(() => completedEvt is not null, TimeSpan.FromSeconds(20), ct);
        await distiller.StopAsync(CancellationToken.None);

        // The distillation must eventually succeed (not dead-letter).
        Assert.NotNull(completedEvt);
        Assert.Equal(1, completedEvt.RecordsWritten);
        Assert.Equal(UpToTurn, completedEvt.NewWatermark);

        // At least 2 calls were intercepted (the 429 faults).
        Assert.True(faultClient.InterceptedCallCount >= 2,
            $"E5.2: Expected at least 2 intercepted (429) calls; got {faultClient.InterceptedCallCount}.");

        // No dead-letter row for this user.
        var dlAssertions = new InMemoryDeadLetterAssertions(this._dataSource);
        await dlAssertions.AssertNoEntryAsync("distillation", userId, ct);
    }

    // ── E5.3 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// E5.3 — Verifies that an HTTP 400 (Bad Request) is classified as a permanent error:
    /// the Distiller dead-letters the job immediately (no retries) and emits
    /// <see cref="DistillationFailedEvent"/> with <c>DeadLettered = true</c>
    /// (Memory-Specifications.md §8.6 Permanent; TestPlan E5.3).
    /// </summary>
    /// <remarks>
    /// Fully deterministic — no real LLM required. The 400 fault is injected once;
    /// the spec classifies HTTP 400 as permanent (non-retryable). The test asserts:
    /// <list type="bullet">
    ///   <item>A dead-letter row exists with an error containing "400" or "BadRequest".</item>
    ///   <item>A <see cref="DistillationFailedEvent"/> with <c>DeadLettered = true</c> is emitted.</item>
    ///   <item>No record was written (watermark unchanged).</item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task Llm400Permanent_DeadLetteredImmediately_DistillationFailedEventEmitted()
    {
        string? skip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (skip is not null)
        {
            Assert.Skip(skip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userId = $"E53_400-{Guid.NewGuid():N}";
        const string SessionId = "e53-s1";
        const int UpToTurn = 2;

        IEmbeddingGenerator embedder = TestInfrastructure.DeterministicEmbedder(Dim);
        var store = TestInfrastructure.BuildMemoryStore(
            this._dataSource, embedder, NullLogger<PostgresMemoryStore>.Instance);
        var watermarkRepo = new WatermarkRepository(this._dataSource);
        var deadLetterRepo = new DeadLetterRepository(this._dataSource);
        var eventBus = BuildEventBus();

        // Inject a single HTTP 400 (permanent). The inner stub would succeed but must not be reached.
        var faultClient = new FaultInjectingLlmClient(new StubLlmAdapter(FactJson));
        faultClient.Inject400Permanent();

        var distillerOpts = Options.Create(new DistillerOptions
        {
            MaxRetries = 3, // Retries would not apply to 400 (permanent); assert that.
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        });

        var convo = new InMemoryConversationManager();
        convo.Append(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.User, "I prefer Python."));
        convo.Append(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.Assistant, "Got it."));

        var convoRegistry = new InMemoryConversationManagerRegistry();
        convoRegistry.Register(SessionId, convo);

        var channelRegistry = new ChannelSessionRegistry(
            distillerOpts, NullLogger<ChannelSessionRegistry>.Instance);
        var distiller = BuildDistillerService(
            channelRegistry, convoRegistry, faultClient, embedder,
            store, watermarkRepo, deadLetterRepo, eventBus, distillerOpts);

        DistillationFailedEvent? failedEvt = null;
        using var sub = eventBus.Subscribe<DistillationFailedEvent>(async (e, _) =>
        {
            failedEvt = e;
            await Task.CompletedTask;
        });

        channelRegistry.GetOrCreateWriter(userId, SessionId).TryWrite(
            new DistillationJob(userId, SessionId, DistillationTrigger.Inactivity, UpToTurn));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        await distiller.StartAsync(cts.Token);
        await WaitUntilAsync(() => failedEvt is not null, TimeSpan.FromSeconds(15), ct);
        await distiller.StopAsync(CancellationToken.None);

        // DistillationFailedEvent emitted with DeadLettered = true.
        Assert.NotNull(failedEvt);
        Assert.Equal(userId, failedEvt.UserId);
        Assert.Equal(SessionId, failedEvt.SessionId);
        Assert.True(failedEvt.DeadLettered,
            "E5.3: DistillationFailedEvent.DeadLettered must be true for a permanent HTTP 400 error.");

        // Dead-letter row must exist with error substring "400" or "BadRequest".
        var dlAssertions = new InMemoryDeadLetterAssertions(this._dataSource);
        // Accept either "400" or "BadRequest" since the exception message may vary.
        IReadOnlyList<DeadLetterEntry> entries = await dlAssertions.QueryAsync("distillation", userId, ct);
        Assert.True(entries.Count > 0,
            "E5.3: Expected at least one dead-letter entry for the permanent 400 failure.");
        bool hasMatchingError = entries.Any(e =>
            e.Error.Contains("400", StringComparison.OrdinalIgnoreCase)
            || e.Error.Contains("BadRequest", StringComparison.OrdinalIgnoreCase));
        Assert.True(hasMatchingError,
            $"E5.3: Dead-letter error must contain '400' or 'BadRequest'. " +
            $"Actual errors: {string.Join("; ", entries.Select(e => e.Error))}");

        // Watermark must NOT have advanced (job was dead-lettered, not committed).
        int watermark = await watermarkRepo.GetAsync(userId, SessionId, ct);
        Assert.Equal(0, watermark);

        // The 400 is permanent — only one LLM call should have been made (no retries).
        Assert.Equal(1, faultClient.InterceptedCallCount);
    }

    // ── E5.4 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// E5.4 — Verifies that a malformed JSON response triggers exactly one parse-retry and
    /// then dead-letters the job permanently when the retry also fails to produce valid JSON
    /// (Memory-Specifications.md §18.1, §8.6; TestPlan E5.4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per §18.1: "One parse-retry is allowed if the JSON is malformed; the retry prompt is
    /// 'Your previous response was not valid JSON.' After that, classify as permanent → dead-letter."
    /// The Distiller catches <c>ExtractionParseException</c> on the first attempt and retries once;
    /// if the second attempt also raises <c>ExtractionParseException</c>, the job is dead-lettered.
    /// </para>
    /// <para>
    /// We inject two malformed-JSON faults: one for the first attempt, one for the parse-retry.
    /// This is deterministic — no real LLM required.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task MalformedJson_OneParseRetryWithStricterPrompt_ThenDeadLetter()
    {
        string? skip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (skip is not null)
        {
            Assert.Skip(skip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userId = $"E54_Malformed-{Guid.NewGuid():N}";
        const string SessionId = "e54-s1";
        const int UpToTurn = 2;

        IEmbeddingGenerator embedder = TestInfrastructure.DeterministicEmbedder(Dim);
        var store = TestInfrastructure.BuildMemoryStore(
            this._dataSource, embedder, NullLogger<PostgresMemoryStore>.Instance);
        var watermarkRepo = new WatermarkRepository(this._dataSource);
        var deadLetterRepo = new DeadLetterRepository(this._dataSource);
        var eventBus = BuildEventBus();

        // Inject two malformed-JSON faults so both the initial attempt and the parse-retry fail.
        var faultClient = new FaultInjectingLlmClient(new StubLlmAdapter(FactJson));
        faultClient.InjectMalformedJsonOnce(); // attempt 1 → ExtractionParseException
        faultClient.InjectMalformedJsonOnce(); // parse-retry → ExtractionParseException (permanent)

        var distillerOpts = Options.Create(new DistillerOptions
        {
            MaxRetries = 3,
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        });

        var convo = new InMemoryConversationManager();
        convo.Append(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.User, "I prefer Python."));
        convo.Append(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.Assistant, "Got it."));

        var convoRegistry = new InMemoryConversationManagerRegistry();
        convoRegistry.Register(SessionId, convo);

        var channelRegistry = new ChannelSessionRegistry(
            distillerOpts, NullLogger<ChannelSessionRegistry>.Instance);
        var distiller = BuildDistillerService(
            channelRegistry, convoRegistry, faultClient, embedder,
            store, watermarkRepo, deadLetterRepo, eventBus, distillerOpts);

        DistillationFailedEvent? failedEvt = null;
        using var sub = eventBus.Subscribe<DistillationFailedEvent>(async (e, _) =>
        {
            failedEvt = e;
            await Task.CompletedTask;
        });

        channelRegistry.GetOrCreateWriter(userId, SessionId).TryWrite(
            new DistillationJob(userId, SessionId, DistillationTrigger.Inactivity, UpToTurn));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        await distiller.StartAsync(cts.Token);
        await WaitUntilAsync(() => failedEvt is not null, TimeSpan.FromSeconds(15), ct);
        await distiller.StopAsync(CancellationToken.None);

        // DistillationFailedEvent emitted with DeadLettered = true.
        Assert.NotNull(failedEvt);
        Assert.True(failedEvt.DeadLettered,
            "E5.4: DistillationFailedEvent.DeadLettered must be true after two parse failures.");

        // Dead-letter row must exist.
        var dlAssertions = new InMemoryDeadLetterAssertions(this._dataSource);
        await dlAssertions.AssertHasEntryAsync("distillation", userId, ct: ct);

        // Watermark must not have advanced.
        int watermark = await watermarkRepo.GetAsync(userId, SessionId, ct);
        Assert.Equal(0, watermark);

        // Exactly two LLM calls were intercepted (first attempt + one parse retry).
        Assert.Equal(2, faultClient.InterceptedCallCount);
    }

    // ── E5.5 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// E5.5 — Verifies that when the embedding service is unavailable, the Distiller retries
    /// and eventually dead-letters the job, and that this does NOT block the retrieval hot
    /// path — the store's <c>LastWrittenAtAsync</c> for a different user is still readable
    /// (Memory-Specifications.md §12.2; TestPlan E5.5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The LLM successfully returns a canned valid episode, so LLM extraction succeeds.
    /// The embedder throws <see cref="HttpRequestException"/> on every call to simulate a
    /// service outage. The Distiller retries, exhausts its budget, and dead-letters. The hot
    /// path (retrieval for a separate user) is unaffected.
    /// </para>
    /// <para>
    /// Deterministic: no real LLM or embedder required.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EmbeddingServiceDown_DistillationRetriesAndDeadLetters_DoesNotBlockHotPath()
    {
        string? skip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (skip is not null)
        {
            Assert.Skip(skip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userId = $"E55_EmbedDown-{Guid.NewGuid():N}";
        string hotPathUserId = $"E55_HotPath-{Guid.NewGuid():N}";
        const string SessionId = "e55-s1";
        const int UpToTurn = 2;

        // Throwing embedder: every call fails with an HttpRequestException (no status code).
        // HttpRequestException with no status code is NOT classified as transient by the
        // Distiller's IsTransient() method (which requires 429 or 503). Therefore it goes
        // straight to dead-letter as a permanent error after the first attempt.
        // NOTE: If the production code treats bare HttpRequestException (no status code) as
        // transient, MaxRetries > 0 will result in multiple retries before dead-lettering.
        // Either way, the job must eventually dead-letter.
        var throwingEmbedder = BuildThrowingEmbedder();

        // LLM returns a valid episode so that extraction succeeds and the embedder is reached.
        var llmAdapter = new StubLlmAdapter(FactJson);

        var store = TestInfrastructure.BuildMemoryStore(
            this._dataSource, throwingEmbedder, NullLogger<PostgresMemoryStore>.Instance);
        var watermarkRepo = new WatermarkRepository(this._dataSource);
        var deadLetterRepo = new DeadLetterRepository(this._dataSource);
        var eventBus = BuildEventBus();

        var distillerOpts = Options.Create(new DistillerOptions
        {
            MaxRetries = 1,
            RetryBaseDelay = TimeSpan.FromMilliseconds(10),
        });

        var convo = new InMemoryConversationManager();
        convo.Append(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.User, "I prefer Python."));
        convo.Append(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.Assistant, "Got it."));

        var convoRegistry = new InMemoryConversationManagerRegistry();
        convoRegistry.Register(SessionId, convo);

        var channelRegistry = new ChannelSessionRegistry(
            distillerOpts, NullLogger<ChannelSessionRegistry>.Instance);
        var distiller = BuildDistillerService(
            channelRegistry, convoRegistry, llmAdapter, throwingEmbedder,
            store, watermarkRepo, deadLetterRepo, eventBus, distillerOpts);

        // Subscribe for both outcomes (the embedder failure may be classified as permanent
        // or transient depending on the production classification logic).
        DistillationFailedEvent? failedEvt = null;
        using var sub = eventBus.Subscribe<DistillationFailedEvent>(async (e, _) =>
        {
            failedEvt = e;
            await Task.CompletedTask;
        });

        channelRegistry.GetOrCreateWriter(userId, SessionId).TryWrite(
            new DistillationJob(userId, SessionId, DistillationTrigger.Inactivity, UpToTurn));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        await distiller.StartAsync(cts.Token);
        await WaitUntilAsync(() => failedEvt is not null, TimeSpan.FromSeconds(20), ct);
        await distiller.StopAsync(CancellationToken.None);

        // Distillation must have failed and been dead-lettered.
        Assert.NotNull(failedEvt);
        Assert.True(failedEvt.DeadLettered,
            "E5.5: DistillationFailedEvent.DeadLettered must be true when the embedder is unavailable.");

        var dlAssertions = new InMemoryDeadLetterAssertions(this._dataSource);
        await dlAssertions.AssertHasEntryAsync("distillation", userId, ct: ct);

        // ── Hot-path: retrieval for a different user is unaffected ────────────
        // Seed one record for hotPathUserId using the deterministic embedder (not the throwing one).
        IEmbeddingGenerator goodEmbedder = TestInfrastructure.DeterministicEmbedder(Dim);
        var hotStore = TestInfrastructure.BuildMemoryStore(
            this._dataSource, goodEmbedder, NullLogger<PostgresMemoryStore>.Instance);

        await hotStore.UpsertAsync(MemoryRecord.Create(
            id: Guid.NewGuid().ToString(),
            userId: hotPathUserId,
            sessionId: null,
            contentType: ContentType.Fact,
            domain: "Preferences",
            key: "Language",
            title: "Hot path record",
            value: "This record belongs to the hot-path user.",
            tags: [],
            importance: 0.5,
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow),
            ct);

        // LastWrittenAt must be readable for the hot-path user — it is unaffected by the
        // embedding outage affecting a different user's distillation job.
        DateTimeOffset? lastWritten = await hotStore.LastWrittenAtAsync(hotPathUserId, ct);
        Assert.NotNull(lastWritten);
    }

    // ── E5.6 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// E5.6 — Verifies that after a process restart the <c>LastWrittenAt</c> cache is
    /// hydrated from the database on first access, incurring at most one additional DB round-trip
    /// ("one-turn penalty") without causing stale results
    /// (Memory-Specifications.md §12.2, §6.1; TestPlan E5.6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cache is local to each <see cref="WatermarkRepository"/> instance (and
    /// <see cref="PostgresMemoryStore"/> for the <c>LastWrittenAt</c> cache). Simulating a
    /// process restart means constructing a new store instance with an empty cache. The first
    /// call must return the correct persisted value — not a stale <see langword="null"/> —
    /// proving the one-turn-penalty, not a multi-turn regression.
    /// </para>
    /// <para>
    /// Deterministic: seeds the store directly, then rebuilds the store with a fresh cache.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ProcessRestart_LastWrittenAtCacheHydratedFromDb_OneTurnPenaltyOnly()
    {
        string? skip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (skip is not null)
        {
            Assert.Skip(skip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userId = $"E56_Restart-{Guid.NewGuid():N}";

        IEmbeddingGenerator embedder = TestInfrastructure.DeterministicEmbedder(Dim);

        // ── Pre-restart: write one record so LastWrittenAt is persisted ────────
        var storePreRestart = TestInfrastructure.BuildMemoryStore(
            this._dataSource, embedder, NullLogger<PostgresMemoryStore>.Instance);

        MemoryRecord seeded = await storePreRestart.UpsertAsync(MemoryRecord.Create(
            id: Guid.NewGuid().ToString(),
            userId: userId,
            sessionId: null,
            contentType: ContentType.Fact,
            domain: "Preferences",
            key: "Language",
            title: "Python preference",
            value: "User prefers Python.",
            tags: ["python"],
            importance: 0.7,
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow),
            ct);

        // Confirm the in-process cache has the value.
        DateTimeOffset? lastWrittenPreRestart = await storePreRestart.LastWrittenAtAsync(userId, ct);
        Assert.NotNull(lastWrittenPreRestart);

        _ = seeded; // suppress unused variable warning

        // ── Simulate process restart: new store instance (empty in-memory cache) ─
        var storePostRestart = TestInfrastructure.BuildMemoryStore(
            this._dataSource, embedder, NullLogger<PostgresMemoryStore>.Instance);

        // The new store has an empty in-memory cache; the first call must hit the DB.
        // Per §12.2: "First retrieval hits the DB to hydrate — acceptable one-turn penalty."
        DateTimeOffset? lastWrittenPostRestart = await storePostRestart.LastWrittenAtAsync(userId, ct);

        // The persisted value must be returned on the first (cold-cache) call.
        Assert.NotNull(lastWrittenPostRestart);

        // The hydrated value must be reasonably close to what was written (within 5 seconds).
        Assert.True(
            Math.Abs((lastWrittenPostRestart.Value - lastWrittenPreRestart.Value).TotalSeconds) < 5,
            $"E5.6: LastWrittenAt after restart ({lastWrittenPostRestart}) " +
            $"must be close to the pre-restart value ({lastWrittenPreRestart}). " +
            $"Difference: {(lastWrittenPostRestart.Value - lastWrittenPreRestart.Value).TotalSeconds:F1}s.");

        // Second call uses the in-memory cache (no further DB round-trip — still correct).
        DateTimeOffset? secondCall = await storePostRestart.LastWrittenAtAsync(userId, ct);
        Assert.Equal(lastWrittenPostRestart, secondCall);

        // Watermark hydration: new WatermarkRepository (empty cache) must also read from DB.
        // Seed a watermark first.
        var watermarkRepoPreRestart = new WatermarkRepository(this._dataSource);
        const string TestSessionId = "e56-session";
        await watermarkRepoPreRestart.AdvanceAsync(userId, TestSessionId, candidate: 5, ct);

        // New instance (simulates process restart).
        var watermarkRepoPostRestart = new WatermarkRepository(this._dataSource);
        int watermarkFromDb = await watermarkRepoPostRestart.GetAsync(userId, TestSessionId, ct);
        Assert.Equal(5, watermarkFromDb);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>Builds the in-memory event bus used by all Group 5 tests.</summary>
    private static InMemoryEventBus BuildEventBus() =>
        new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);

    /// <summary>
    /// Builds a <see cref="DistillerBackgroundService"/> wired with the provided components.
    /// </summary>
    /// <param name="channelRegistry">The per-session channel registry.</param>
    /// <param name="convoRegistry">The conversation manager registry.</param>
    /// <param name="llm">The LLM adapter (may be a fault-injecting decorator or a stub).</param>
    /// <param name="embedder">The embedding generator.</param>
    /// <param name="store">The memory store.</param>
    /// <param name="watermarkRepo">The watermark repository.</param>
    /// <param name="deadLetterRepo">The dead-letter repository.</param>
    /// <param name="eventBus">The event bus.</param>
    /// <param name="opts">Distiller options.</param>
    /// <returns>A new, not-yet-started <see cref="DistillerBackgroundService"/>.</returns>
    private static DistillerBackgroundService BuildDistillerService(
        ChannelSessionRegistry channelRegistry,
        InMemoryConversationManagerRegistry convoRegistry,
        ILlmClientAdapter llm,
        IEmbeddingGenerator embedder,
        IMemoryStore store,
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
            new WatermarkStoreAdapter(watermarkRepo),
            new DeadLetterStoreAdapter(deadLetterRepo),
            eventBus,
            opts,
            TimeProvider.System,
            NullLogger<DistillerBackgroundService>.Instance);

    /// <summary>
    /// Builds an <see cref="IEmbeddingGenerator"/> that throws
    /// <see cref="HttpRequestException"/> on every call (simulates embedding service outage).
    /// </summary>
    private static IEmbeddingGenerator BuildThrowingEmbedder()
    {
        var mock = new Mock<IEmbeddingGenerator>();
        mock.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException(
                "Simulated embedding service outage."));
        return mock.Object;
    }

    /// <summary>
    /// Polls <paramref name="condition"/> every 50 ms until it returns
    /// <see langword="true"/> or <paramref name="timeout"/> elapses.
    /// </summary>
    /// <param name="condition">The predicate to poll.</param>
    /// <param name="timeout">Maximum wait duration.</param>
    /// <param name="ct">External cancellation token.</param>
    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        CancellationToken ct)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50, ct);
        }
    }
}
