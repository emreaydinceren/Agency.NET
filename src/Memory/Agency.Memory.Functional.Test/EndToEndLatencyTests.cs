using Agency.Harness;
using Agency.Harness.Contexts;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Agency.Memory.Retrieval;
using Agency.Memory.Sql.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Diagnostics;
using IEmbeddingGenerator = Agency.Embeddings.Common.IEmbeddingGenerator;
using MemoryRecord = Agency.Memory.Common.Records.Record;

namespace Agency.Memory.Functional.Test;

/// <summary>
/// End-to-end hot-path latency test (Spec §11.1).
/// </summary>
/// <remarks>
/// Runs a 30-turn session with the memory pipeline enabled using a stub embedder (to avoid
/// real embedding-service latency). Asserts that p95 of <c>OnPreIteration</c> duration
/// is &lt;= 500 ms and that at least 90% of iterations are gate-hits (no embedding + search call).
///
/// Requires a running PostgreSQL instance (see README.md).
/// Skip with: <c>dotnet test --filter "Category!=Functional"</c>.
/// </remarks>
[Trait("Category", "Functional")]
[Collection("memory-db")]
public sealed class EndToEndLatencyTests : IAsyncLifetime
{
    private static readonly IConfiguration _config = TestInfrastructure.BuildConfiguration();

    private NpgsqlDataSource _dataSource = default!;
    private PostgresMemoryStore _store = default!;
    private RetrievalEngine _engine = default!;
    private IEmbeddingGenerator _embedder = default!;
    private const int Dim = 4;

    /// <summary>
    /// Initialises Postgres and the retrieval engine with a deterministic stub embedder.
    /// </summary>
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

        this._embedder = TestInfrastructure.DeterministicEmbedder(Dim);
        this._store = TestInfrastructure.BuildMemoryStore(
            this._dataSource,
            this._embedder,
            NullLogger<PostgresMemoryStore>.Instance);

        var opts = Options.Create(new MemoryOptions
        {
            RetrievalTopK = 5,
            OverFetchFactor = 2,
        });
        this._engine = new RetrievalEngine(this._store, this._embedder, opts);
    }

    /// <summary>Disposes the data source.</summary>
    public async ValueTask DisposeAsync()
    {
        if (this._dataSource is not null)
        {
            await this._dataSource.DisposeAsync();
        }
    }

    // ── G.3 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that the memory hot-path overhead stays within the Spec §11.1 budget:
    /// p95 &lt;= 500 ms per iteration, and at least 90% of iterations are gate-hits
    /// (store unchanged → no embedding + search call).
    /// </summary>
    [Fact]
    public async Task EndToEnd_HighFrequencyTurns_HotPathLatencyUnaffected()
    {
        string? skip = await TestInfrastructure.CheckPostgresAsync(_config, TestContext.Current.CancellationToken);
        if (skip is not null)
        {
            Assert.Skip(skip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        const int TurnCount = 30;
        const string UserId = "user-latency";
        const string UserPrompt = "Write me a script to deduplicate this list.";

        // Seed one record so the store's LastWrittenAt is set.
        // Without this, lastWritten == null and the gate always fires (correct per spec §8.1,
        // but makes the gate-hit ratio test meaningless).
        await this._store.UpsertAsync(
            MemoryRecord.Create(
                id: System.Guid.NewGuid().ToString(),
                userId: UserId,
                sessionId: null,
                contentType: Agency.Memory.Common.Records.ContentType.Fact,
                domain: "Prefs",
                key: "Lang",
                title: "Language",
                value: "Python",
                tags: [],
                importance: 0.7,
                createdAt: DateTimeOffset.UtcNow,
                updatedAt: DateTimeOffset.UtcNow),
            ct);

        // Build a context with a single prior message so the engine has something to work with.
        var conv = new InMemoryConversationManager();
        conv.Append(new Microsoft.Extensions.AI.ChatMessage(
            Microsoft.Extensions.AI.ChatRole.User, UserPrompt));

        var ctx = new Context
        {
            Query = new QueryContext { Prompt = UserPrompt },
            User = new UserSpecificContext { Id = UserId },
            Conversation = conv,
        };

        var durations = new List<long>(TurnCount);
        int gateHits = 0;
        int gateMisses = 0;

        for (int turn = 0; turn < TurnCount; turn++)
        {
            var sw = Stopwatch.StartNew();

            // Simulate OnPreIteration: check gate and optionally retrieve.
            bool shouldRetrieve = await RetrievalGate.ShouldRetrieveAsync(ctx, this._store, ct);

            if (shouldRetrieve)
            {
                gateMisses++;
                await this._engine.RetrieveAsync(ctx, ct);
            }
            else
            {
                gateHits++;
            }

            sw.Stop();
            durations.Add(sw.ElapsedMilliseconds);

            // Simulate a small gap between turns (does not count toward latency budget).
            // No real LLM call — we just cycle through the hook.
        }

        // ── Latency assertion ─────────────────────────────────────────────────

        durations.Sort();
        long p95 = durations[(int)(TurnCount * 0.95) - 1];

        // Assert p95 <= 500 ms (Spec §11.1 gate-miss budget; gate-hits should be <<1 ms).
        Assert.True(
            p95 <= 500,
            $"p95 OnPreIteration duration was {p95} ms, expected <= 500 ms.");

        // ── Gate-hit ratio assertion ─────────────────────────────────────────

        double gateHitRatio = (double)gateHits / TurnCount;

        // After the first turn writes no records (empty store), subsequent turns
        // should all be gate-hits because the store is unchanged.
        // We expect at least 90% gate-hits across 30 turns.
        Assert.True(
            gateHitRatio >= 0.90,
            $"Gate-hit ratio was {gateHitRatio:P0}, expected >= 90%. " +
            $"Gate hits: {gateHits}, gate misses: {gateMisses}.");
    }
}
