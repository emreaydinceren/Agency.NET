using Agency.Memory.Common.Records;
using Agency.Memory.Sql.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using MemoryRecord = Agency.Memory.Common.Records.Record;
using IEmbeddingGenerator = Agency.Embeddings.Common.IEmbeddingGenerator;

namespace Agency.Memory.Functional.Test;

/// <summary>
/// End-to-end functional test: <c>ForgetMe</c> wipes all data for one user
/// while leaving other users' records intact (Spec §12.4, Use Case U4).
/// </summary>
/// <remarks>
/// Requires a running PostgreSQL instance (see README.md).
/// Skip with: <c>dotnet test --filter "Category!=Functional"</c>.
/// Run with: <c>dotnet test --filter "Category=Functional"</c>.
/// </remarks>
[Trait("Category", "Functional")]
[Collection("memory-db")]
public sealed class EndToEndForgetMeTests : IAsyncLifetime
{
    private static readonly IConfiguration _config = TestInfrastructure.BuildConfiguration();

    private NpgsqlDataSource _dataSource = default!;
    private PostgresMemoryStore _store = default!;
    private const int Dim = 4;

    /// <summary>
    /// Initialises the data source and resets the schema before each test run.
    /// Skips if Postgres is not reachable.
    /// </summary>
    public async ValueTask InitializeAsync()
    {
        string? skip = await TestInfrastructure.CheckPostgresAsync(_config, TestContext.Current.CancellationToken);
        if (skip is not null)
        {
            return; // individual tests will skip via the check below
        }

        this._dataSource = TestInfrastructure.BuildDataSource(_config);
        await TestInfrastructure.ResetSchemaAsync(
            this._dataSource, Dim, TestContext.Current.CancellationToken);

        IEmbeddingGenerator embedder = TestInfrastructure.DeterministicEmbedder(Dim);
        this._store = TestInfrastructure.BuildMemoryStore(
            this._dataSource,
            embedder,
            NullLogger<PostgresMemoryStore>.Instance);
    }

    /// <summary>Disposes the data source after each test.</summary>
    public async ValueTask DisposeAsync()
    {
        if (this._dataSource is not null)
        {
            await this._dataSource.DisposeAsync();
        }
    }

    // ── G.2 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <c>ForgetMeAsync</c> removes all records for the targeted user
    /// while leaving a second user's records intact, and that
    /// <c>LastWrittenAtAsync</c> is updated for the forgotten user (Spec §12.4, §8.1).
    /// </summary>
    [Fact]
    public async Task EndToEnd_ForgetMeWipesAllUserData()
    {
        string? skip = await TestInfrastructure.CheckPostgresAsync(_config, TestContext.Current.CancellationToken);
        if (skip is not null)
        {
            Assert.Skip(skip);
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        const string UserA = "user-forget-a";
        const string UserB = "user-forget-b";
        DateTimeOffset seedTime = DateTimeOffset.UtcNow;

        // ── 1. Seed records for both users ───────────────────────────────────

        for (int i = 0; i < 10; i++)
        {
            await this._store.UpsertAsync(MakeRecord(UserA, $"key-{i}"), ct);
        }

        for (int i = 0; i < 3; i++)
        {
            await this._store.UpsertAsync(MakeRecord(UserB, $"key-{i}"), ct);
        }

        // Sanity-check that both users have data.
        IReadOnlyList<MemoryRecord> beforeA = await this._store.GetAllForUserAsync(UserA, ct);
        IReadOnlyList<MemoryRecord> beforeB = await this._store.GetAllForUserAsync(UserB, ct);
        Assert.Equal(10, beforeA.Count);
        Assert.Equal(3, beforeB.Count);

        // ── 2. Forget UserA ──────────────────────────────────────────────────

        int deleted = await this._store.ForgetMeAsync(UserA, ct);
        Assert.Equal(10, deleted);

        // ── 3. Assert UserA has no records ───────────────────────────────────

        IReadOnlyList<MemoryRecord> afterA = await this._store.GetAllForUserAsync(UserA, ct);
        Assert.Empty(afterA);

        // ── 4. Assert UserB records are intact ───────────────────────────────

        IReadOnlyList<MemoryRecord> afterB = await this._store.GetAllForUserAsync(UserB, ct);
        Assert.Equal(3, afterB.Count);

        // ── 5. Assert LastWrittenAt is bumped for UserA (Spec §8.1 invariant) ─

        DateTimeOffset? lastWritten = await this._store.LastWrittenAtAsync(UserA, ct);
        Assert.NotNull(lastWritten);
        Assert.True(lastWritten >= seedTime, "LastWrittenAt must be >= seed time after ForgetMe.");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static MemoryRecord MakeRecord(string userId, string key) =>
        MemoryRecord.Create(
            id: Guid.NewGuid().ToString(),
            userId: userId,
            sessionId: null,
            contentType: ContentType.Fact,
            domain: "ForgetMeTest",
            key: key,
            title: $"Test record {key}",
            value: $"Value for {key}.",
            tags: [],
            importance: 0.5,
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: DateTimeOffset.UtcNow);
}
