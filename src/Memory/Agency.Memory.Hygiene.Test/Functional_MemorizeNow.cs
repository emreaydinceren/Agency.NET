using Agency.Embeddings.Common;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Agency.Memory.Sql.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Npgsql;
using MemoryRecord = Agency.Memory.Common.Records.Record;

namespace Agency.Memory.Hygiene.Test;

/// <summary>
/// Functional (E2E) test for FT-6 (MemorizeNow-Design.md § Functional Test Validations § FT-6):
/// the Hygiene Sweeper's importance-pruning pass protects a Low-importance agent-signaled fact
/// (MemorizeNow's 0.3 floor) against a live PostgreSQL database.
/// </summary>
/// <remarks>
/// <see cref="HygieneSweeperTests_Importance"/> (UT-6) already proves the survive/prune predicate
/// using an in-memory <c>PredicateFakeStore</c> that mirrors the SQL by hand — useful for fast,
/// deterministic unit coverage, but it never runs the real
/// <see cref="PostgresMemoryStore.DeleteWhereLowImportanceStaleAsync"/> SQL. These tests exercise
/// that real query (and <see cref="PostgresMemoryStore.MemorizeNowAsync"/>'s real key/importance/
/// provenance mapping) end-to-end against docker-compose Postgres, per this task's "live
/// docker-compose Postgres" requirement. Both <c>DeleteWhereLowImportanceStaleAsync</c> and
/// <c>DeleteWhereTtlExceededAsync</c> take the sweeper's clock reading as an explicit <c>now</c>
/// parameter rather than reading the database wall clock (TI-4), so "time advances 30+ days" is
/// simulated by passing a future <c>now</c> to a real <see cref="HygieneSweeperBackgroundService"/>
/// driven by a <see cref="FakeTimeProvider"/> — no row backdating via raw SQL is required.
/// </remarks>
[Trait("Category", "Functional")]
public sealed class Functional_MemorizeNow : IAsyncLifetime
{
    private const string TestSchema = "mem_hygiene_ft_test";

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
    /// An agent calls MemorizeNow with Low importance (0.3) on a fact. Time then advances 45 days —
    /// past the 30-day stale threshold — and the Hygiene Sweeper runs. The agent-signaled fact
    /// survives because 0.3 is above the 0.2 importance-prune threshold; it is not source-checked,
    /// only importance-protected (same finding as
    /// <see cref="HygieneSweeperTests_Importance.Importance_AgentSignaledLowImportanceFact_Survives_ViaImportanceFloor_NotSourceCheck"/>,
    /// now proven against the real Postgres predicate). A Distilled fact of the same age whose
    /// importance (0.1) is genuinely below the threshold is pruned in the same pass, showing the
    /// sweep is selective rather than blanket-skipping everything that looks "agent-adjacent".
    /// </summary>
    [Fact]
    public async Task HygienePruning_AgentSignaledLowImportanceFact_Survives_DistilledSubThresholdFact_IsPruned()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = this.MakeStore();
        const string userId = "u-ft6-survive";

        string compositeKey = await store.MemorizeNowAsync(
            userId, "s1", "Rarely Used Env Var", "STAGING_FLAG only matters when deploying to staging.",
            "Environment", Importance.Low, [], ct);
        var (agentDomain, agentKey) = SplitCompositeKey(compositeKey);

        var now = DateTimeOffset.UtcNow;
        var distilled = MemoryRecord.Create(
            id: string.Empty,
            userId: userId,
            sessionId: null,
            contentType: ContentType.Fact,
            domain: "environment",
            key: "stale-distilled-note",
            title: "Stale distilled note",
            value: "A minor observation from a past session.",
            tags: [],
            importance: 0.1, // below the 0.2 threshold
            createdAt: now,
            updatedAt: now,
            source: MemorySource.Distilled);
        await store.UpsertAsync(distilled, ct);

        // Time advances 45 days — past the 30-day stale threshold — then the sweeper runs.
        var sweeper = CreateSweeper(store, now.AddDays(45), staleAge: TimeSpan.FromDays(30));
        await sweeper.RunOnceAsync(ct);

        var survivingAgentFact = await store.GetByKeyAsync(userId, null, agentDomain, agentKey, ct);
        Assert.NotNull(survivingAgentFact);
        Assert.Equal(MemorySource.AgentSignaled, survivingAgentFact.Source);
        Assert.Equal(0.3, survivingAgentFact.Importance);

        var prunedDistilledFact = await store.GetByKeyAsync(userId, null, "environment", "stale-distilled-note", ct);
        Assert.Null(prunedDistilledFact);
    }

    /// <summary>
    /// The importance-pruning pass and the TTL pass are independent (same distinction documented by
    /// <see cref="HygieneSweeperTests_Importance.Importance_TtlPass_DeletesHighImportanceRecord_IndependentlyOfImportanceProtection"/>):
    /// a High-importance (0.9) agent-signaled fact is immune to the importance-pruning pass, but
    /// once it exceeds its content-type TTL the independent TTL pass still deletes it.
    /// </summary>
    [Fact]
    public async Task HygienePruning_TtlPass_DeletesHighImportanceAgentFact_IndependentlyOfImportanceProtection()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = this.MakeStore();
        const string userId = "u-ft6-ttl";

        string compositeKey = await store.MemorizeNowAsync(
            userId, "s1", "Critical Deploy Step", "Always run migrations before restarting the service.",
            "Deployment", Importance.High, [], ct);
        var (domain, key) = SplitCompositeKey(compositeKey);

        var now = DateTimeOffset.UtcNow;
        // 90 days later exceeds a 60-day Fact TTL, even though importance (0.9) is far above the
        // 0.2 pruning threshold — TTL still applies independently of importance protection.
        var sweeper = CreateSweeper(
            store,
            now.AddDays(90),
            staleAge: TimeSpan.FromDays(30),
            ttl: new Dictionary<ContentType, TimeSpan> { [ContentType.Fact] = TimeSpan.FromDays(60) });

        await sweeper.RunOnceAsync(ct);

        var record = await store.GetByKeyAsync(userId, null, domain, key, ct);
        Assert.Null(record);
    }

    private PostgresMemoryStore MakeStore() =>
        new(this._dataSource, DeterministicEmbedder(1536), Options.Create(new MemoryOptions()), NullLogger<PostgresMemoryStore>.Instance);

    private static HygieneSweeperBackgroundService CreateSweeper(
        PostgresMemoryStore store,
        DateTimeOffset now,
        TimeSpan staleAge,
        Dictionary<ContentType, TimeSpan>? ttl = null) =>
        new(
            store,
            Options.Create(new MemoryOptions { ImportancePruneThreshold = 0.2, StalePruneAge = staleAge, Ttl = ttl ?? [] }),
            new FakeTimeProvider(now),
            NullLogger<HygieneSweeperBackgroundService>.Instance);

    private static (string Domain, string Key) SplitCompositeKey(string compositeKey)
    {
        string[] parts = compositeKey.Split('|', 2);
        return (parts[0], parts[1]);
    }

    // ── Local Postgres connection helpers ───────────────────────────────────
    //
    // Mirrors Agency.Memory.Sql.Postgres.Test/TestHelpers.cs (that class is `internal` to its own
    // assembly and not visible here). Isolated to its own schema so this project's DDL/DML never
    // races the Postgres.Test or Functional.Test assemblies on the shared `records` table — the
    // importance/TTL sweep queries have no user_id filter, so schema isolation matters more here
    // than in most functional test classes.

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
