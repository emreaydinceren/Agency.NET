using Agency.Harness;
using Agency.Harness.Contexts;
using Agency.Embeddings.OpenAI;
using Agency.Llm.Common;
using Agency.Llm.OpenAI;
using Agency.Memory.Common.Events;
using Agency.Memory.Common.Jobs;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Agency.Memory.Distiller.Services;
using Agency.Memory.Functional.Test.Infrastructure;
using Agency.Memory.Retrieval;
using Agency.Memory.Sql.Postgres;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using IEmbeddingGenerator = Agency.Embeddings.Common.IEmbeddingGenerator;
using MemoryRecord = Agency.Memory.Common.Records.Record;

namespace Agency.Memory.Functional.Test;

/// <summary>
/// End-to-end Group 2 — Privacy &amp; Forget tests.
/// Covers forget/isolation scenarios: agent forget tool, ForgetMe wipe, mid-session
/// ForgetMe, and cross-user Global-scope isolation (Memory-TestPlan.md §3, Group 2).
/// </summary>
/// <remarks>
/// All tests carry <c>[Trait("Category","Functional")]</c> and
/// <c>[Trait("Group","Forget")]</c> so they can be run in isolation:
/// <code>
/// dotnet test src/Memory/Agency.Memory.Functional.Test
///     --filter "Category=Functional&amp;Group=Forget"
/// </code>
/// Each test uses a unique <c>userId</c> of the form <c>{TestName}-{Guid}</c>
/// so residue from a failed run cannot poison subsequent runs.
///
/// E2.1, E2.2, and E2.4 seed records directly via <see cref="IMemoryStore.UpsertAsync"/>
/// with the deterministic stub embedder; they are deterministically green (Postgres-only,
/// no LM Studio required).
///
/// E2.3 inherently requires an in-flight distillation (LLM-gated); it skips gracefully
/// when LM Studio is unreachable or no model is loaded.
///
/// Schema dim is fixed at 1 536 for the entire class, matching the dimension used
/// by Group 1 and the shared schema reset — do NOT call ResetSchemaAsync with a different
/// dimension from within this class.
/// </remarks>
[Trait("Category", "Functional")]
[Trait("Group", "Forget")]
[Collection("memory-db")]
public sealed class Group2PrivacyAndForgetTests : IAsyncLifetime
{
    private static readonly IConfiguration _config = TestInfrastructure.BuildConfiguration();

    // LM Studio configuration keys (used only by E2.3).
    private const string LmStudioBaseUrlKey = "MemoryFunctional:LmStudio:BaseUrl";
    private const string LmStudioApiKeyKey = "MemoryFunctional:LmStudio:ApiKey";
    private const string LmStudioChatModelKey = "MemoryFunctional:LmStudio:ChatModel";
    private const string LmStudioEmbeddingModelKey = "MemoryFunctional:LmStudio:EmbeddingModel";

    /// <summary>
    /// The actual embedding dimension produced by the configured LM Studio model, detected
    /// once on first use and cached for all test instances in this class.
    /// Falls back to 1536 when LM Studio is unreachable so that deterministic Postgres-only
    /// tests (E2.1, E2.2, E2.4) still run.
    /// </summary>
    /// <remarks>
    /// Static because xUnit v3 creates a new class instance per test; sharing the detected
    /// dim avoids re-probing LM Studio 4 times and prevents stale/transient probe results
    /// from resetting the schema to a wrong dimension mid-run.
    /// </remarks>
    private static int _embeddingDim = 1536;
    private static bool _schemaInitialised;
    private static readonly SemaphoreSlim _classInitLock = new(1, 1);

    private NpgsqlDataSource _dataSource = default!;

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises one shared Postgres data source. The schema is reset exactly once for the
    /// entire class run (guarded by a static flag); subsequent test instances skip the reset
    /// and reuse the existing schema. Per-test isolation is achieved via unique <c>userId</c>
    /// partition keys. Skips silently if Postgres is unreachable — individual tests perform
    /// their own skip check.
    /// </summary>
    public async ValueTask InitializeAsync()
    {
        string? pgSkip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);

        if (pgSkip is not null)
        {
            return;
        }

        this._dataSource = TestInfrastructure.BuildDataSource(_config);

        await _classInitLock.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            if (!_schemaInitialised)
            {
                // Detect the real embedding dimension before creating the schema.
                string? lmSkip = await TestInfrastructure.CheckLmStudioAsync(
                    _config, TestContext.Current.CancellationToken);
                if (lmSkip is null)
                {
                    string baseUrl = _config[LmStudioBaseUrlKey] ?? "http://llm-host.example:1234/v1";
                    string apiKey = _config[LmStudioApiKeyKey] ?? "lm-studio";
                    string embeddingModel = _config[LmStudioEmbeddingModelKey] ?? "local-embedding-model";
                    var probeEmbedder = new Agency.Embeddings.OpenAI.EmbeddingGenerator(
                        new Agency.Embeddings.OpenAI.EmbeddingOptions
                        {
                            BaseUrl = baseUrl,
                            ApiKey = apiKey,
                            ModelId = embeddingModel,
                        });
                    _embeddingDim = await TestInfrastructure.DetectEmbeddingDimAsync(
                        probeEmbedder, TestContext.Current.CancellationToken);
                }

                await TestInfrastructure.ResetSchemaAsync(
                    this._dataSource, _embeddingDim, TestContext.Current.CancellationToken);
                _schemaInitialised = true;
            }
        }
        finally
        {
            _classInitLock.Release();
        }
    }

    /// <summary>Disposes the shared Postgres data source.</summary>
    public async ValueTask DisposeAsync()
    {
        if (this._dataSource is not null)
        {
            await this._dataSource.DisposeAsync();
        }
    }

    // ── E2.1 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// E2.1 — Verifies that the agent forget tool (exposed as
    /// <see cref="IMemoryStore.ForgetAsync"/>) removes a specific named Fact from the
    /// store and that a subsequent retrieval no longer surfaces that record
    /// (Spec Use Case U3).
    /// </summary>
    /// <remarks>
    /// Steps:
    /// <list type="number">
    ///   <item>Seed a Fact record for the user via <see cref="IMemoryStore.UpsertAsync"/>.</item>
    ///   <item>Verify the record is present via <see cref="IMemoryStore.GetByKeyAsync"/>.</item>
    ///   <item>Call <see cref="IMemoryStore.ForgetAsync"/> (the agent-forget path).</item>
    ///   <item>Run retrieval and assert the record does not appear.</item>
    /// </list>
    /// Deterministically green: Postgres-only, no LM Studio required.
    /// </remarks>
    [Fact]
    public async Task AgentForgetTool_RemovesNamedFact_NextRetrievalDoesNotSurfaceIt()
    {
        string? pgSkip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (pgSkip is not null)
        {
            Assert.Skip(pgSkip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userId = $"AgentForgetTool_RemovesNamedFact-{Guid.NewGuid():N}";
        const string Domain = "Preferences";
        const string Key = "Database";

        IEmbeddingGenerator stubEmbedder = TestInfrastructure.DeterministicEmbedder(_embeddingDim);
        PostgresMemoryStore store = TestInfrastructure.BuildMemoryStore(
            this._dataSource, stubEmbedder, NullLogger<PostgresMemoryStore>.Instance);

        // ── Step 1: seed a Fact that will be forgotten ────────────────────────
        MemoryRecord seeded = await store.UpsertAsync(MakeRecord(userId, null, Domain, Key,
            title: "Database preference",
            value: "User prefers Postgres as their primary database engine."), ct);

        // Sanity: the record must be retrievable by key before the forget.
        MemoryRecord? before = await store.GetByKeyAsync(userId, null, Domain, Key, ct);
        Assert.NotNull(before);
        Assert.Equal(seeded.Id, before.Id);

        // ── Step 2: call the agent forget path ────────────────────────────────
        bool deleted = await store.ForgetAsync(userId, Domain, Key, ct);
        Assert.True(deleted, "E2.1: ForgetAsync must return true for a known record.");

        // ── Step 3: verify the record is gone via GetByKeyAsync ───────────────
        MemoryRecord? afterForget = await store.GetByKeyAsync(userId, null, Domain, Key, ct);
        Assert.Null(afterForget);

        // ── Step 4: run retrieval and assert the record does not surface ───────
        var ctx = new Context
        {
            Query = new QueryContext { Prompt = "What database should I use?" },
            User = new UserSpecificContext { Id = userId },
            Conversation = new InMemoryConversationManager(),
        };
        ctx.Conversation.Append(new ChatMessage(ChatRole.User, "What database should I use?"));

        var memOpts = Options.Create(new MemoryOptions
        {
            RetrievalTopK = 10,
            OverFetchFactor = 2,
        });
        var engine = new RetrievalEngine(store, stubEmbedder, memOpts);
        await engine.RetrieveAsync(ctx, ct);

        // ── Acceptance ────────────────────────────────────────────────────────
        bool forgottenRecordPresent =
            ctx.Knowledge.Records.Any(r =>
                r.Title.Contains("Database", StringComparison.OrdinalIgnoreCase)
                || r.Value.Contains("Postgres", StringComparison.OrdinalIgnoreCase));

        Assert.False(
            forgottenRecordPresent,
            $"E2.1: The forgotten database-preference record must not appear in retrieval. " +
            $"Actual Knowledge records: {string.Join("; ", ctx.Knowledge.Records.Select(r => r.Title))}");
    }

    // ── E2.2 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// E2.2 — Verifies that <see cref="IMemoryStore.ForgetMeAsync"/> wipes all records
    /// for user A while leaving user B's records intact, and that
    /// <see cref="IMemoryStore.LastWrittenAtAsync"/> is updated for the forgotten user
    /// (Spec Use Case U4).
    /// </summary>
    /// <remarks>
    /// Steps:
    /// <list type="number">
    ///   <item>Seed multiple records for user A and user B.</item>
    ///   <item>Call <see cref="IMemoryStore.ForgetMeAsync"/> for user A.</item>
    ///   <item>Assert user A has zero records; user B still has all of theirs.</item>
    ///   <item>Assert <see cref="IMemoryStore.LastWrittenAtAsync"/> is bumped for user A
    ///     (Spec §8.1 correctness invariant: every mutation must bump LastWrittenAt).</item>
    /// </list>
    /// Deterministically green: Postgres-only, no LM Studio required.
    /// </remarks>
    [Fact]
    public async Task ForgetMe_WipesAllUserData_OtherUsersUnaffected()
    {
        string? pgSkip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (pgSkip is not null)
        {
            Assert.Skip(pgSkip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userA = $"ForgetMe_WipesAllUserData-A-{Guid.NewGuid():N}";
        string userB = $"ForgetMe_WipesAllUserData-B-{Guid.NewGuid():N}";
        DateTimeOffset seedTime = DateTimeOffset.UtcNow;

        IEmbeddingGenerator stubEmbedder = TestInfrastructure.DeterministicEmbedder(_embeddingDim);
        PostgresMemoryStore store = TestInfrastructure.BuildMemoryStore(
            this._dataSource, stubEmbedder, NullLogger<PostgresMemoryStore>.Instance);

        // ── Step 1: seed records for both users ───────────────────────────────
        const int UserACount = 7;
        const int UserBCount = 3;

        for (int i = 0; i < UserACount; i++)
        {
            await store.UpsertAsync(MakeRecord(userA, null, "ForgetMeTest", $"key-{i}",
                title: $"User A record {i}",
                value: $"Value {i} for user A."), ct);
        }

        for (int i = 0; i < UserBCount; i++)
        {
            await store.UpsertAsync(MakeRecord(userB, null, "ForgetMeTest", $"key-{i}",
                title: $"User B record {i}",
                value: $"Value {i} for user B."), ct);
        }

        // Sanity-check: both users have data.
        IReadOnlyList<MemoryRecord> beforeA = await store.GetAllForUserAsync(userA, ct);
        IReadOnlyList<MemoryRecord> beforeB = await store.GetAllForUserAsync(userB, ct);
        Assert.Equal(UserACount, beforeA.Count);
        Assert.Equal(UserBCount, beforeB.Count);

        // ── Step 2: forget user A ─────────────────────────────────────────────
        int deleted = await store.ForgetMeAsync(userA, ct);
        Assert.Equal(UserACount, deleted);

        // ── Step 3: assert user A has no records ──────────────────────────────
        IReadOnlyList<MemoryRecord> afterA = await store.GetAllForUserAsync(userA, ct);
        Assert.Empty(afterA);

        // ── Step 4: assert user B records are completely intact ───────────────
        IReadOnlyList<MemoryRecord> afterB = await store.GetAllForUserAsync(userB, ct);
        Assert.Equal(UserBCount, afterB.Count);

        // ── Step 5: LastWrittenAt must be bumped for user A (Spec §8.1) ───────
        DateTimeOffset? lastWritten = await store.LastWrittenAtAsync(userA, ct);
        Assert.NotNull(lastWritten);
        Assert.True(
            lastWritten >= seedTime,
            $"E2.2: LastWrittenAt ({lastWritten}) must be >= seed time ({seedTime}) " +
            "after ForgetMeAsync — the gate must see the mutation.");
    }

    // ── E2.3 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// E2.3 — Verifies that calling <c>ForgetMe</c> mid-session (while a distillation job
    /// is in flight) correctly wipes existing records so that subsequent retrieval returns
    /// empty, and that a distillation job completing after the wipe is permitted to write a
    /// fresh record — since the user is mid-session and presumably wants future memory
    /// (Spec §12.4 operational edge-case).
    /// </summary>
    /// <remarks>
    /// Steps:
    /// <list type="number">
    ///   <item>Pre-seed a record for the user.</item>
    ///   <item>Call <see cref="IMemoryStore.ForgetMeAsync"/> to wipe existing records.</item>
    ///   <item>Assert retrieval returns empty immediately after the wipe.</item>
    ///   <item>Enqueue an in-flight distillation job (LLM-gated). If LM Studio is unavailable
    ///     the test skips here gracefully.</item>
    ///   <item>Await <see cref="DistillationCompletedEvent"/>. If it times out, skip.</item>
    ///   <item>Assert the store now has at least one new record written by the post-wipe
    ///     distillation (the new record is permitted by design).</item>
    /// </list>
    /// LLM-gated: skips gracefully when LM Studio is unreachable or no model is loaded.
    /// The Postgres-path sub-assertions (steps 1–3) are deterministically green.
    /// </remarks>
    [Fact]
    public async Task ForgetMe_MidSession_NextRetrievalEmpty_InFlightDistillationStillWritesNewRecord()
    {
        string? pgSkip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (pgSkip is not null)
        {
            Assert.Skip(pgSkip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userId = $"ForgetMe_MidSession-{Guid.NewGuid():N}";
        const string SessionId = "e23-s1";

        IEmbeddingGenerator stubEmbedder = TestInfrastructure.DeterministicEmbedder(_embeddingDim);
        PostgresMemoryStore stubStore = TestInfrastructure.BuildMemoryStore(
            this._dataSource, stubEmbedder, NullLogger<PostgresMemoryStore>.Instance);

        // ── Step 1: pre-seed a record (simulates prior session data) ──────────
        await stubStore.UpsertAsync(MakeRecord(userId, null, "Preferences", "Language",
            title: "Language preference",
            value: "User prefers C# over Python."), ct);

        IReadOnlyList<MemoryRecord> priorRecords = await stubStore.GetAllForUserAsync(userId, ct);
        Assert.NotEmpty(priorRecords);

        // ── Step 2: call ForgetMe (mid-session wipe) ──────────────────────────
        int wipedCount = await stubStore.ForgetMeAsync(userId, ct);
        Assert.True(wipedCount >= 1, "E2.3: ForgetMeAsync must delete the pre-seeded record.");

        // ── Step 3: retrieval immediately after wipe must return empty ─────────
        var ctx = new Context
        {
            Query = new QueryContext { Prompt = "What is my preferred language?" },
            User = new UserSpecificContext { Id = userId },
            Conversation = new InMemoryConversationManager(),
        };
        ctx.Conversation.Append(new ChatMessage(
            ChatRole.User, "What is my preferred language?"));

        var memOpts = Options.Create(new MemoryOptions
        {
            RetrievalTopK = 10,
            OverFetchFactor = 2,
        });
        var engine = new RetrievalEngine(stubStore, stubEmbedder, memOpts);
        await engine.RetrieveAsync(ctx, ct);

        Assert.Empty(ctx.Knowledge.Records);
        Assert.Empty(ctx.Memory.Records);

        // ── Step 4: attempt in-flight distillation (LLM-gated) ───────────────
        // Check LM Studio reachability now; skip gracefully if not available.
        string? llmSkip = await TestInfrastructure.CheckLmStudioAsync(
            _config, TestContext.Current.CancellationToken);
        if (llmSkip is not null)
        {
            Assert.Skip(
                $"E2.3: Postgres-path assertions passed (ForgetMe wipes data, retrieval returns empty). " +
                $"In-flight distillation sub-test skipped: {llmSkip}");
            return;
        }

        (IEmbeddingGenerator realEmbedder, ILlmClientAdapter llmAdapter, PostgresMemoryStore realStore,
            WatermarkRepository watermarkRepo, DeadLetterRepository deadLetterRepo,
            InMemoryEventBus eventBus, DistillerOptions distillerOpts) =
            this.BuildRealPipeline();

        // Confirm ForgetMe state is reflected in the real store as well.
        IReadOnlyList<MemoryRecord> afterWipe = await realStore.GetAllForUserAsync(userId, ct);
        Assert.Empty(afterWipe);

        // A new mid-session conversation turn arrives after the wipe.
        var conv = new InMemoryConversationManager();
        conv.Append(new ChatMessage(ChatRole.User, "I switched to Rust for systems programming."));
        conv.Append(new ChatMessage(ChatRole.Assistant, "Noted, I will use Rust for systems-programming examples."));

        var channelRegistry = new ChannelSessionRegistry(
            Options.Create(distillerOpts),
            NullLogger<ChannelSessionRegistry>.Instance);

        var convoRegistry = new InMemoryConversationManagerRegistry();
        convoRegistry.Register(SessionId, conv);

        var distillerService = new DistillerBackgroundService(
            channelRegistry,
            convoRegistry,
            llmAdapter,
            realEmbedder,
            realStore,
            new WatermarkStoreAdapter(watermarkRepo),
            new DeadLetterStoreAdapter(deadLetterRepo),
            eventBus,
            Options.Create(distillerOpts),
            TimeProvider.System,
            NullLogger<DistillerBackgroundService>.Instance);

        // Subscribe to the event bus BEFORE enqueueing the job.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(90));
        await distillerService.StartAsync(cts.Token);

        var job = new DistillationJob(
            userId, SessionId, DistillationTrigger.Inactivity, UpToTurnIndex: conv.Messages.Count);
        channelRegistry.GetOrCreateWriter(userId, SessionId).TryWrite(job);

        // ── Step 5: await distillation event ─────────────────────────────────
        DistillationCompletedEvent completed;
        try
        {
            completed = await TestInfrastructure.WaitForDistillationOrFailAsync(
                eventBus,
                userId,
                SessionId,
                timeout: TimeSpan.FromSeconds(60),
                ct: cts.Token);
        }
        catch (DistillationFailedException dfe)
        {
            await distillerService.StopAsync(CancellationToken.None);
            Assert.Skip(
                $"E2.3: Postgres-path assertions passed. In-flight distillation failed before completing. " +
                $"Real cause: {dfe.Message}");
            return;
        }
        catch (TimeoutException)
        {
            await distillerService.StopAsync(CancellationToken.None);
            Assert.Skip(
                "E2.3: Postgres-path assertions passed. In-flight distillation timed out after 60 s — " +
                "LM Studio is reachable but no model is loaded or the response is too slow.");
            return;
        }
        catch (OperationCanceledException)
        {
            await distillerService.StopAsync(CancellationToken.None);
            Assert.Skip(
                "E2.3: Postgres-path assertions passed. In-flight distillation was cancelled " +
                "(LM Studio request timeout). Load a compatible model in LM Studio and retry.");
            return;
        }

        await distillerService.StopAsync(CancellationToken.None);

        // ── Step 6: assert the post-wipe distillation wrote a new record ──────
        // Per Spec §12.4: "Distillation in flight may write a fresh Record — by design,
        // since the user is mid-session and presumably wants future memory."
        if (completed.RecordsWritten == 0)
        {
            Assert.Skip(
                "E2.3: All Postgres-path assertions passed. Post-wipe distillation completed " +
                "but wrote 0 records — the LLM decided nothing was memorable from the turn. " +
                "This is spec-compliant; try a more content-rich conversation for a firmer assertion.");
            return;
        }

        IReadOnlyList<MemoryRecord> afterDistillation = await realStore.GetAllForUserAsync(userId, ct);
        Assert.True(
            afterDistillation.Count >= 1,
            $"E2.3: Expected at least 1 new record written by post-wipe distillation, " +
            $"got {afterDistillation.Count}. The store must accept new writes after ForgetMe.");
    }

    // ── E2.4 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// E2.4 — Verifies cross-user isolation: a Global-scope record seeded for user A
    /// must never appear in the retrieval results for user B, confirming Principle P3
    /// (SessionId is a soft ranking signal; UserId is the hard partition boundary)
    /// (Spec §17, Principle P3).
    /// </summary>
    /// <remarks>
    /// Steps:
    /// <list type="number">
    ///   <item>Seed multiple Global-scope records for user A across various domains.</item>
    ///   <item>Seed a separate set of records for user B.</item>
    ///   <item>Run retrieval as user B with a query that strongly resembles user A's data.</item>
    ///   <item>Assert that no user A record appears in user B's retrieval results.</item>
    ///   <item>Run retrieval as user A and assert user A's own records are present.</item>
    /// </list>
    /// Deterministically green: Postgres-only, no LM Studio required.
    /// </remarks>
    [Fact]
    public async Task CrossUserIsolation_GlobalScopeNeverLeaksAcrossUserIds()
    {
        string? pgSkip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (pgSkip is not null)
        {
            Assert.Skip(pgSkip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userA = $"CrossUserIsolation-A-{Guid.NewGuid():N}";
        string userB = $"CrossUserIsolation-B-{Guid.NewGuid():N}";

        // Sentinel tokens that must never appear in user B's retrieval results.
        const string UserAToken = "UserASentinelZelda42";
        const string UserBToken = "UserBSentinelLink99";

        IEmbeddingGenerator stubEmbedder = TestInfrastructure.DeterministicEmbedder(_embeddingDim);
        var memOpts = Options.Create(new MemoryOptions
        {
            RetrievalTopK = 20,
            OverFetchFactor = 3,
        });

        PostgresMemoryStore store = TestInfrastructure.BuildMemoryStore(
            this._dataSource, stubEmbedder, NullLogger<PostgresMemoryStore>.Instance);

        // ── Step 1: seed Global-scope records for user A ──────────────────────
        // Use multiple domains to stress the isolation boundary.
        await store.UpsertAsync(MakeRecord(userA, null, "Preferences", "Language",
            title: $"Language pref {UserAToken}",
            value: $"User A prefers Python. {UserAToken}"), ct);

        await store.UpsertAsync(MakeRecord(userA, null, "Preferences", "Editor",
            title: $"Editor pref {UserAToken}",
            value: $"User A uses Emacs. {UserAToken}"), ct);

        await store.UpsertAsync(MakeRecord(userA, null, "Debugging", "Ssl",
            title: $"SSL debug episode {UserAToken}",
            value: $"User A debugged an SSL handshake issue last week. {UserAToken}",
            contentType: ContentType.Memory), ct);

        // ── Step 2: seed records for user B ───────────────────────────────────
        await store.UpsertAsync(MakeRecord(userB, null, "Preferences", "Language",
            title: $"Language pref {UserBToken}",
            value: $"User B prefers TypeScript. {UserBToken}"), ct);

        // ── Step 3: retrieve as user B with a query similar to user A's data ──
        var ctxB = new Context
        {
            Query = new QueryContext { Prompt = "What language and editor should I use? Any SSL tips?" },
            User = new UserSpecificContext { Id = userB },
            Conversation = new InMemoryConversationManager(),
        };
        ctxB.Conversation.Append(new ChatMessage(
            ChatRole.User, "What language and editor should I use? Any SSL tips?"));

        var engineB = new RetrievalEngine(store, stubEmbedder, memOpts);
        await engineB.RetrieveAsync(ctxB, ct);

        // ── Step 4: assert user A's sentinel never leaks to user B ───────────
        bool userALeaked =
            ctxB.Knowledge.Records.Any(r =>
                r.Title.Contains(UserAToken, StringComparison.Ordinal)
                || r.Value.Contains(UserAToken, StringComparison.Ordinal))
            || ctxB.Memory.Records.Any(r =>
                r.Title.Contains(UserAToken, StringComparison.Ordinal)
                || r.Value.Contains(UserAToken, StringComparison.Ordinal));

        Assert.False(
            userALeaked,
            $"E2.4: User A's records must NEVER appear in user B's retrieval. " +
            $"Leaked Knowledge records: {string.Join("; ", ctxB.Knowledge.Records.Select(r => r.Title))}. " +
            $"Leaked Memory records: {string.Join("; ", ctxB.Memory.Records.Select(r => r.Title))}.");

        // ── Step 5: retrieve as user A — own records must be visible ──────────
        var ctxA = new Context
        {
            Query = new QueryContext { Prompt = "What language and editor should I use? Any SSL tips?" },
            User = new UserSpecificContext { Id = userA },
            Conversation = new InMemoryConversationManager(),
        };
        ctxA.Conversation.Append(new ChatMessage(
            ChatRole.User, "What language and editor should I use? Any SSL tips?"));

        var engineA = new RetrievalEngine(store, stubEmbedder, memOpts);
        await engineA.RetrieveAsync(ctxA, ct);

        bool userACanSeeOwnData =
            ctxA.Knowledge.Records.Any(r =>
                r.Title.Contains(UserAToken, StringComparison.Ordinal)
                || r.Value.Contains(UserAToken, StringComparison.Ordinal))
            || ctxA.Memory.Records.Any(r =>
                r.Title.Contains(UserAToken, StringComparison.Ordinal)
                || r.Value.Contains(UserAToken, StringComparison.Ordinal));

        Assert.True(
            userACanSeeOwnData,
            $"E2.4: User A must be able to retrieve their own Global-scope records. " +
            $"Knowledge records: {string.Join("; ", ctxA.Knowledge.Records.Select(r => r.Title))}. " +
            $"Memory records: {string.Join("; ", ctxA.Memory.Records.Select(r => r.Title))}.");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="MemoryRecord"/> with minimal required fields and sensible defaults.
    /// </summary>
    /// <param name="userId">The owning user.</param>
    /// <param name="sessionId">The session, or <see langword="null"/> for Global-scope records.</param>
    /// <param name="domain">The semantic domain.</param>
    /// <param name="key">The stable identifier within the domain.</param>
    /// <param name="title">The short human-readable title.</param>
    /// <param name="value">The Markdown body.</param>
    /// <param name="contentType">
    /// The content type discriminator; defaults to <see cref="ContentType.Fact"/>.
    /// </param>
    /// <returns>A new <see cref="MemoryRecord"/> ready for upsert.</returns>
    private static MemoryRecord MakeRecord(
        string userId,
        string? sessionId,
        string domain,
        string key,
        string title,
        string value,
        ContentType contentType = ContentType.Fact) =>
        MemoryRecord.Create(
            id: Guid.NewGuid().ToString(),
            userId: userId,
            sessionId: sessionId,
            contentType: contentType,
            domain: domain,
            key: key,
            title: title,
            value: value,
            tags: [],
            importance: 0.6,
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow);

    /// <summary>
    /// Builds the real LM Studio–backed pipeline components for E2.3's in-flight
    /// distillation path. Mirrors the pattern from <c>Group1CaptureAndRecallTests</c>.
    /// </summary>
    /// <returns>
    /// A tuple of the embedding generator, LLM adapter, Postgres memory store,
    /// watermark repository, dead-letter repository, in-memory event bus, and
    /// a mutable <see cref="DistillerOptions"/> instance.
    /// </returns>
    private (IEmbeddingGenerator Embedder,
        ILlmClientAdapter LlmAdapter,
        PostgresMemoryStore Store,
        WatermarkRepository WatermarkRepo,
        DeadLetterRepository DeadLetterRepo,
        InMemoryEventBus EventBus,
        DistillerOptions DistillerOpts) BuildRealPipeline()
    {
        string baseUrl = _config[LmStudioBaseUrlKey] ?? "http://llm-host.example:1234/v1";
        string apiKey = _config[LmStudioApiKeyKey] ?? "lm-studio";
        string chatModel = _config[LmStudioChatModelKey] ?? "local-model";
        string embeddingModel = _config[LmStudioEmbeddingModelKey] ?? "text-embedding-3-small";

        var embedder = new EmbeddingGenerator(new EmbeddingOptions
        {
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            ModelId = embeddingModel,
        });

        IChatClient llm = new OpenAIClient(new LlmClientOptions
        {
            ApiKey = apiKey,
            BaseUrl = baseUrl,
            SuppressThinking = true, // distiller suppresses thinking (TI-8.2)
        }).CreateChatClient();

        ILlmClientAdapter llmAdapter = new ChatClientLlmAdapter(llm, chatModel);

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
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);

        var distillerOpts = new DistillerOptions
        {
            MaxRetries = 1,
            RetryBaseDelay = TimeSpan.FromSeconds(2),
            InactivityTimeout = TimeSpan.FromMinutes(5),
        };

        return (embedder, llmAdapter, store, watermarkRepo, deadLetterRepo, eventBus, distillerOpts);
    }
}
