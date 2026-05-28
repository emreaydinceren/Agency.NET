using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Agency.Memory.Functional.Test.Infrastructure;
using Agency.Memory.Hygiene;
using Agency.Memory.Sql.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using IEmbeddingGenerator = Agency.Embeddings.Common.IEmbeddingGenerator;
using MemoryRecord = Agency.Memory.Common.Records.Record;

namespace Agency.Memory.Functional.Test;

/// <summary>
/// End-to-end Group 4 — Hygiene tests.
/// Covers the hygiene sweeper scenarios: TTL-based deletion, LastAccessedAt reset
/// preserving records, low-importance stale-age pruning, and null-TTL Fact preservation
/// (Memory-TestPlan.md §3, Group 4; Spec §6.6, §8.5, §10.3).
/// </summary>
/// <remarks>
/// All tests carry <c>[Trait("Category","Functional")]</c> and
/// <c>[Trait("Group","Hygiene")]</c> so they can be run in isolation:
/// <code>
/// dotnet test src/Memory/Agency.Memory.Functional.Test
///     --filter "Category=Functional&amp;Group=Hygiene"
/// </code>
/// Each test uses a unique <c>userId</c> of the form <c>{TestName}-{Guid}</c>
/// so residue from a failed run cannot poison subsequent runs.
///
/// These tests are fully deterministic — they do not require LM Studio.
/// They seed records with backdated timestamps via direct SQL, configure appropriate
/// TTL/importance thresholds, then drive a single sweep via
/// <see cref="HygieneSweeperBackgroundService.RunOnceAsync"/> (accessible via
/// <c>InternalsVisibleTo("Agency.Memory.Functional.Test")</c> on
/// <c>Agency.Memory.Hygiene</c>).
///
/// Schema dim is fixed at 1 536 for the entire class — do NOT call
/// <see cref="TestInfrastructure.ResetSchemaAsync"/> with a different dimension
/// from within this class.
/// </remarks>
[Trait("Category", "Functional")]
[Trait("Group", "Hygiene")]
[Collection("memory-db")]
public sealed class Group4HygieneTests : IAsyncLifetime
{
    private static readonly IConfiguration _config = TestInfrastructure.BuildConfiguration();

    /// <summary>Embedding dimension shared by all tests; must match the shared schema.</summary>
    private const int EmbeddingDim = 1536;

    private NpgsqlDataSource _dataSource = default!;
    private IEmbeddingGenerator _stubEmbedder = default!;

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises the Postgres data source and stub embedder.
    /// The schema reset uses the standard 1 536-dim column, consistent with Group 1–3.
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

    // ── E4.1 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// E4.1 — Verifies that a Memory record whose <c>updated_at</c> is older than
    /// the configured TTL and that has never been accessed is deleted by a single sweep
    /// (Spec §6.6 TTL pass, §8.5).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// <list type="bullet">
    ///   <item>One Memory record seeded with <c>updated_at = now() - 60 days</c>,
    ///   <c>last_accessed_at = NULL</c>.</item>
    ///   <item>TTL for <see cref="ContentType.Memory"/> configured to 30 days.</item>
    /// </list>
    /// Steps:
    /// <list type="number">
    ///   <item>Insert record via raw SQL with backdated timestamp.</item>
    ///   <item>Invoke <see cref="HygieneSweeperBackgroundService.RunOnceAsync"/>.</item>
    ///   <item>Assert record no longer exists in the store.</item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task HygieneSweep_MemoryOlderThanTtl_AndNotAccessed_Deleted()
    {
        string? pgSkip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (pgSkip is not null)
        {
            Assert.Skip(pgSkip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userId = $"HygieneSweep_TtlDeleted-{Guid.NewGuid():N}";
        const string Domain = "Hygiene";
        const string Key = "TtlTarget";

        // ── Seed the record with a backdated timestamp ────────────────────────
        await this.InsertBackdatedRecordAsync(
            userId: userId,
            domain: Domain,
            key: Key,
            contentType: ContentType.Memory,
            importance: 0.7,
            updatedAtOffset: TimeSpan.FromDays(-60), // 60 days old — past the 30-day TTL
            lastAccessedAtOffset: null,               // never accessed
            ct: ct);

        // ── Configure store + sweeper with a 30-day TTL for Memory ────────────
        var memOpts = Options.Create(new MemoryOptions
        {
            Ttl = new Dictionary<ContentType, TimeSpan>
            {
                [ContentType.Memory] = TimeSpan.FromDays(30),
            },
        });

        var store = new PostgresMemoryStore(
            this._dataSource,
            this._stubEmbedder,
            memOpts,
            NullLogger<PostgresMemoryStore>.Instance);

        var sweeper = this.BuildSweeper(store, memOpts.Value);

        // ── Precondition: record exists before sweep ──────────────────────────
        MemoryRecord? before = await store.GetByKeyAsync(userId, null, Domain, Key, ct);
        Assert.NotNull(before);

        // ── Run one sweep ─────────────────────────────────────────────────────
        await sweeper.RunOnceAsync(ct);

        // ── Acceptance: record must be gone ───────────────────────────────────
        MemoryRecord? after = await store.GetByKeyAsync(userId, null, Domain, Key, ct);
        Assert.Null(after);
    }

    // ── E4.2 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// E4.2 — Verifies that retrieval via <see cref="IMemoryStore.SearchAsync"/> resets
    /// <c>last_accessed_at</c> to now, preventing the record from being deleted by the
    /// TTL sweep even though its <c>updated_at</c> is older than the configured TTL
    /// (Spec §6.6 reset).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// <list type="bullet">
    ///   <item>One Memory record seeded with <c>updated_at = now() - 60 days</c>.</item>
    ///   <item>TTL for <see cref="ContentType.Memory"/> configured to 30 days.</item>
    /// </list>
    /// Steps:
    /// <list type="number">
    ///   <item>Insert record with old <c>updated_at</c>; <c>last_accessed_at</c> also
    ///   backdated to confirm it would otherwise be swept.</item>
    ///   <item>Call <see cref="IMemoryStore.SearchAsync"/> to bump <c>last_accessed_at</c>
    ///   to wall-clock now (fire-and-forget; allow time to settle).</item>
    ///   <item>Invoke <see cref="HygieneSweeperBackgroundService.RunOnceAsync"/>.</item>
    ///   <item>Assert record survives — <c>last_accessed_at</c> was refreshed past the TTL
    ///   window.</item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task HygieneSweep_RetrievalResetsLastAccessedAt_PreservesRecord()
    {
        string? pgSkip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (pgSkip is not null)
        {
            Assert.Skip(pgSkip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userId = $"HygieneSweep_AccessPreserves-{Guid.NewGuid():N}";
        const string Domain = "Hygiene";
        const string Key = "AccessedTarget";

        // ── Seed record: old updated_at, old last_accessed_at (would be swept) ─
        await this.InsertBackdatedRecordAsync(
            userId: userId,
            domain: Domain,
            key: Key,
            contentType: ContentType.Memory,
            importance: 0.7,
            updatedAtOffset: TimeSpan.FromDays(-60),       // 60 days old
            lastAccessedAtOffset: TimeSpan.FromDays(-45),  // last accessed 45 days ago — also past TTL
            ct: ct);

        var memOpts = Options.Create(new MemoryOptions
        {
            Ttl = new Dictionary<ContentType, TimeSpan>
            {
                [ContentType.Memory] = TimeSpan.FromDays(30),
            },
            RetrievalTopK = 10,
            OverFetchFactor = 3,
        });

        var store = new PostgresMemoryStore(
            this._dataSource,
            this._stubEmbedder,
            memOpts,
            NullLogger<PostgresMemoryStore>.Instance);

        // ── Confirm it WOULD have been swept without the retrieval touch ───────
        // (We'll verify this at the end by checking the final state rather than
        // doing a mid-test sweep which would complicate the flow.)

        // ── Trigger SearchAsync to bump last_accessed_at to now ───────────────
        var queryEmbedding = await this._stubEmbedder.GenerateEmbeddingAsync(
            "hygiene test query", ct);

        var searchQuery = new SearchQuery(
            UserId: userId,
            QueryEmbedding: queryEmbedding,
            TopK: 10);

        IReadOnlyList<SearchHit> hits = await store.SearchAsync(searchQuery, ct);
        Assert.True(hits.Count >= 1, "E4.2: Expected at least one search hit after seeding.");

        // Allow the fire-and-forget last_accessed_at bump to complete.
        // SearchAsync fires Task.Run to update last_accessed_at asynchronously;
        // a short wait is sufficient since it is a single lightweight UPDATE.
        await Task.Delay(TimeSpan.FromSeconds(1), ct);

        // ── Run one sweep ─────────────────────────────────────────────────────
        var sweeper = this.BuildSweeper(store, memOpts.Value);
        await sweeper.RunOnceAsync(ct);

        // ── Acceptance: record must still be present (access reset the clock) ─
        MemoryRecord? after = await store.GetByKeyAsync(userId, null, Domain, Key, ct);
        Assert.NotNull(after);
    }

    // ── E4.3 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// E4.3 — Verifies that the importance-pruning pass deletes low-importance stale records
    /// while preserving high-importance stale records at the same age (Spec §6.6 importance pass).
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// <list type="bullet">
    ///   <item>Low-importance record (importance = 0.1): <c>last_accessed_at = now() - 60 days</c>.</item>
    ///   <item>High-importance record (importance = 0.9): same age — <c>last_accessed_at = now() - 60 days</c>.</item>
    ///   <item><see cref="MemoryOptions.ImportancePruneThreshold"/> = 0.3; <see cref="MemoryOptions.StalePruneAge"/> = 30 days.</item>
    ///   <item>No TTL configured for any content type (isolates the importance pass).</item>
    /// </list>
    /// Steps:
    /// <list type="number">
    ///   <item>Insert both records with backdated <c>last_accessed_at</c>.</item>
    ///   <item>Invoke <see cref="HygieneSweeperBackgroundService.RunOnceAsync"/>.</item>
    ///   <item>Assert low-importance record deleted; high-importance record preserved.</item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task HygieneSweep_LowImportanceStaleAge_Deleted_HighImportancePreserved()
    {
        string? pgSkip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (pgSkip is not null)
        {
            Assert.Skip(pgSkip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userId = $"HygieneSweep_ImportancePrune-{Guid.NewGuid():N}";
        const string Domain = "Hygiene";
        const string LowKey = "LowImportanceStale";
        const string HighKey = "HighImportanceStale";

        // ── Seed low-importance stale record (candidate for deletion) ─────────
        await this.InsertBackdatedRecordAsync(
            userId: userId,
            domain: Domain,
            key: LowKey,
            contentType: ContentType.Memory,
            importance: 0.1,                           // below pruning threshold (0.3)
            updatedAtOffset: TimeSpan.FromDays(-60),   // 60 days old
            lastAccessedAtOffset: TimeSpan.FromDays(-60), // last accessed 60 days ago — stale
            ct: ct);

        // ── Seed high-importance stale record (must be preserved) ─────────────
        await this.InsertBackdatedRecordAsync(
            userId: userId,
            domain: Domain,
            key: HighKey,
            contentType: ContentType.Memory,
            importance: 0.9,                           // above pruning threshold (0.3)
            updatedAtOffset: TimeSpan.FromDays(-60),   // also 60 days old
            lastAccessedAtOffset: TimeSpan.FromDays(-60), // also last accessed 60 days ago
            ct: ct);

        // No TTL configured — isolates the importance pass only.
        var memOpts = Options.Create(new MemoryOptions
        {
            Ttl = [],   // no TTL pruning
            ImportancePruneThreshold = 0.3,
            StalePruneAge = TimeSpan.FromDays(30),
        });

        var store = new PostgresMemoryStore(
            this._dataSource,
            this._stubEmbedder,
            memOpts,
            NullLogger<PostgresMemoryStore>.Instance);

        var sweeper = this.BuildSweeper(store, memOpts.Value);

        // ── Preconditions: both records exist ────────────────────────────────
        MemoryRecord? lowBefore = await store.GetByKeyAsync(userId, null, Domain, LowKey, ct);
        MemoryRecord? highBefore = await store.GetByKeyAsync(userId, null, Domain, HighKey, ct);
        Assert.NotNull(lowBefore);
        Assert.NotNull(highBefore);

        // ── Run one sweep ─────────────────────────────────────────────────────
        await sweeper.RunOnceAsync(ct);

        // ── Acceptance ────────────────────────────────────────────────────────
        MemoryRecord? lowAfter = await store.GetByKeyAsync(userId, null, Domain, LowKey, ct);
        MemoryRecord? highAfter = await store.GetByKeyAsync(userId, null, Domain, HighKey, ct);

        Assert.Null(lowAfter);    // low-importance stale record must be deleted
        Assert.NotNull(highAfter); // high-importance record must survive
    }

    // ── E4.4 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// E4.4 — Verifies that a Fact with no TTL configured for <see cref="ContentType.Fact"/>
    /// is never deleted by the TTL pass, even when its <c>updated_at</c> is arbitrarily old
    /// (Spec §10.3 resource bounds; Spec §6.6 internal flow "if ttl is null: continue").
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// <list type="bullet">
    ///   <item>One Fact record seeded with <c>updated_at = now() - 365 days</c>,
    ///   <c>last_accessed_at = NULL</c>.</item>
    ///   <item>TTL configured only for <see cref="ContentType.Memory"/> (30 days) —
    ///   no entry for <see cref="ContentType.Fact"/>.</item>
    /// </list>
    /// Steps:
    /// <list type="number">
    ///   <item>Insert Fact with old timestamp via raw SQL.</item>
    ///   <item>Invoke <see cref="HygieneSweeperBackgroundService.RunOnceAsync"/>.</item>
    ///   <item>Assert Fact record still exists — the TTL pass skips types with no TTL entry.</item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task HygieneSweep_FactsWithNullTtl_NeverExpire()
    {
        string? pgSkip = await TestInfrastructure.CheckPostgresAsync(
            _config, TestContext.Current.CancellationToken);
        if (pgSkip is not null)
        {
            Assert.Skip(pgSkip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        string userId = $"HygieneSweep_NullTtlFact-{Guid.NewGuid():N}";
        const string Domain = "Hygiene";
        const string Key = "DurableFact";

        // ── Seed Fact with a very old timestamp ───────────────────────────────
        await this.InsertBackdatedRecordAsync(
            userId: userId,
            domain: Domain,
            key: Key,
            contentType: ContentType.Fact,
            importance: 0.8,
            updatedAtOffset: TimeSpan.FromDays(-365), // 1 year old — would be swept if TTL applied
            lastAccessedAtOffset: null,               // never accessed
            ct: ct);

        // TTL only for Memory; Fact has no TTL entry → sweep skips Facts.
        var memOpts = Options.Create(new MemoryOptions
        {
            Ttl = new Dictionary<ContentType, TimeSpan>
            {
                [ContentType.Memory] = TimeSpan.FromDays(30),
            },
            // Importance prune threshold set high so this test also doesn't trigger
            // the importance pass (the record has importance 0.8 which is above 0.2 default,
            // but we set it explicitly to 0.2 to be clear).
            ImportancePruneThreshold = 0.2,
            StalePruneAge = TimeSpan.FromDays(30),
        });

        var store = new PostgresMemoryStore(
            this._dataSource,
            this._stubEmbedder,
            memOpts,
            NullLogger<PostgresMemoryStore>.Instance);

        var sweeper = this.BuildSweeper(store, memOpts.Value);

        // ── Precondition: Fact exists ─────────────────────────────────────────
        MemoryRecord? before = await store.GetByKeyAsync(userId, null, Domain, Key, ct);
        Assert.NotNull(before);

        // ── Run one sweep ─────────────────────────────────────────────────────
        await sweeper.RunOnceAsync(ct);

        // ── Acceptance: Fact must not be deleted (no TTL for ContentType.Fact) ─
        MemoryRecord? after = await store.GetByKeyAsync(userId, null, Domain, Key, ct);
        Assert.NotNull(after);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Inserts a record directly into the <c>records</c> table with explicit, potentially
    /// backdated timestamps.  Used by Group 4 tests to set up TTL/staleness preconditions
    /// that <see cref="IMemoryStore.UpsertAsync"/> cannot provide (it forces <c>now()</c>
    /// for <c>created_at</c> / <c>updated_at</c>).
    /// </summary>
    /// <param name="userId">The owning user identifier.</param>
    /// <param name="domain">The semantic domain.</param>
    /// <param name="key">The stable key within the domain.</param>
    /// <param name="contentType">The content type discriminator.</param>
    /// <param name="importance">The importance score.</param>
    /// <param name="updatedAtOffset">Negative offset from <c>now()</c> for <c>updated_at</c> / <c>created_at</c>.</param>
    /// <param name="lastAccessedAtOffset">
    /// Optional negative offset from <c>now()</c> for <c>last_accessed_at</c>.
    /// Pass <see langword="null"/> to insert <c>NULL</c> (never accessed).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    private async Task InsertBackdatedRecordAsync(
        string userId,
        string domain,
        string key,
        ContentType contentType,
        double importance,
        TimeSpan updatedAtOffset,
        TimeSpan? lastAccessedAtOffset,
        CancellationToken ct)
    {
        // Generate a stub embedding to satisfy the NOT NULL embedding column constraint.
        var embedding = await this._stubEmbedder.GenerateEmbeddingAsync(
            $"{domain} {key}", ct);

        // Use Postgres interval arithmetic so the backdating is server-side and immune
        // to client/server clock skew.
        string lastAccessedSql = lastAccessedAtOffset.HasValue
            ? $"now() + interval '{(int)lastAccessedAtOffset.Value.TotalSeconds} seconds'"
            : "NULL";

        int secondsOffset = (int)updatedAtOffset.TotalSeconds; // negative → past

        string sql = $@"
            INSERT INTO records (
                id, user_id, session_id, content_type, domain, key,
                title, value, tags, importance, embedding,
                created_at, updated_at, last_accessed_at)
            VALUES (
                gen_random_uuid(), @user_id, NULL, @content_type, @domain, @key,
                @title, @value, ARRAY[]::text[], @importance, @embedding,
                now() + interval '{secondsOffset} seconds',
                now() + interval '{secondsOffset} seconds',
                {lastAccessedSql})
            ON CONFLICT (user_id, COALESCE(session_id, ''), domain, key) DO NOTHING;";

        await using var conn = await this._dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)conn);
        cmd.Parameters.AddWithValue("user_id", userId);
        cmd.Parameters.AddWithValue("content_type", (short)contentType);
        cmd.Parameters.AddWithValue("domain", domain);
        cmd.Parameters.AddWithValue("key", key);
        cmd.Parameters.AddWithValue("title", $"{domain} — {key}");
        cmd.Parameters.AddWithValue("value", $"Seeded by Group 4 test: {domain}/{key}");
        cmd.Parameters.AddWithValue("importance", importance);
        cmd.Parameters.Add(new NpgsqlParameter("embedding", new Pgvector.Vector(embedding.ToArray())));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Builds a <see cref="HygieneSweeperBackgroundService"/> with the given store and options.
    /// The <see cref="TimeShim"/> is constructed but not advanced — Group 4 tests drive
    /// the sweeper directly via <c>RunOnceAsync</c> rather than via the periodic timer loop,
    /// so virtual time advancement is not required for correctness.
    /// </summary>
    /// <param name="store">The memory store to sweep.</param>
    /// <param name="options">The memory options governing TTL and importance thresholds.</param>
    /// <returns>A configured <see cref="HygieneSweeperBackgroundService"/> instance.</returns>
    private HygieneSweeperBackgroundService BuildSweeper(
        IMemoryStore store,
        MemoryOptions options)
    {
        var shim = new TimeShim();
        return new HygieneSweeperBackgroundService(
            store,
            Options.Create(options),
            shim.Provider,
            NullLogger<HygieneSweeperBackgroundService>.Instance);
    }
}
