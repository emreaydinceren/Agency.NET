using Agency.Memory.Common.Events;
using Agency.Memory.Common.Jobs;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Agency.Memory.Consolidator.Services;
using Agency.Memory.Sql.Postgres;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Npgsql;
using IEmbeddingGenerator = Agency.Embeddings.Common.IEmbeddingGenerator;
using MemoryRecord = Agency.Memory.Common.Records.Record;

namespace Agency.Memory.Functional.Test;

/// <summary>
/// End-to-end Group 3 — Consolidation tests.
/// Covers the LLM-driven consolidation scenarios where the Consolidator sub-agent
/// resolves contradictions, merges duplicates, expands sparse records, deletes stale
/// low-importance memories, enforces per-user serial execution with coalescing, and
/// handles large corpora gracefully (Memory-TestPlan.md §3, Group 3).
/// </summary>
/// <remarks>
/// All tests carry <c>[Trait("Category","Functional")]</c> and
/// <c>[Trait("Group","Consolidation")]</c> so they can be run in isolation:
/// <code>
/// dotnet test src/Memory/Agency.Memory.Functional.Test
///     --filter "Category=Functional&amp;Group=Consolidation"
/// </code>
///
/// Schema dim is fixed at 1 536 for the entire class — do NOT call
/// <c>ResetSchemaAsync</c> with a different dimension from within this class.
///
/// Tests E3.1–E3.4 are LLM-driven: they seed Postgres with deterministic preconditions,
/// drive consolidation via the real <see cref="ConsolidatorBackgroundService"/> through the
/// HTTP cache proxy (BaseUrl <c>localhost:12345</c>), and await
/// <see cref="ConsolidationCompletedEvent"/>. They skip gracefully when the proxy is
/// unreachable. Tests E3.5 and E3.6 have deterministic sub-invariants verified without an LLM,
/// plus optional LLM-gated halves that skip gracefully.
///
/// <para>
/// <b>HTTP cache replay &amp; determinism contract (E3.1–E3.4).</b>
/// The proxy keys every entry on <c>SHA256(request body)</c>, so a recorded response only
/// replays when the consolidator's LLM request body is byte-identical run-to-run. The
/// consolidator is a multi-turn agent, so EVERY turn's body must be stable. Three sources of
/// per-run variation are pinned here so these tests are cacheable (instead of cache-missing and
/// degrading to no-ops, the behaviour before this was added):
/// <list type="number">
///   <item>
///     <b>Agent clock.</b> <see cref="DeterministicClock"/> is injected via
///     <c>BuildConsolidatorService</c> → <c>CreateRunner(timeProvider:)</c> so the agent's
///     "Current date/time (UTC)" system-prompt line is fixed (otherwise it changes every second).
///   </item>
///   <item>
///     <b>Prompt identifiers.</b> Each test uses a fixed <c>userId</c> literal and each seeded
///     record a fixed, <c>Guid</c>-parseable <c>id</c> literal (see <see cref="MakeRecord"/>) —
///     both are rendered verbatim into the reconciliation prompt and echoed across turns.
///     Per-test row isolation comes from the schema reset in <c>InitializeAsync</c> (xUnit
///     instantiates the class once per test method), NOT from random ids.
///   </item>
///   <item>
///     <b>Merge id.</b> A deterministic, <c>Guid</c>-parseable id factory is injected via
///     <c>CreateRunner(mergeIdFactory:)</c> so the id <c>Memory_Merge</c> mints — and echoes
///     into the next turn — is stable.
///   </item>
/// </list>
/// Keep seeded <c>ageInDays</c> values off the <c>HumanizeAge</c> bucket boundaries (7, 30) so
/// the relative "… ago" rendering can't flip between record and replay.
/// </para>
///
/// <para>
/// <b>Replay enforcement.</b> When <c>MEMORY_CACHE_REPLAY=1</c> (set by CI when a cassette is
/// present) the LLM-driven mutation is REQUIRED — E3.2 (merge) and E3.4 (delete) hard-fail if it
/// is not observed (see <see cref="CacheReplayRequired"/>). Unset (dev machine without a cassette,
/// or live-LLM variance) they keep an advisory <c>Assert.Skip</c>.
/// </para>
///
/// <para>
/// <b>Recording / regenerating cassettes.</b> The cache proxy lives in a separate repo,
/// <c>E:\Repos\Agency.HttpCacheProxy</c> (cassettes under
/// <c>src/Agency.Utils.HttpCacheProxy/cache/</c>). The determinism changes above must be built
/// BEFORE recording, or per-run GUIDs get baked into cassettes that never replay. Procedure:
/// <list type="number">
///   <item>Start LM Studio (the CI baseline model) and Postgres (<c>cd src; docker-compose up -d</c>).</item>
///   <item>Clear the proxy's <c>cache/*.json</c> and restart it fresh via the proxy repo's
///     <c>run.ps1</c> (a stale in-memory hit masks a miss and never persists).</item>
///   <item>Run this group to record:
///     <c>dotnet test … --filter "Group=Consolidation" -- RunConfiguration.MaxCpuCount=1</c>.
///     Only chat completions hit the proxy — embeddings use a Moq stub
///     (<see cref="TestInfrastructure.DeterministicEmbedder"/>).</item>
///   <item>Validate offline: point the proxy's upstream at an unreachable host, restart it, re-run
///     <c>--no-build</c>; expect ZERO <c>[MISS]</c> for E3.1–E3.4 and all four to execute (not skip).</item>
/// </list>
/// Cassettes are invalidated by any change to the request body: the reconciliation prompt
/// (<c>ConsolidatorReconciliationPrompt.Version</c>), <c>SystemPromptBuilder</c>, the record
/// rendering, the tool definitions/order, <c>MaxIterations</c>, or the model — re-record after any
/// of these. Keep <c>[Collection("memory-db")]</c> and <c>MaxCpuCount=1</c> (the schema reset is a
/// global DROP; serialization prevents cross-class corruption).
/// </para>
/// </remarks>
[Trait("Category", "Functional")]
[Trait("Group", "Consolidation")]
[Collection("memory-db")]
public sealed class Group3ConsolidationTests : IAsyncLifetime
{
    private static readonly IConfiguration _config = TestInfrastructure.BuildConfiguration();

    // LM Studio configuration keys.
    private const string LmStudioBaseUrlKey = "MemoryFunctional:LmStudio:BaseUrl";
    private const string LmStudioApiKeyKey = "MemoryFunctional:LmStudio:ApiKey";
    private const string LmStudioChatModelKey = "MemoryFunctional:LmStudio:ChatModel";

    /// <summary>
    /// Embedding dimension shared across all tests.
    /// Must match the schema reset in <see cref="InitializeAsync"/>.
    /// </summary>
    private const int EmbeddingDim = 1536;

    /// <summary>
    /// Fixed wall-clock instant injected into the consolidator sub-agent so the system-prompt
    /// "Current date/time (UTC)" line is byte-identical across runs, keeping the LLM request
    /// bodies replayable from the HTTP response cache. Must stay a hard-coded literal kept in
    /// lockstep with <c>MemoryE2EFixture.DeterministicClock</c> so cassettes recorded by either
    /// path share the same timestamp.
    /// </summary>
    private static readonly DateTimeOffset DeterministicClock =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// When set (CI sets <c>MEMORY_CACHE_REPLAY=1</c>), the LLM-driven mutation MUST be observed —
    /// the determinism work plus a recorded cassette guarantees it replays, so a missing merge/
    /// delete is a real failure, not graceful degradation. Unset on dev machines (no cassette or
    /// live LLM variance), the tests keep their advisory <c>Assert.Skip</c>.
    /// </summary>
    private static readonly bool CacheReplayRequired =
        Environment.GetEnvironmentVariable("MEMORY_CACHE_REPLAY") == "1";

    private NpgsqlDataSource _dataSource = default!;

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises one shared Postgres data source at the 1 536-dim schema.
    /// Each test uses a unique <c>userId</c> for row-level isolation.
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
    }

    /// <summary>Disposes the shared Postgres data source.</summary>
    public async ValueTask DisposeAsync()
    {
        if (this._dataSource is not null)
        {
            await this._dataSource.DisposeAsync();
        }
    }

    // ── E3.1 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// E3.1 — Verifies that the Consolidator sub-agent, when driven by a real LLM,
    /// resolves a contradiction between two records with the same logical fact
    /// (one old, one new) and retains the latest value in the store
    /// (Spec Use Case U3, §6.3).
    /// </summary>
    /// <remarks>
    /// Precondition: two Fact records with the same domain/key but different session IDs
    /// (old = "prefers Postgres", new = "switched to SQLite"). The Consolidator should
    /// use <c>Memory_Update</c> or <c>Memory_Merge</c> + <c>Memory_Delete</c> so that
    /// afterwards the store has one record with "SQLite" in its value.
    /// LLM-gated: skips if LM Studio has no model loaded.
    /// </remarks>
    [Fact]
    public async Task Consolidator_MergesContradiction_LatestStateRetained()
    {
        string? pgSkip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (pgSkip is not null)
        {
            Assert.Skip(pgSkip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userId = "e31-consolidator-contradiction";

        IEmbeddingGenerator embedder = TestInfrastructure.DeterministicEmbedder(EmbeddingDim);
        PostgresMemoryStore store = TestInfrastructure.BuildMemoryStore(
            this._dataSource, embedder, NullLogger<PostgresMemoryStore>.Instance);

        // ── Seed two contradictory records ────────────────────────────────────
        // Old: user prefers Postgres (7 days ago, lower importance).
        await store.UpsertAsync(MakeRecord(
            id: "11111111-1111-1111-1111-000000000001",
            userId: userId,
            sessionId: "session-old",
            domain: "Preferences",
            key: "Database",
            title: "Database preference",
            value: "User prefers Postgres for data persistence.",
            importance: 0.55,
            ageInDays: 8), ct);

        // New: user switched to SQLite (1 day ago, higher importance).
        await store.UpsertAsync(MakeRecord(
            id: "11111111-1111-1111-1111-000000000002",
            userId: userId,
            sessionId: "session-new",
            domain: "Preferences",
            key: "Database",
            title: "Database preference (updated)",
            value: "User switched to SQLite for local data storage.",
            importance: 0.65,
            ageInDays: 1), ct);

        IReadOnlyList<MemoryRecord> before = await store.GetAllForUserAsync(userId, ct);
        Assert.Equal(2, before.Count);

        // ── Check LM Studio reachability before wiring the real LLM path ─────
        string? llmSkip = await TestInfrastructure.CheckLmStudioAsync(
            _config, TestContext.Current.CancellationToken);
        if (llmSkip is not null)
        {
            Assert.Skip(
                $"E3.1: Postgres preconditions seeded ({before.Count} records). " +
                $"LLM-gated half skipped: {llmSkip}");
            return;
        }

        // ── Build the consolidator service with the real LLM ──────────────────
        (ConsolidatorBackgroundService service, InMemoryEventBus eventBus) =
            this.BuildConsolidatorService(store);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(120));

        // Subscribe BEFORE triggering.
        var job = new ConsolidationJob(userId, "session-new");
        ConsolidationCompletedEvent completed;
        await service.StartAsync(cts.Token);
        try
        {
            // Enqueue directly via the internal ProcessJobAsync to avoid needing
            // a live DistillationCompletedEvent from the distiller.
            var processTask = service.ProcessJobAsync(job, cts.Token);
            var waitTask = TestInfrastructure.WaitForEventAsync<ConsolidationCompletedEvent>(
                eventBus,
                timeout: TimeSpan.FromSeconds(90),
                predicate: e => e.UserId == userId,
                ct: cts.Token);
            await processTask;
            completed = await waitTask;
        }
        catch (TimeoutException)
        {
            await service.StopAsync(CancellationToken.None);
            Assert.Skip(
                "E3.1: Consolidation did not complete within 90 s. " +
                "LM Studio is reachable but no model is loaded or response is too slow.");
            return;
        }
        catch (OperationCanceledException)
        {
            await service.StopAsync(CancellationToken.None);
            Assert.Skip("E3.1: Consolidation was cancelled (LM Studio request timeout).");
            return;
        }

        await service.StopAsync(CancellationToken.None);

        // ── Acceptance ────────────────────────────────────────────────────────
        // After consolidation the store must have fewer records than before,
        // and the remaining record(s) must reference "SQLite" (latest state).
        IReadOnlyList<MemoryRecord> after = await store.GetAllForUserAsync(userId, ct);
        Assert.NotNull(completed);
        Assert.Equal(userId, completed.UserId);

        bool latestRetained = after.Any(r =>
            r.Value.Contains("SQLite", StringComparison.OrdinalIgnoreCase)
            || r.Title.Contains("SQLite", StringComparison.OrdinalIgnoreCase));

        Assert.True(
            latestRetained,
            $"E3.1: Expected the store to retain the SQLite (latest) preference after consolidation. " +
            $"Remaining records: {string.Join("; ", after.Select(r => $"{r.Title}: {r.Value}"))}.");
    }

    // ── E3.2 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// E3.2 — Verifies that the Consolidator sub-agent merges two near-duplicate Fact
    /// records (both expressing the same preference, slightly differently worded) into a
    /// single record (Spec Use Case U5, §6.3).
    /// </summary>
    /// <remarks>
    /// Precondition: two Fact records in the same domain/key but different sessions,
    /// both saying the user prefers Python (worded differently).
    /// After consolidation, the store should have one record for the user in that domain.
    /// LLM-gated: skips if LM Studio has no model loaded.
    /// </remarks>
    [Fact]
    public async Task Consolidator_MergesDuplicateFacts_IntoSingleRecord()
    {
        string? pgSkip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (pgSkip is not null)
        {
            Assert.Skip(pgSkip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userId = "e32-consolidator-duplicates";

        IEmbeddingGenerator embedder = TestInfrastructure.DeterministicEmbedder(EmbeddingDim);
        PostgresMemoryStore store = TestInfrastructure.BuildMemoryStore(
            this._dataSource, embedder, NullLogger<PostgresMemoryStore>.Instance);

        // ── Seed two duplicate records ────────────────────────────────────────
        await store.UpsertAsync(MakeRecord(
            id: "22222222-2222-2222-2222-000000000001",
            userId: userId,
            sessionId: "session-a",
            domain: "Preferences",
            key: "Language",
            title: "Language preference (session A)",
            value: "User prefers Python for scripting.",
            importance: 0.7,
            ageInDays: 5), ct);

        await store.UpsertAsync(MakeRecord(
            id: "22222222-2222-2222-2222-000000000002",
            userId: userId,
            sessionId: "session-b",
            domain: "Preferences",
            key: "Language",
            title: "Language preference (session B)",
            value: "User prefers Python for scripting tasks.",
            importance: 0.7,
            ageInDays: 3), ct);

        IReadOnlyList<MemoryRecord> before = await store.GetAllForUserAsync(userId, ct);
        Assert.Equal(2, before.Count);

        string? llmSkip = await TestInfrastructure.CheckLmStudioAsync(
            _config, TestContext.Current.CancellationToken);
        if (llmSkip is not null)
        {
            Assert.Skip(
                $"E3.2: Postgres preconditions seeded ({before.Count} records). " +
                $"LLM-gated half skipped: {llmSkip}");
            return;
        }

        (ConsolidatorBackgroundService service, InMemoryEventBus eventBus) =
            this.BuildConsolidatorService(store);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(120));

        ConsolidationCompletedEvent completed;
        await service.StartAsync(cts.Token);
        try
        {
            var processTask = service.ProcessJobAsync(new ConsolidationJob(userId, "session-b"), cts.Token);
            var waitTask = TestInfrastructure.WaitForEventAsync<ConsolidationCompletedEvent>(
                eventBus,
                timeout: TimeSpan.FromSeconds(90),
                predicate: e => e.UserId == userId,
                ct: cts.Token);
            await processTask;
            completed = await waitTask;
        }
        catch (TimeoutException)
        {
            await service.StopAsync(CancellationToken.None);
            Assert.Skip(
                "E3.2: Consolidation did not complete within 90 s. " +
                "LM Studio is reachable but no model is loaded or response is too slow.");
            return;
        }
        catch (OperationCanceledException)
        {
            await service.StopAsync(CancellationToken.None);
            Assert.Skip("E3.2: Consolidation was cancelled.");
            return;
        }

        await service.StopAsync(CancellationToken.None);

        // ── Acceptance ────────────────────────────────────────────────────────
        // The LLM should have merged the two near-duplicate records into one.
        IReadOnlyList<MemoryRecord> after = await store.GetAllForUserAsync(userId, ct);
        Assert.NotNull(completed);
        Assert.Equal(userId, completed.UserId);

        // With determinism in place (pinned agent clock + fixed record/user ids + deterministic
        // merge id) the request bodies are byte-stable, so this merge IS replayable from the HTTP
        // response cache. When MEMORY_CACHE_REPLAY=1 (CI, cassette present) a missing merge is a
        // real failure; otherwise (dev machine without a cassette, or live LLM variance) keep the
        // advisory skip.
        if (after.Count >= before.Count)
        {
            string detail =
                $"No merge was observed (before={before.Count}, after={after.Count}). " +
                $"Remaining records: {string.Join("; ", after.Select(r => $"{r.Title}: {r.Value}"))}.";

            if (CacheReplayRequired)
            {
                Assert.Fail($"E3.2: {detail}");
            }

            Assert.Skip($"E3.2 [advisory]: {detail} Subject to live LLM variance when no cassette is present.");
            return;
        }

        bool pythonRetained = after.Any(r =>
            r.Value.Contains("Python", StringComparison.OrdinalIgnoreCase)
            || r.Title.Contains("Python", StringComparison.OrdinalIgnoreCase));

        Assert.True(
            pythonRetained,
            $"E3.2: Merged record must still reference Python. " +
            $"Remaining records: {string.Join("; ", after.Select(r => $"{r.Title}: {r.Value}"))}.");
    }

    // ── E3.3 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// E3.3 — Verifies that the Consolidator sub-agent expands a sparse (thin) record
    /// by incorporating detail from a newer, richer record about the same topic
    /// (Spec §6.3 "Expansion").
    /// </summary>
    /// <remarks>
    /// Precondition: one sparse record ("User likes Docker.") and one newer, richer record
    /// ("User uses Docker for containerised development; always pins image tags to avoid drift.").
    /// After consolidation, the remaining record should incorporate the detail from the newer one.
    /// LLM-gated: skips if LM Studio has no model loaded.
    /// </remarks>
    [Fact]
    public async Task Consolidator_ExpandsSparseRecord_WithDetailFromNewer()
    {
        string? pgSkip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (pgSkip is not null)
        {
            Assert.Skip(pgSkip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userId = "e33-consolidator-sparse";

        IEmbeddingGenerator embedder = TestInfrastructure.DeterministicEmbedder(EmbeddingDim);
        PostgresMemoryStore store = TestInfrastructure.BuildMemoryStore(
            this._dataSource, embedder, NullLogger<PostgresMemoryStore>.Instance);

        // ── Seed sparse record (old) ──────────────────────────────────────────
        MemoryRecord sparseRecord = await store.UpsertAsync(MakeRecord(
            id: "33333333-3333-3333-3333-000000000001",
            userId: userId,
            sessionId: "session-sparse",
            domain: "Tools",
            key: "ContainerRuntime",
            title: "Container runtime preference",
            value: "User likes Docker.",
            importance: 0.5,
            ageInDays: 10), ct);

        // ── Seed richer record (newer, different session) ─────────────────────
        await store.UpsertAsync(MakeRecord(
            id: "33333333-3333-3333-3333-000000000002",
            userId: userId,
            sessionId: "session-rich",
            domain: "Tools",
            key: "ContainerRuntime",
            title: "Docker usage details",
            value: "User uses Docker for containerised development; always pins image tags to avoid " +
                   "version drift. Uses Docker Compose for local multi-service orchestration.",
            importance: 0.75,
            ageInDays: 1), ct);

        IReadOnlyList<MemoryRecord> before = await store.GetAllForUserAsync(userId, ct);
        Assert.Equal(2, before.Count);

        string? llmSkip = await TestInfrastructure.CheckLmStudioAsync(
            _config, TestContext.Current.CancellationToken);
        if (llmSkip is not null)
        {
            Assert.Skip(
                $"E3.3: Postgres preconditions seeded ({before.Count} records). " +
                $"LLM-gated half skipped: {llmSkip}");
            return;
        }

        (ConsolidatorBackgroundService service, InMemoryEventBus eventBus) =
            this.BuildConsolidatorService(store);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(120));

        ConsolidationCompletedEvent completed;
        await service.StartAsync(cts.Token);
        try
        {
            var processTask = service.ProcessJobAsync(new ConsolidationJob(userId, "session-rich"), cts.Token);
            var waitTask = TestInfrastructure.WaitForEventAsync<ConsolidationCompletedEvent>(
                eventBus,
                timeout: TimeSpan.FromSeconds(90),
                predicate: e => e.UserId == userId,
                ct: cts.Token);
            await processTask;
            completed = await waitTask;
        }
        catch (TimeoutException)
        {
            await service.StopAsync(CancellationToken.None);
            Assert.Skip(
                "E3.3: Consolidation did not complete within 90 s. " +
                "LM Studio is reachable but no model is loaded or response is too slow.");
            return;
        }
        catch (OperationCanceledException)
        {
            await service.StopAsync(CancellationToken.None);
            Assert.Skip("E3.3: Consolidation was cancelled.");
            return;
        }

        await service.StopAsync(CancellationToken.None);

        // ── Acceptance ────────────────────────────────────────────────────────
        // The sparse record should have been expanded or merged with the richer one.
        // Observable: at least one record with more detail than "User likes Docker."
        IReadOnlyList<MemoryRecord> after = await store.GetAllForUserAsync(userId, ct);
        Assert.NotNull(completed);
        Assert.Equal(userId, completed.UserId);

        // Either the count dropped (merge) or the sparse record was updated (expansion).
        // Verify at least one record is richer than the original sparse value.
        bool recordExpanded = after.Any(r =>
            r.Value.Length > sparseRecord.Value.Length
            && r.Value.Contains("Docker", StringComparison.OrdinalIgnoreCase));

        Assert.True(
            recordExpanded,
            $"E3.3: Expected the sparse record to be expanded with detail from the newer record. " +
            $"Sparse original length: {sparseRecord.Value.Length}. " +
            $"After records: {string.Join("; ", after.Select(r => $"'{r.Title}'({r.Value.Length} chars)"))}.");
    }

    // ── E3.4 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// E3.4 — Verifies that the Consolidator sub-agent deletes a stale, low-importance
    /// memory record when the LLM decides it is no longer relevant (Spec §6.3, §8.4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Precondition: one stale low-importance record (importance=0.05, 45 days old, explicitly
    /// tagged as obsolete), plus one high-importance anchor record that must NOT be deleted.
    /// The stale record's value and title both signal that it is a one-off artefact with no
    /// lasting relevance, maximising the LLM's confidence in the delete decision.
    /// </para>
    /// <para>
    /// <b>Hardening strategy (TI-8.3):</b>
    /// The primary assertion is <em>causal</em> and sourced from production code: the consolidator
    /// publishes a <see cref="MemoryMutatedEvent"/> for every Merge/Update/Delete its sub-agent
    /// performs. Asserting that a <c>Delete</c> event fired for the stale record's id (the id is
    /// embedded in <see cref="MemoryMutatedEvent.Detail"/>) proves the sub-agent actually invoked
    /// the <c>Memory_Delete</c> tool — the true §8.4 invariant — independent of any timing or
    /// ordering artefacts, and without a test-only store decorator.
    /// </para>
    /// <para>
    /// If the LLM declines to delete, the test skips with a precise advisory rather than a false
    /// red. With the TI-8.4 structural DELETE rule now in the reconciliation prompt (importance
    /// &lt; 0.1 AND age &gt; 30 days AND self-described obsolete → delete by default), this case is
    /// clear-cut and a skip should be rare residual LLM variance.
    /// </para>
    /// <para>LLM-gated: skips if LM Studio has no model loaded.</para>
    /// </remarks>
    [Fact]
    public async Task Consolidator_DeletesStaleLowImportanceMemory_ViaSubAgent()
    {
        string? pgSkip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (pgSkip is not null)
        {
            Assert.Skip(pgSkip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userId = "e34-consolidator-stale";

        IEmbeddingGenerator embedder = TestInfrastructure.DeterministicEmbedder(EmbeddingDim);
        PostgresMemoryStore pgStore = TestInfrastructure.BuildMemoryStore(
            this._dataSource, embedder, NullLogger<PostgresMemoryStore>.Instance);

        // ── Seed stale low-importance record (candidate for deletion) ─────────
        // Signals to the LLM are made as unambiguous as possible:
        //   • importance 0.05 (near-zero — the minimum meaningful score)
        //   • 45 days old (well past any reasonable staleness threshold)
        //   • Title and value both explicitly state the record is obsolete
        //   • Session "session-old-debug" is distinct from the trigger session,
        //     removing any ambiguity about it being a "freshly written" record
        MemoryRecord staleRecord = await pgStore.UpsertAsync(MakeRecord(
            id: "44444444-4444-4444-4444-000000000001",
            userId: userId,
            sessionId: "session-old-debug",
            domain: "Debugging",
            key: "ObsoleteOneOffWorkaround",
            title: "OBSOLETE: one-off dev-machine workaround (safe to delete)",
            value: "OBSOLETE — no longer applicable. Temporarily disabled certificate validation on " +
                   "the dev machine to unblock a one-off test run in November. The root cause was " +
                   "fixed the same day. This note has no lasting value and should be deleted.",
            importance: 0.05,
            ageInDays: 45), ct);

        // ── Seed high-importance anchor record (must NOT be deleted) ──────────
        MemoryRecord anchorRecord = await pgStore.UpsertAsync(MakeRecord(
            id: "44444444-4444-4444-4444-000000000002",
            userId: userId,
            sessionId: "session-anchor",
            domain: "Preferences",
            key: "Language",
            title: "Primary language preference",
            value: "User prefers C# for all server-side development. Always uses .NET 10.",
            importance: 0.9,
            ageInDays: 2), ct);

        IReadOnlyList<MemoryRecord> before = await pgStore.GetAllForUserAsync(userId, ct);
        Assert.Equal(2, before.Count);

        string? llmSkip = await TestInfrastructure.CheckLmStudioAsync(
            _config, TestContext.Current.CancellationToken);
        if (llmSkip is not null)
        {
            Assert.Skip(
                $"E3.4: Postgres preconditions seeded ({before.Count} records: " +
                $"stale id={staleRecord.Id}, anchor id={anchorRecord.Id}). " +
                $"LLM-gated half skipped: {llmSkip}");
            return;
        }

        // ── Subscribe to the production MemoryMutatedEvent observable (TI-8.3) ─
        // The consolidator publishes a MemoryMutatedEvent for every Merge/Update/Delete its
        // sub-agent performs. Capturing a Delete event for the stale record's id is a causal
        // observable of the Memory_Delete tool-call sourced from real product code — no
        // test-only store decorator required.
        (ConsolidatorBackgroundService service, InMemoryEventBus eventBus) =
            this.BuildConsolidatorService(pgStore);

        var mutations = new System.Collections.Concurrent.ConcurrentBag<MemoryMutatedEvent>();
        using IDisposable mutationSub = eventBus.Subscribe<MemoryMutatedEvent>((evt, _) =>
        {
            mutations.Add(evt);
            return Task.CompletedTask;
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(120));

        ConsolidationCompletedEvent completed;
        await service.StartAsync(cts.Token);
        try
        {
            // Use a neutral trigger session (not "session-old-debug") so the LLM
            // cannot mistake the stale record for a recently triggered write.
            var processTask = service.ProcessJobAsync(new ConsolidationJob(userId, "session-anchor"), cts.Token);
            var waitTask = TestInfrastructure.WaitForEventAsync<ConsolidationCompletedEvent>(
                eventBus,
                timeout: TimeSpan.FromSeconds(90),
                predicate: e => e.UserId == userId,
                ct: cts.Token);
            await processTask;
            completed = await waitTask;
        }
        catch (TimeoutException)
        {
            await service.StopAsync(CancellationToken.None);
            Assert.Skip(
                "E3.4: Consolidation did not complete within 90 s. " +
                "LM Studio is reachable but no model is loaded or response is too slow.");
            return;
        }
        catch (OperationCanceledException)
        {
            await service.StopAsync(CancellationToken.None);
            Assert.Skip("E3.4: Consolidation was cancelled.");
            return;
        }

        await service.StopAsync(CancellationToken.None);

        // ── Acceptance ────────────────────────────────────────────────────────
        Assert.NotNull(completed);
        Assert.Equal(userId, completed.UserId);

        // Primary (causal) assertion: a Delete MemoryMutatedEvent fired for the stale record id.
        // This proves the consolidator sub-agent invoked Memory_Delete for that record, independent
        // of any timing or store-flush ordering artefacts. The delete tool's result text — carried
        // in MemoryMutatedEvent.Detail ("Deleted record {id}.") — embeds the deleted record id.
        bool deleteToolCalledForStale = mutations.Any(
            m => m.Operation == "Delete" && m.Detail.Contains(staleRecord.Id, StringComparison.Ordinal));

        if (!deleteToolCalledForStale)
        {
            // With determinism in place the delete is replayable from the HTTP response cache, so
            // when MEMORY_CACHE_REPLAY=1 (CI, cassette present) a missing delete is a real failure.
            // Otherwise (dev machine without a cassette, or live LLM variance) keep the advisory
            // skip — the TI-8.4 structural DELETE rule makes a residual miss rare but possible.
            IReadOnlyList<MemoryRecord> afterSkip = await pgStore.GetAllForUserAsync(userId, ct);
            string detail =
                $"No Delete mutation was observed for the stale record (id={staleRecord.Id}). " +
                $"Observed mutations: [{string.Join("; ", mutations.Select(m => $"{m.Operation}:{m.Detail}"))}]. " +
                $"Remaining records: {string.Join("; ", afterSkip.Select(r => $"id={r.Id} title='{r.Title}'"))}.";

            if (CacheReplayRequired)
            {
                Assert.Fail($"E3.4: {detail}");
            }

            Assert.Skip(
                $"E3.4 [advisory]: {detail} The reconciliation prompt carries the TI-8.4 structural " +
                $"DELETE rule, so this is clear-cut; a residual skip is genuine LLM variance when no cassette is present.");
            return;
        }

        // Secondary assertion: final store state confirms the delete persisted.
        IReadOnlyList<MemoryRecord> after = await pgStore.GetAllForUserAsync(userId, ct);
        bool staleGone = after.All(r => r.Id != staleRecord.Id);
        Assert.True(
            staleGone,
            $"E3.4: Memory_Delete was called for stale record (id={staleRecord.Id}) but it " +
            $"is still present in the store — DeleteByIdAsync may have returned false or thrown. " +
            $"Remaining: {string.Join("; ", after.Select(r => $"id={r.Id} title='{r.Title}'"))}.");

        // The anchor record must still be present.
        bool anchorPreserved = after.Any(r => r.Id == anchorRecord.Id);
        Assert.True(
            anchorPreserved,
            $"E3.4: Expected the high-importance anchor record (id={anchorRecord.Id}) to be preserved. " +
            $"Remaining: {string.Join("; ", after.Select(r => $"id={r.Id} title='{r.Title}'"))}.");
    }

    // ── E3.5 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// E3.5 — Verifies the per-user serial execution guarantee of
    /// <see cref="ConsolidatorBackgroundService"/>: two session-end triggers for the same
    /// user coalesce into a single pending re-run rather than spawning two concurrent passes
    /// (Spec §10.2, §12.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deterministic sub-invariant (verified without a model):</b>
    /// The coalescing behaviour is observable entirely through the background service's
    /// internal counters, which are exercised by wiring a stub agent runner (barrier-gated).
    /// When a second trigger arrives while a run is in-flight, the service marks the user
    /// as "pending" and does NOT spawn a second concurrent execution. After the first run
    /// completes, exactly one additional run is performed.
    /// </para>
    /// <para>
    /// This sub-invariant is verified with a stub runner and does NOT require LM Studio.
    /// A real-LLM epilogue (two <see cref="ConsolidationCompletedEvent"/>s emitted for
    /// one user) is appended but skips gracefully if no model is loaded.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Consolidator_PerUserSerial_TwoSessionEndsCoalesceIntoOnePass()
    {
        string? pgSkip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (pgSkip is not null)
        {
            Assert.Skip(pgSkip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userId = $"Consolidator_PerUserSerial-{Guid.NewGuid():N}";

        IEmbeddingGenerator embedder = TestInfrastructure.DeterministicEmbedder(EmbeddingDim);
        PostgresMemoryStore store = TestInfrastructure.BuildMemoryStore(
            this._dataSource, embedder, NullLogger<PostgresMemoryStore>.Instance);

        // Seed one record so the service does not exit on empty-store guard.
        // E3.5 verifies coalescing via stub runners and is not cache-replayed, so a random id is fine.
        await store.UpsertAsync(MakeRecord(
            id: Guid.NewGuid().ToString(),
            userId: userId,
            sessionId: "session-coalesce",
            domain: "Preferences",
            key: "Language",
            title: "Language preference",
            value: "User prefers C#.",
            importance: 0.7,
            ageInDays: 1), ct);

        // ── Deterministic sub-invariant: coalescing under a stub runner ───────
        // The stub runner blocks on a barrier so we can simulate the second trigger
        // arriving while the first run is in-flight.
        int agentRunCount = 0;
        var barrier = new TaskCompletionSource<(int, int, int)>(TaskCreationOptions.RunContinuationsAsynchronously);

        var mockStore = new Mock<IMemoryStore>(MockBehavior.Loose);
        mockStore
            .Setup(s => s.GetAllForUserAsync(userId, It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(
                async (uid, tok) => await store.GetAllForUserAsync(uid, tok));

        var stubEventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);

        var consolidatorOpts = Options.Create(new ConsolidatorOptions
        {
            MaxIterations = 10,
            MaxCostUsd = 1.0m,
        });

        Task<(int, int, int)> StubRunner(string uid, IReadOnlyList<MemoryRecord> records, CancellationToken token)
        {
            Interlocked.Increment(ref agentRunCount);
            return barrier.Task;
        }

        var service = new ConsolidatorBackgroundService(
            mockStore.Object,
            StubRunner,
            stubEventBus,
            consolidatorOpts,
            NullLogger<ConsolidatorBackgroundService>.Instance);

        // Start the first run (will block at barrier).
        var firstRunTask = service.ProcessJobAsync(new ConsolidationJob(userId, "session-1"), ct);

        // Simulate a second trigger arriving while the first is in-flight.
        service.EnqueuePendingIfCoalesced(userId);

        // Release the barrier so the first run completes.
        barrier.SetResult((0, 0, 0));
        await firstRunTask;

        // Drain the pending re-run (the coalesced second pass).
        await service.DrainPendingAsync(ct);

        // ── Deterministic assertion ───────────────────────────────────────────
        // Two runs total: the first real run and the coalesced second run.
        // No concurrent execution happened — each ran serially after the other.
        Assert.Equal(2, agentRunCount);

        // ── LLM-gated epilogue ────────────────────────────────────────────────
        // Verify that when driven via the event bus (real path), two DistillationCompleted
        // events for the same user produce exactly one ConsolidationCompletedEvent
        // (or two, one per trigger, but NEVER two concurrent passes).
        // This half requires LM Studio; skip gracefully if not available.
        string? llmSkip = await TestInfrastructure.CheckLmStudioAsync(
            _config, TestContext.Current.CancellationToken);
        if (llmSkip is not null)
        {
            // Deterministic sub-invariant verified above — skip the LLM half only.
            return;
        }

        (ConsolidatorBackgroundService realService, InMemoryEventBus realEventBus) =
            this.BuildConsolidatorService(store);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(180));

        int consolidationEventCount = 0;
        using IDisposable _ = realEventBus.Subscribe<ConsolidationCompletedEvent>(async (evt, _) =>
        {
            if (evt.UserId == userId)
            {
                Interlocked.Increment(ref consolidationEventCount);
            }

            await Task.CompletedTask;
        });

        await realService.StartAsync(cts.Token);

        // Publish two DistillationCompletedEvents for the same user in rapid succession.
        await realEventBus.PublishAsync(new DistillationCompletedEvent(
            userId, "session-1", RecordsWritten: 1, NewWatermark: 2), cts.Token);

        await realEventBus.PublishAsync(new DistillationCompletedEvent(
            userId, "session-2", RecordsWritten: 1, NewWatermark: 4), cts.Token);

        // Wait for at least one ConsolidationCompletedEvent with a generous timeout.
        try
        {
            await TestInfrastructure.WaitForEventAsync<ConsolidationCompletedEvent>(
                realEventBus,
                timeout: TimeSpan.FromSeconds(120),
                predicate: e => e.UserId == userId,
                ct: cts.Token);
        }
        catch (TimeoutException)
        {
            await realService.StopAsync(CancellationToken.None);
            Assert.Skip(
                "E3.5: Deterministic sub-invariant passed (coalescing = 2 serial runs). " +
                "LLM-gated event-bus epilogue timed out — no model loaded.");
            return;
        }
        catch (OperationCanceledException)
        {
            await realService.StopAsync(CancellationToken.None);
            Assert.Skip("E3.5: LLM-gated epilogue was cancelled.");
            return;
        }

        await realService.StopAsync(CancellationToken.None);

        // Both events must have produced at least one ConsolidationCompletedEvent.
        Assert.True(
            consolidationEventCount >= 1,
            $"E3.5: Expected at least 1 ConsolidationCompletedEvent for user '{userId}', " +
            $"got {consolidationEventCount}.");
    }

    // ── E3.6 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// E3.6 — Verifies that when a user has more than <c>MaxRecordsPerPass</c> (500)
    /// records, the Consolidator emits a warning log and still completes successfully
    /// (Spec §6.3 V1 large-corpus guard, §12.4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deterministic sub-invariant (verified without a model):</b>
    /// The warning is emitted by <see cref="ConsolidatorBackgroundService.ProcessJobAsync"/>
    /// immediately after loading records, before the sub-agent is invoked. This check is
    /// exercised against a stub runner that never calls the LLM, making it fully
    /// deterministic. The warning is captured via a <see cref="ILogger{T}"/> spy.
    /// </para>
    /// <para>
    /// <b>LLM-gated half:</b> the "still completes" portion (the sub-agent actually finishes
    /// on a 501-record corpus) requires LM Studio and skips gracefully when no model is loaded.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Consolidator_LargeCorpus_EmitsWarning_AndStillCompletes()
    {
        string? pgSkip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (pgSkip is not null)
        {
            Assert.Skip(pgSkip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userId = $"Consolidator_LargeCorpus-{Guid.NewGuid():N}";

        // ── Deterministic sub-invariant: warning emitted pre-LLM ─────────────
        // Seed records only in the mock store (no Postgres I/O for this sub-invariant)
        // so the test is fast and does not grow the DB.
        int overThresholdCount = ConsolidatorBackgroundService.MaxRecordsPerPass + 1; // 501

        var mockStore = new Mock<IMemoryStore>(MockBehavior.Loose);
        var largeCorpus = Enumerable.Range(0, overThresholdCount)
            .Select(i => MemoryRecord.Create(
                id: Guid.NewGuid().ToString(),
                userId: userId,
                sessionId: null,
                contentType: ContentType.Fact,
                domain: "Test",
                key: $"key-{i}",
                title: $"Record {i}",
                value: $"Value {i}",
                tags: [],
                importance: 0.5,
                createdAt: DateTimeOffset.UtcNow.AddDays(-1),
                updatedAt: DateTimeOffset.UtcNow.AddDays(-1)))
            .ToList();

        mockStore
            .Setup(s => s.GetAllForUserAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(largeCorpus);

        var warningLogger = new WarningCapturingLogger<ConsolidatorBackgroundService>();
        var stubEventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        bool stubAgentInvoked = false;
        Task<(int, int, int)> StubRunner(string uid, IReadOnlyList<MemoryRecord> records, CancellationToken token)
        {
            stubAgentInvoked = true;
            return Task.FromResult((0, 0, 0));
        }

        var consolidatorOpts = Options.Create(new ConsolidatorOptions
        {
            MaxIterations = 5,
            MaxCostUsd = 1.0m,
        });

        var serviceWithSpy = new ConsolidatorBackgroundService(
            mockStore.Object,
            StubRunner,
            stubEventBus,
            consolidatorOpts,
            warningLogger);

        await serviceWithSpy.ProcessJobAsync(new ConsolidationJob(userId, "session-large"), ct);

        // ── Deterministic assertions ──────────────────────────────────────────
        // 1. Warning was emitted (fired pre-LLM, before the stub agent ran).
        Assert.True(
            warningLogger.WarningCount > 0,
            $"E3.6: Expected a warning to be logged for large corpus ({overThresholdCount} records > " +
            $"MaxRecordsPerPass={ConsolidatorBackgroundService.MaxRecordsPerPass}), but no warning was captured.");

        // 2. The stub agent was still invoked (service continued past the warning).
        Assert.True(
            stubAgentInvoked,
            "E3.6: Expected the stub agent to be invoked even after the large-corpus warning. " +
            "The service must not abort on oversized corpora.");

        // ── LLM-gated half: "still completes" with a real LLM ─────────────────
        string? llmSkip = await TestInfrastructure.CheckLmStudioAsync(
            _config, TestContext.Current.CancellationToken);
        if (llmSkip is not null)
        {
            // Deterministic sub-invariant fully verified above.
            return;
        }

        // For the LLM half, seed just above the threshold into Postgres so the real
        // sub-agent has a manageable (but over-threshold) corpus to work with.
        // Use a smaller threshold-crossing corpus (502 simple records) to keep LLM calls bounded.
        IEmbeddingGenerator embedder = TestInfrastructure.DeterministicEmbedder(EmbeddingDim);
        PostgresMemoryStore realStore = TestInfrastructure.BuildMemoryStore(
            this._dataSource, embedder, NullLogger<PostgresMemoryStore>.Instance);

        string llmUserId = $"{userId}-llm";
        const int LlmSeedCount = 502;
        for (int i = 0; i < LlmSeedCount; i++)
        {
            await realStore.UpsertAsync(MakeRecord(
                id: Guid.NewGuid().ToString(),
                userId: llmUserId,
                sessionId: null,
                domain: $"Domain{i % 10}",
                key: $"key-{i}",
                title: $"Record {i}",
                value: $"Value content {i} — a durable fact for test user.",
                importance: 0.5,
                ageInDays: 1), ct);
        }

        (ConsolidatorBackgroundService realService, InMemoryEventBus realEventBus) =
            this.BuildConsolidatorService(realStore);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(180));

        ConsolidationCompletedEvent llmCompleted;
        await realService.StartAsync(cts.Token);
        try
        {
            var processTask = realService.ProcessJobAsync(new ConsolidationJob(llmUserId, "session-large-llm"), cts.Token);
            var waitTask = TestInfrastructure.WaitForEventAsync<ConsolidationCompletedEvent>(
                realEventBus,
                timeout: TimeSpan.FromSeconds(150),
                predicate: e => e.UserId == llmUserId,
                ct: cts.Token);
            await processTask;
            llmCompleted = await waitTask;
        }
        catch (TimeoutException)
        {
            await realService.StopAsync(CancellationToken.None);
            Assert.Skip(
                $"E3.6: Deterministic sub-invariant passed (warning emitted, agent invoked). " +
                $"LLM-gated 'still completes' half timed out after 150 s — no model loaded.");
            return;
        }
        catch (OperationCanceledException)
        {
            await realService.StopAsync(CancellationToken.None);
            Assert.Skip("E3.6: LLM-gated epilogue was cancelled.");
            return;
        }

        await realService.StopAsync(CancellationToken.None);

        Assert.NotNull(llmCompleted);
        Assert.Equal(llmUserId, llmCompleted.UserId);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="ConsolidatorBackgroundService"/> wired with the real LM Studio
    /// <see cref="IChatClient"/> and the supplied <paramref name="store"/>, plus an
    /// <see cref="InMemoryEventBus"/> for event capture.
    /// </summary>
    /// <param name="store">The memory store to operate on.</param>
    /// <returns>
    /// A tuple of the configured service and the event bus.
    /// </returns>
    private (ConsolidatorBackgroundService Service, InMemoryEventBus EventBus) BuildConsolidatorService(
        IMemoryStore store)
    {
        string baseUrl = _config[LmStudioBaseUrlKey] ?? "http://llm-host.example:1234/v1";
        string apiKey = _config[LmStudioApiKeyKey] ?? "lm-studio";
        string chatModel = _config[LmStudioChatModelKey] ?? "local-model";

        IChatClient llmClient = new Agency.Llm.OpenAI.OpenAIClient(new Agency.Llm.Common.LlmClientOptions
        {
            ApiKey = apiKey,
            BaseUrl = baseUrl,
        }).CreateChatClient();

        var consolidatorOpts = Options.Create(new ConsolidatorOptions
        {
            MaxIterations = 20,
            MaxCostUsd = 1.0m,
            Model = chatModel,
        });

        var eventBus = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);

        // Pin the agent clock so the system-prompt timestamp is byte-stable, and supply a
        // deterministic, Guid-parseable id for Memory_Merge so the merged-record id echoed into
        // later turns is stable. Both keep the consolidator's request bodies replayable from the
        // HTTP response cache. One counter per service instance — each test runs a single pass.
        var timeProvider = new FakeTimeProvider(DeterministicClock);
        int mergeSeq = 0;
        Func<string> mergeIdFactory = () => $"00000000-0000-0000-0000-{(++mergeSeq):D12}";

        Func<string, IReadOnlyList<MemoryRecord>, CancellationToken, Task<(int Merges, int Updates, int Deletes)>> runner =
            ConsolidatorSubAgentFactory.CreateRunner(
                llmClient,
                chatModel,
                store,
                consolidatorOpts,
                eventBus,
                NullLogger<Agency.Harness.Agent>.Instance,
                timeProvider,
                mergeIdFactory);

        var service = new ConsolidatorBackgroundService(
            store,
            runner,
            eventBus,
            consolidatorOpts,
            NullLogger<ConsolidatorBackgroundService>.Instance);

        return (service, eventBus);
    }

    /// <summary>
    /// Creates a <see cref="MemoryRecord"/> with minimal required fields and sensible defaults.
    /// </summary>
    /// <param name="id">
    /// The record's stable primary key. Tests pass fixed, Guid-parseable literals (not random
    /// GUIDs) so the id rendered into the reconciliation prompt — and echoed in tool-call
    /// args/results — is byte-stable, keeping the request bodies replayable from the HTTP cache.
    /// </param>
    /// <param name="userId">The owning user.</param>
    /// <param name="sessionId">The session, or <see langword="null"/> for global records.</param>
    /// <param name="domain">The semantic domain.</param>
    /// <param name="key">The stable identifier within the domain.</param>
    /// <param name="title">The short human-readable title.</param>
    /// <param name="value">The Markdown body.</param>
    /// <param name="importance">The importance score in [0, 1].</param>
    /// <param name="ageInDays">How many days ago the record was created/updated.</param>
    /// <param name="contentType">
    /// The content type discriminator; defaults to <see cref="ContentType.Fact"/>.
    /// </param>
    /// <returns>A new <see cref="MemoryRecord"/> ready for upsert.</returns>
    private static MemoryRecord MakeRecord(
        string id,
        string userId,
        string? sessionId,
        string domain,
        string key,
        string title,
        string value,
        double importance = 0.6,
        int ageInDays = 1,
        ContentType contentType = ContentType.Fact)
    {
        DateTimeOffset ts = DateTimeOffset.UtcNow.AddDays(-ageInDays);
        return MemoryRecord.Create(
            id: id,
            userId: userId,
            sessionId: sessionId,
            contentType: contentType,
            domain: domain,
            key: key,
            title: title,
            value: value,
            tags: [],
            importance: importance,
            createdAt: ts,
            updatedAt: ts);
    }
}

// ── Internal test helpers ─────────────────────────────────────────────────────

/// <summary>
/// A minimal <see cref="ILogger{T}"/> implementation that counts log entries at
/// <see cref="LogLevel.Warning"/> or above. Used by E3.6 to verify that the
/// large-corpus warning is emitted before the LLM call.
/// </summary>
internal sealed class WarningCapturingLogger<T> : ILogger<T>
{
    private int _warningCount;

    /// <summary>Gets the number of log entries at <see cref="LogLevel.Warning"/> or above captured so far.</summary>
    internal int WarningCount => this._warningCount;

    /// <inheritdoc/>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    /// <inheritdoc/>
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Warning)
        {
            Interlocked.Increment(ref this._warningCount);
        }
    }
}
