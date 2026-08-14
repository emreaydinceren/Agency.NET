using System.Text.Json;
using Agency.Embeddings.Common;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Agency.Memory.Distiller.Tools;
using Agency.Memory.Sql.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using MemoryRecord = Agency.Memory.Common.Records.Record;

namespace Agency.Memory.Distiller.Test;

/// <summary>
/// Functional (E2E) test for FT-7 (MemorizeNow-Design.md § Functional Test Validations § FT-7):
/// domain values are case-folded consistently so semantically-identical domains don't fragment
/// into separate buckets, exercised against a live PostgreSQL database through the real
/// <see cref="MemorizeNowTool"/> → <see cref="PostgresMemoryStore"/> path.
/// </summary>
/// <remarks>
/// <b>Scope finding, not silently assumed away:</b> case-folding (<c>domain.ToLowerInvariant()</c>)
/// is implemented exactly once, inside <see cref="PostgresMemoryStore.MemorizeNowAsync"/> (and its
/// Sqlite/in-memory-stub twins) — i.e. it is a guarantee of the agent-signaled MemorizeNow path
/// specifically, not a store-wide invariant. The generic <see cref="IMemoryStore.UpsertAsync"/> used
/// by the Distiller's episode-extraction pipeline (<c>EpisodeExtractionParser</c>, which passes the
/// LLM's raw <c>r.Domain</c> straight through — confirmed by repo-wide grep, no normalization call
/// anywhere in <c>Agency.Memory.Distiller</c> or <c>Agency.Memory.Consolidator</c>) does not fold
/// case. The tests below therefore split FT-7's claim in two: the guarantee that actually holds
/// (repeated MemorizeNow calls with varying domain casing never fragment or duplicate — verified
/// against the real Postgres predicate) and the gap that doesn't (a Distiller-authored record with
/// an unfolded domain fragments away from the MemorizeNow bucket instead of unifying with it). This
/// mirrors the "flagged, not silently fixed" pattern already used for Tasks 21–23 in the project
/// tracker; fixing the Distiller/Consolidator side is a follow-up, not a test-writing-task change.
/// </remarks>
[Trait("Category", "Functional")]
public sealed class Functional_MemorizeNow : IAsyncLifetime
{
    private const string TestSchema = "mem_distiller_ft_test";

    private NpgsqlDataSource _dataSource = default!;

    /// <summary>Builds an isolated-schema Postgres data source and resets it for each test.</summary>
    public async ValueTask InitializeAsync()
    {
        this._dataSource = BuildDataSource();
        await ResetSchemaAsync(this._dataSource, 1536, TestContext.Current.CancellationToken);
    }

    /// <summary>Disposes the data source after each test.</summary>
    public async ValueTask DisposeAsync() => await this._dataSource.DisposeAsync();

    /// <summary>
    /// An agent calls MemorizeNow three times with three different-cased spellings of the same
    /// domain ("Performance", "performance", "PERFORMANCE") and three distinct titles. All three
    /// records land under the single case-folded domain "performance" — no domain fragmentation —
    /// and a domain-filtered <see cref="IMemoryStore.SearchAsync"/> query for "performance" (the
    /// canonical lowercase form) pools all three candidates in one bucket regardless of how each was
    /// originally cased at write time.
    /// </summary>
    [Fact]
    public async Task DomainConsistency_MemorizeNow_CaseVariants_AllFoldToSameLowercaseDomain_NoFragmentation()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = this.MakeStore();
        var tool = new MemorizeNowTool(store, "u-ft7-variants", "s1");

        await InvokeMemorizeNow(tool, "Python 3.10 async perf", "Python 3.10+ is faster.", "Performance", ct);
        await InvokeMemorizeNow(tool, "npm cold start", "npm cold start takes 2s on this machine.", "performance", ct);
        await InvokeMemorizeNow(tool, "SQL index scan cost", "Missing index causes full table scan.", "PERFORMANCE", ct);

        IReadOnlyList<MemoryRecord> all = await store.GetAllForUserAsync("u-ft7-variants", ct);

        Assert.Equal(3, all.Count);
        Assert.All(all, r => Assert.Equal("performance", r.Domain));

        // The domain-filtered search bucket contains all three, addressed only by the canonical
        // lowercase form -- proving retrieval pools candidates from one domain bucket, not three.
        ReadOnlyMemory<float> queryEmbedding = await DeterministicEmbedder(1536).GenerateEmbeddingAsync("query", ct);
        var searchHits = await store.SearchAsync(
            new SearchQuery("u-ft7-variants", queryEmbedding, TopK: 10, Domain: "performance"),
            ct);
        Assert.Equal(3, searchHits.Count);
    }

    /// <summary>
    /// Calling MemorizeNow with the *same* title but three different domain casings overwrites a
    /// single record rather than creating duplicates — the composite key
    /// <c>"{domain}|{key}"</c> is normalized (domain lowercased, key slugified) before the upsert's
    /// conflict target is evaluated, so there is exactly one surviving row, holding the third call's
    /// content, no matter how the domain was cased on each call.
    /// </summary>
    [Fact]
    public async Task DomainConsistency_MemorizeNow_SameTitleDifferentDomainCase_OverwritesSingleRecord_NoDuplicateKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = this.MakeStore();
        var tool = new MemorizeNowTool(store, "u-ft7-overwrite", "s1");

        await InvokeMemorizeNow(tool, "Async Perf", "First phrasing.", "Performance", ct);
        await InvokeMemorizeNow(tool, "Async Perf", "Second phrasing.", "performance", ct);
        await InvokeMemorizeNow(tool, "Async Perf", "Third phrasing.", "PERFORMANCE", ct);

        IReadOnlyList<MemoryRecord> all = await store.GetAllForUserAsync("u-ft7-overwrite", ct);

        MemoryRecord single = Assert.Single(all);
        Assert.Equal("performance", single.Domain);
        Assert.Equal("async-perf", single.Key);
        Assert.Equal("Third phrasing.", single.Value);
    }

    /// <summary>
    /// Documents the FT-7 scope gap described in the class remarks: a Distilled-source record
    /// written the way the real Distiller pipeline writes it today (raw LLM-cased domain, via the
    /// generic <see cref="IMemoryStore.UpsertAsync"/> the Distiller actually calls -- not
    /// MemorizeNow) does <b>not</b> get case-folded, and lands as a separate domain bucket from an
    /// agent-signaled "performance" fact instead of unifying with it. This is real, current
    /// behavior, not a defect introduced by this test: it demonstrates that MemorizeNow's
    /// case-folding guarantee is scoped to its own write path, not a store-wide invariant.
    /// </summary>
    [Fact]
    public async Task DomainConsistency_DistillerAuthoredRecord_UnfoldedDomain_FragmentsFromMemorizeNowDomain()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = this.MakeStore();
        const string userId = "u-ft7-gap";

        var tool = new MemorizeNowTool(store, userId, "s1");
        await InvokeMemorizeNow(tool, "Async Perf Agent Fact", "Agent-verified conclusion.", "Performance", ct);

        // Mirrors EpisodeExtractionParser's real behavior: the LLM's raw domain string is passed
        // straight into a Record and upserted via the generic path, with no case-folding.
        var now = DateTimeOffset.UtcNow;
        var distilled = MemoryRecord.Create(
            id: string.Empty,
            userId: userId,
            sessionId: null,
            contentType: ContentType.Fact,
            domain: "PERFORMANCE",
            key: "distilled-fact",
            title: "Distilled Perf Fact",
            value: "Extracted from a past session's transcript.",
            tags: [],
            importance: 0.6,
            createdAt: now,
            updatedAt: now,
            source: MemorySource.Distilled);
        await store.UpsertAsync(distilled, ct);

        IReadOnlyList<MemoryRecord> all = await store.GetAllForUserAsync(userId, ct);

        var distinctDomains = all.Select(r => r.Domain).Distinct().ToList();
        Assert.Equal(2, all.Count);
        Assert.Equal(2, distinctDomains.Count);
        Assert.Contains("performance", distinctDomains);
        Assert.Contains("PERFORMANCE", distinctDomains);
    }

    private PostgresMemoryStore MakeStore() =>
        new(this._dataSource, DeterministicEmbedder(1536), Options.Create(new MemoryOptions()), NullLogger<PostgresMemoryStore>.Instance);

    private static async Task InvokeMemorizeNow(
        MemorizeNowTool tool, string title, string value, string domain, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(new
        {
            title,
            value,
            domain,
            importance = "Normal",
            tags = Array.Empty<string>(),
        });
        JsonElement input = JsonDocument.Parse(json).RootElement;

        var result = await tool.InvokeAsync(input, ct);
        Assert.False(result.IsError);
    }

    // ── Local Postgres connection helpers ───────────────────────────────────
    //
    // Mirrors Agency.Memory.Sql.Postgres.Test/TestHelpers.cs (that class is `internal` to its own
    // assembly and not visible here). Isolated to its own schema so this project's DDL/DML never
    // races the Postgres.Test or Hygiene.Test/Functional.Test assemblies on the shared `records`
    // table when run concurrently.

    private static NpgsqlDataSource BuildDataSource()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<Functional_MemorizeNow>()
            .AddEnvironmentVariables()
            .Build();

        var cs = config.GetConnectionString("PostgreSql")
            ?? throw new InvalidOperationException("Connection string 'PostgreSql' not found.");

        var csb = new NpgsqlConnectionStringBuilder(cs) { NoResetOnClose = true };
        var builder = new NpgsqlDataSourceBuilder(csb.ConnectionString);
        builder.UseVector();

        string initSql = $"CREATE SCHEMA IF NOT EXISTS {TestSchema}; SET search_path TO {TestSchema}, public;";
        builder.UsePhysicalConnectionInitializer(
            conn =>
            {
                using var cmd = new NpgsqlCommand(initSql, conn);
                cmd.ExecuteNonQuery();
            },
            async conn =>
            {
                await using var cmd = new NpgsqlCommand(initSql, conn);
                await cmd.ExecuteNonQueryAsync();
            });

        return builder.Build();
    }

    private static async Task ResetSchemaAsync(NpgsqlDataSource dataSource, int dim, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var dropCmd = new NpgsqlCommand("DROP TABLE IF EXISTS records CASCADE;", (NpgsqlConnection)conn);
        await dropCmd.ExecuteNonQueryAsync(ct);

        await new MemorySchemaInitializer(dataSource).InitializeAsync(dim, ct);

        await using var truncCmd = new NpgsqlCommand(
            "TRUNCATE TABLE watermarks, dead_letter, user_state;",
            (NpgsqlConnection)conn);
        await truncCmd.ExecuteNonQueryAsync(ct);
    }

    private static IEmbeddingGenerator DeterministicEmbedder(int dim)
    {
        var mock = new Mock<IEmbeddingGenerator>();
        mock.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((input, _) =>
            {
                var rng = new Random(input.GetHashCode());
                var arr = new float[dim];
                for (int i = 0; i < dim; i++)
                {
                    arr[i] = (float)rng.NextDouble();
                }

                return Task.FromResult((ReadOnlyMemory<float>)arr.AsMemory());
            });
        return mock.Object;
    }
}
