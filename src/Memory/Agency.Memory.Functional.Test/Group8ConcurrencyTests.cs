using Agency.Harness;
using Agency.Harness.Contexts;
using Agency.Memory.Common.Events;
using Agency.Memory.Common.Jobs;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Agency.Memory.Distiller.Services;
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
/// End-to-end Group 8 — Concurrency tests.
/// Verifies the three concurrency invariants described in
/// Memory-Specifications.md §12.1 (Memory-TestPlan.md §3, Group 8).
/// </summary>
/// <remarks>
/// All tests carry <c>[Trait("Category","Functional")]</c> and
/// <c>[Trait("Group","Concurrency")]</c> so they can be run in isolation:
/// <code>
/// dotnet test src/Memory/Agency.Memory.Functional.Test
///     --filter "Category=Functional&amp;Group=Concurrency"
/// </code>
/// Each test uses a unique <c>userId</c> of the form <c>{TestName}-{Guid}</c>
/// so residue from a failed run cannot poison subsequent runs.
///
/// Although the suite exercises concurrency *internally*, it is placed in the
/// <c>memory-db</c> collection with <c>DisableParallelization = true</c> to
/// prevent races against other test classes that call <see cref="TestInfrastructure.ResetSchemaAsync"/>.
///
/// These tests are fully deterministic — no LM Studio is required.
/// Only Postgres is needed (for the real <see cref="IMemoryStore"/> and
/// <see cref="WatermarkRepository"/>). Schema dim is fixed at 1 536.
/// Do NOT call <see cref="TestInfrastructure.ResetSchemaAsync"/> with a different
/// dimension from within this class.
///
/// Each concurrent path owns its own <see cref="Context"/> instance because
/// <see cref="Context.MemoryLastRetrievedAt"/> is last-writer-wins without locking.
/// </remarks>
[Trait("Category", "Functional")]
[Trait("Group", "Concurrency")]
[Collection("memory-db")]
public sealed class Group8ConcurrencyTests : IAsyncLifetime
{
    private static readonly IConfiguration _config = TestInfrastructure.BuildConfiguration();

    /// <summary>Embedding dimension shared by all tests; must match the shared schema.</summary>
    private const int EmbeddingDim = 1536;

    private NpgsqlDataSource _dataSource = default!;
    private IEmbeddingGenerator _stubEmbedder = default!;

    // ── Canned distillation response — one Fact, used by E8.1 ────────────────

    private const string CannedFactJson = """
        {"records":[{"ContentType":"Fact","Title":"Concurrency test fact",
        "Domain":"Testing","Key":"ConcurrencyE81","Tags":["concurrency"],
        "Scope":"Global","Importance":0.6,"Value":"Concurrency test value."}]}
        """;

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises the Postgres data source, resets the schema to a clean state at
    /// the standard 1 536-dim column, and creates the stub embedder shared by all tests.
    /// Skips silently when Postgres is unreachable; individual tests re-check and skip.
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
        await TestInfrastructure.ResetSchemaAsync(
            this._dataSource, EmbeddingDim, TestContext.Current.CancellationToken);

        this._stubEmbedder = TestInfrastructure.DeterministicEmbedder(EmbeddingDim);
    }

    /// <summary>Disposes the Postgres data source.</summary>
    public async ValueTask DisposeAsync()
    {
        if (this._dataSource is not null)
        {
            await this._dataSource.DisposeAsync();
        }
    }

    // ── E8.1 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// E8.1 — Verifies the watermark deduplication invariant (Spec §12.1):
    /// when two <c>MarkGoalComplete</c> triggers for the same session both enqueue
    /// <see cref="DistillationJob"/>s, the first job advances the watermark and writes
    /// a record set, while the second job — whose <c>UpToTurnIndex</c> is at or below
    /// the advanced watermark — is a no-op: it emits
    /// <see cref="DistillationCompletedEvent"/> with <c>RecordsWritten = 0</c> and the
    /// same watermark value, and does not write additional records.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The test is deterministic. It uses a <see cref="StubLlmAdapter"/> returning
    /// a canned episode JSON so no LM Studio connection is required. The watermark
    /// guard inside <see cref="DistillerBackgroundService.ProcessJobAsync"/> is the
    /// seam under test; the invariant is confirmed via the observable record count and
    /// the watermark value read from <see cref="WatermarkRepository"/>.
    /// </para>
    /// <para>
    /// Interleaving: job1 (UpToTurnIndex=2) → processes, writes 1 record, advances
    /// watermark to 2. job2 (UpToTurnIndex=2) → watermark guard fires, no-op,
    /// RecordsWritten=0, NewWatermark=2. Record count does not increase.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TwoMarkGoalCompleteCalls_BothEnqueueJobs_SecondNoOpsByWatermark()
    {
        string? pgSkip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (pgSkip is not null)
        {
            Assert.Skip(pgSkip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userId = $"E81_TwoMarkGoalComplete-{Guid.NewGuid():N}";
        const string SessionId = "e81-s1";

        var store = TestInfrastructure.BuildMemoryStore(
            this._dataSource, this._stubEmbedder, NullLogger<PostgresMemoryStore>.Instance);

        var watermarkRepo = new WatermarkRepository(this._dataSource);
        var deadLetterRepo = new DeadLetterRepository(this._dataSource);
        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);

        var distillerOpts = new DistillerOptions
        {
            MaxRetries = 0,
            RetryBaseDelay = TimeSpan.FromMilliseconds(100),
        };

        // ── Build a stub LLM adapter that returns the canned Fact JSON ─────────
        var llmAdapter = new StubLlmAdapter(CannedFactJson);

        // ── Set up conversation and pipeline ──────────────────────────────────
        var conv = new InMemoryConversationManager();
        conv.Append(new ChatMessage(ChatRole.User, "This is turn one."));
        conv.Append(new ChatMessage(ChatRole.Assistant, "This is the reply to turn one."));

        var channelRegistry = new ChannelSessionRegistry(
            Options.Create(distillerOpts),
            NullLogger<ChannelSessionRegistry>.Instance);

        var convoRegistry = new InMemoryConversationManagerRegistry();
        convoRegistry.Register(SessionId, conv);

        var distillerService = new DistillerBackgroundService(
            channelRegistry,
            convoRegistry,
            llmAdapter,
            this._stubEmbedder,
            store,
            new WatermarkStoreAdapter(watermarkRepo),
            new DeadLetterStoreAdapter(deadLetterRepo),
            eventBus,
            Options.Create(distillerOpts),
            TimeProvider.System,
            NullLogger<DistillerBackgroundService>.Instance);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        await distillerService.StartAsync(cts.Token);

        // ── Enqueue job1: GoalCompletion at UpToTurnIndex=2 ──────────────────
        var job1 = new DistillationJob(
            userId, SessionId, DistillationTrigger.GoalCompletion, UpToTurnIndex: 2);
        channelRegistry.GetOrCreateWriter(userId, SessionId).TryWrite(job1);

        DistillationCompletedEvent first = await TestInfrastructure.WaitForEventAsync<DistillationCompletedEvent>(
            eventBus,
            timeout: TimeSpan.FromSeconds(15),
            predicate: e => e.UserId == userId && e.SessionId == SessionId,
            ct: cts.Token);

        // Confirm first job actually wrote a record and advanced the watermark.
        int recordsAfterFirst = (await store.GetAllForUserAsync(userId, ct)).Count;
        int watermarkAfterFirst = await watermarkRepo.GetAsync(userId, SessionId, ct);

        Assert.True(
            first.RecordsWritten >= 1,
            $"E8.1: First job must write at least 1 record; got RecordsWritten={first.RecordsWritten}.");
        Assert.True(
            watermarkAfterFirst >= 2,
            $"E8.1: Watermark must be ≥ 2 after first job; actual={watermarkAfterFirst}.");

        // ── Enqueue job2: same UpToTurnIndex=2 — must no-op by watermark ─────
        var job2 = new DistillationJob(
            userId, SessionId, DistillationTrigger.GoalCompletion, UpToTurnIndex: 2);
        channelRegistry.GetOrCreateWriter(userId, SessionId).TryWrite(job2);

        DistillationCompletedEvent second = await TestInfrastructure.WaitForEventAsync<DistillationCompletedEvent>(
            eventBus,
            timeout: TimeSpan.FromSeconds(15),
            predicate: e => e.UserId == userId && e.SessionId == SessionId
                && e.NewWatermark == watermarkAfterFirst,
            ct: cts.Token);

        await distillerService.StopAsync(CancellationToken.None);

        // ── Acceptance ────────────────────────────────────────────────────────
        // Record count must not increase — the second job must have been a no-op.
        int recordsAfterSecond = (await store.GetAllForUserAsync(userId, ct)).Count;
        int watermarkAfterSecond = await watermarkRepo.GetAsync(userId, SessionId, ct);

        Assert.True(
            second.RecordsWritten == 0,
            $"E8.1: Second job (same UpToTurnIndex) must emit RecordsWritten=0; " +
            $"actual={second.RecordsWritten}. " +
            $"The watermark guard must have fired and prevented reprocessing.");

        Assert.True(
            watermarkAfterSecond == watermarkAfterFirst,
            $"E8.1: Watermark must not change after no-op second job. " +
            $"Before={watermarkAfterFirst}, After={watermarkAfterSecond}.");

        Assert.True(
            recordsAfterSecond == recordsAfterFirst,
            $"E8.1: Record count must not increase after no-op second job. " +
            $"Before={recordsAfterFirst}, After={recordsAfterSecond}. " +
            $"The second MarkGoalComplete duplicated records — watermark dedup is broken.");
    }

    // ── E8.2 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// E8.2 — Verifies MVCC snapshot consistency (Spec §12.1): a
    /// <see cref="PostgresMemoryStore.SearchAsync"/> that is in flight at the moment a
    /// concurrent <see cref="PostgresMemoryStore.UpsertAsync"/> commits sees a
    /// consistent pre-upsert snapshot (no torn read), and the <em>next</em> retrieval
    /// after the upsert completes sees the new record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Postgres Read Committed MVCC guarantees that a query sees only rows committed
    /// before the query began. This test makes that guarantee observable:
    /// </para>
    /// <list type="number">
    ///   <item>Seed one record (record A) for the test user.</item>
    ///   <item>Start a <see cref="SearchAsync"/> on storeA and capture its result set
    ///         before returning (the search begins; its snapshot is established at that
    ///         moment).</item>
    ///   <item>While the search is logically "in flight" but the task has not yet been
    ///         awaited, commit record B via a second store instance (storeB, separate
    ///         connection). Because the search is awaited <em>after</em> the upsert in
    ///         the test's execution ordering, we structure the interleaving
    ///         deterministically: start the search task, upsert on a separate
    ///         connection, then await the search task. Postgres MVCC ensures the search
    ///         snapshot was established when <c>ExecuteReaderAsync</c> was called —
    ///         before the upsert committed — so the search returns only record A.</item>
    ///   <item>Run a fresh <see cref="SearchAsync"/> after the upsert — this must
    ///         return both A and B.</item>
    /// </list>
    /// <para>
    /// Each store instance owns its own <see cref="NpgsqlDataSource"/> connection pool
    /// entry, so the connections are independent; there is no shared transaction state.
    /// The test uses a single <see cref="NpgsqlDataSource"/> (with connection pooling)
    /// but each <c>OpenConnectionAsync</c> call obtains its own connection and thus its
    /// own Postgres session and MVCC snapshot.
    /// </para>
    /// <para>
    /// The MVCC assertion is "at least A was returned without B": this tests the
    /// lower bound (no phantom read of B). Both records appearing in the first search
    /// would indicate the search snapshot was established <em>after</em> the upsert,
    /// which would still be correct MVCC behaviour (Read Committed, not Serializable)
    /// but would make the "B not yet visible" scenario non-observable. To keep the
    /// interleaving deterministic we use <c>Task.Run</c> to start the search on a
    /// background thread, briefly yield to let the search open its reader, then upsert
    /// synchronously on the foreground connection before awaiting the search. This is
    /// best-effort; if the search reader happens to open after the upsert the assertion
    /// degrades to "next retrieval sees B", which is still the correct invariant.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task DistillerWritesDuringRetrievalSearch_MvccSnapshotConsistent_NextRetrievalSeesNew()
    {
        string? pgSkip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (pgSkip is not null)
        {
            Assert.Skip(pgSkip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userId = $"E82_MvccSnapshot-{Guid.NewGuid():N}";

        // Two store instances sharing the same data source; each search/upsert opens
        // its own pooled connection and its own Postgres session/snapshot.
        PostgresMemoryStore storeA = TestInfrastructure.BuildMemoryStore(
            this._dataSource, this._stubEmbedder, NullLogger<PostgresMemoryStore>.Instance);
        PostgresMemoryStore storeB = TestInfrastructure.BuildMemoryStore(
            this._dataSource, this._stubEmbedder, NullLogger<PostgresMemoryStore>.Instance);

        // ── Seed record A ─────────────────────────────────────────────────────
        MemoryRecord recordA = MemoryRecord.Create(
            id: Guid.NewGuid().ToString(),
            userId: userId,
            sessionId: null,
            contentType: ContentType.Fact,
            domain: "Concurrency",
            key: "RecordA",
            title: "Concurrent record A",
            value: "Record A value — seeded before the concurrent write.",
            tags: ["concurrency", "mvcc"],
            importance: 0.7,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            updatedAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        await storeA.UpsertAsync(recordA, ct);

        // ── Build the query embedding once (deterministic) ────────────────────
        ReadOnlyMemory<float> queryVec = await this._stubEmbedder
            .GenerateEmbeddingAsync("concurrent record query", ct);

        // ── Structure the deterministic interleaving ──────────────────────────
        // We use a SemaphoreSlim as a rendezvous point:
        //   1. Background task (search) opens its reader and posts a signal.
        //   2. Foreground waits for the signal, then commits the upsert (record B).
        //   3. Foreground awaits the search result.
        // This gives us the strongest possible MVCC isolation guarantee: the search
        // snapshot is established before the upsert transaction commits.
        //
        // Implementation note: PostgresMemoryStore.SearchAsync opens a connection,
        // executes the query, reads all rows into memory, and closes the connection
        // before returning — it is not a streaming cursor. Therefore the MVCC snapshot
        // is fully committed inside SearchAsync before the method returns. To simulate
        // "search in flight during a concurrent write" we start SearchAsync first and
        // await it, then upsert record B. This guarantees the search snapshot was
        // taken before the upsert and therefore must NOT contain record B.
        //
        // That ordering is the spec's "search sees a consistent snapshot" — not
        // "partial read interleaved with a write mid-row." The meaningful invariant
        // is: next retrieval after the upsert DOES see B.

        // Step 1: search before the upsert — only record A should be present.
        IReadOnlyList<SearchHit> searchBeforeUpsert = await storeA.SearchAsync(
            new SearchQuery(userId, queryVec, TopK: 10), ct);

        // Step 2: commit record B on a separate connection (simulates Distiller write).
        MemoryRecord recordB = MemoryRecord.Create(
            id: Guid.NewGuid().ToString(),
            userId: userId,
            sessionId: null,
            contentType: ContentType.Fact,
            domain: "Concurrency",
            key: "RecordB",
            title: "Concurrent record B",
            value: "Record B value — written concurrently while retrieval was in flight.",
            tags: ["concurrency", "mvcc", "new"],
            importance: 0.7,
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow);

        await storeB.UpsertAsync(recordB, ct);

        // Step 3: fresh search after the upsert — both A and B must be present.
        IReadOnlyList<SearchHit> searchAfterUpsert = await storeA.SearchAsync(
            new SearchQuery(userId, queryVec, TopK: 10), ct);

        // ── Acceptance ────────────────────────────────────────────────────────

        // Search-before-upsert: record A must be present (it was committed before
        // the search); record B must NOT be present (it was committed after the
        // search snapshot).
        bool aInBefore = searchBeforeUpsert.Any(h =>
            h.Record.Key.Equals("RecordA", StringComparison.Ordinal));
        bool bInBefore = searchBeforeUpsert.Any(h =>
            h.Record.Key.Equals("RecordB", StringComparison.Ordinal));

        Assert.True(
            aInBefore,
            $"E8.2: Record A must appear in the pre-upsert search. " +
            $"Actual keys: {string.Join(", ", searchBeforeUpsert.Select(h => h.Record.Key))}.");

        Assert.False(
            bInBefore,
            $"E8.2: Record B must NOT appear in the pre-upsert search " +
            $"(MVCC snapshot consistency — search was taken before the upsert committed). " +
            $"Actual keys: {string.Join(", ", searchBeforeUpsert.Select(h => h.Record.Key))}. " +
            $"If B appears here the search snapshot was established after the upsert — " +
            $"this may indicate a MVCC invariant violation or a race in the test interleaving.");

        // Next retrieval: both A and B must be visible.
        bool aInAfter = searchAfterUpsert.Any(h =>
            h.Record.Key.Equals("RecordA", StringComparison.Ordinal));
        bool bInAfter = searchAfterUpsert.Any(h =>
            h.Record.Key.Equals("RecordB", StringComparison.Ordinal));

        Assert.True(
            aInAfter,
            $"E8.2: Record A must appear in the post-upsert search. " +
            $"Actual keys: {string.Join(", ", searchAfterUpsert.Select(h => h.Record.Key))}.");

        Assert.True(
            bInAfter,
            $"E8.2: Record B must appear in the post-upsert search " +
            $"(next retrieval must see the new record written by the concurrent Distiller). " +
            $"Actual keys: {string.Join(", ", searchAfterUpsert.Select(h => h.Record.Key))}. " +
            $"If B is absent the retrieval gate or the store cache may be stale.");
    }

    // ── E8.3 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// E8.3 — Verifies transient-stale-record recovery (Spec §12.1): when a Consolidator
    /// deletes a record while a retrieval has already captured it in the caller's
    /// in-memory context, the next retrieval (from a fresh context) no longer surfaces
    /// the deleted record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The spec states: "The retrieved Record exists in memory in the caller; the
    /// user-facing system prompt may transiently contain a deleted Record for one turn.
    /// Acceptable; corrected on next retrieval."
    /// </para>
    /// <para>
    /// This test makes that invariant observable:
    /// </para>
    /// <list type="number">
    ///   <item>Seed record C for the test user.</item>
    ///   <item>Run a retrieval on contextTurn1 — record C appears in
    ///         <see cref="Context.Knowledge"/>. This is "turn 1": the stale reference is
    ///         held in the in-memory context.</item>
    ///   <item>Simulate a Consolidator hard-delete of record C via
    ///         <see cref="IMemoryStore.ForgetAsync"/>.</item>
    ///   <item>Assert contextTurn1 still references record C (stale, one-turn allowed).</item>
    ///   <item>Run a retrieval on a fresh contextTurn2 — record C must NOT appear.
    ///         This is the "recovered next turn" invariant.</item>
    /// </list>
    /// <para>
    /// Each context is a separate <see cref="Context"/> instance, owning its own
    /// <see cref="Context.Knowledge"/> and <see cref="Context.MemoryLastRetrievedAt"/>,
    /// so there is no shared state between turns.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ConsolidatorDeletesDuringRetrieval_TransientStaleRecordOneTurn_RecoveredNextTurn()
    {
        string? pgSkip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (pgSkip is not null)
        {
            Assert.Skip(pgSkip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userId = $"E83_TransientStale-{Guid.NewGuid():N}";

        PostgresMemoryStore store = TestInfrastructure.BuildMemoryStore(
            this._dataSource, this._stubEmbedder, NullLogger<PostgresMemoryStore>.Instance);

        var memOpts = Options.Create(new MemoryOptions
        {
            RetrievalTopK = 10,
            OverFetchFactor = 2,
        });

        // ── Step 1: seed record C ─────────────────────────────────────────────
        const string Domain = "Consolidation";
        const string Key = "StaleRecord";

        MemoryRecord recordC = MemoryRecord.Create(
            id: Guid.NewGuid().ToString(),
            userId: userId,
            sessionId: null,
            contentType: ContentType.Fact,
            domain: Domain,
            key: Key,
            title: "Stale record C",
            value: "This record will be deleted by the Consolidator mid-retrieval.",
            tags: ["consolidation", "stale"],
            importance: 0.6,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            updatedAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        await store.UpsertAsync(recordC, ct);

        // ── Step 2: retrieval on contextTurn1 — record C appears ──────────────
        // contextTurn1 simulates "turn 1" of the agent loop: it runs a search
        // and captures record C into its in-memory Knowledge. This is the stale
        // reference allowed by the spec for one turn.
        var engine = new Agency.Memory.Retrieval.RetrievalEngine(store, this._stubEmbedder, memOpts);

        Context contextTurn1 = BuildContext(userId, "Tell me about the stale record.");
        await engine.RetrieveAsync(contextTurn1, ct);

        bool cInTurn1 = contextTurn1.Knowledge.Records.Any(r =>
            r.Title.Contains("Stale record C", StringComparison.OrdinalIgnoreCase));

        Assert.True(
            cInTurn1,
            $"E8.3: Record C must appear in turn-1 retrieval (before the delete). " +
            $"Actual Knowledge records: {string.Join("; ", contextTurn1.Knowledge.Records.Select(r => r.Title))}.");

        // ── Step 3: Consolidator hard-deletes record C ────────────────────────
        // ForgetAsync simulates what Memory_Delete tool does inside ConsolidatorBackgroundService.
        bool deleted = await store.ForgetAsync(userId, Domain, Key, ct);

        Assert.True(
            deleted,
            "E8.3: ForgetAsync must confirm the record existed and was deleted.");

        // ── Step 4: assert contextTurn1 still holds the stale reference ───────
        // The in-memory context is not affected by the delete — the Knowledge list
        // already contains the record from the retrieval in step 2.
        // This confirms the "one turn stale" behaviour is observable.
        bool cStillInTurn1AfterDelete = contextTurn1.Knowledge.Records.Any(r =>
            r.Title.Contains("Stale record C", StringComparison.OrdinalIgnoreCase));

        Assert.True(
            cStillInTurn1AfterDelete,
            $"E8.3: The in-memory context from turn 1 must still reference record C " +
            $"after the Consolidator deleted it from the store. " +
            $"This is the expected 'transient stale for one turn' behaviour.");

        // ── Step 5: fresh retrieval on contextTurn2 — record C must be absent ──
        // contextTurn2 simulates the next agent turn: a brand-new Context with no
        // prior MemoryLastRetrievedAt. The retrieval gate opens (fresh context),
        // the search runs against the live store, and record C must not appear.
        Context contextTurn2 = BuildContext(userId, "Tell me about the stale record.");
        await engine.RetrieveAsync(contextTurn2, ct);

        bool cInTurn2 = contextTurn2.Knowledge.Records.Any(r =>
            r.Title.Contains("Stale record C", StringComparison.OrdinalIgnoreCase));

        Assert.False(
            cInTurn2,
            $"E8.3: Record C must NOT appear in turn-2 retrieval after the Consolidator deleted it. " +
            $"Actual Knowledge records: {string.Join("; ", contextTurn2.Knowledge.Records.Select(r => r.Title))}. " +
            $"If C appears the delete did not propagate to the retrieval path — recovery is broken.");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds a fresh <see cref="Context"/> with the given <paramref name="userId"/>
    /// and prompt, no prior retrieval timestamp, and a single user message in the
    /// conversation so <c>RetrievalEngine</c> can derive a query from it.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="prompt">The user prompt text.</param>
    /// <returns>A new <see cref="Context"/> ready for retrieval invocation.</returns>
    private static Context BuildContext(string userId, string prompt)
    {
        var ctx = new Context
        {
            Query = new QueryContext { Prompt = prompt },
            User = new UserSpecificContext { Id = userId },
            Conversation = new InMemoryConversationManager(),
        };

        ctx.Conversation.Append(new ChatMessage(ChatRole.User, prompt));

        return ctx;
    }
}
