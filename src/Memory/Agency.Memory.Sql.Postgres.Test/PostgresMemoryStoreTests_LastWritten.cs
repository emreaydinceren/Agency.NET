using Agency.Embeddings.Common;
using Agency.Memory.Common.Records;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using MemoryRecord = Agency.Memory.Common.Records.Record;

namespace Agency.Memory.Sql.Postgres.Test;

/// <summary>
/// Functional tests for <see cref="PostgresMemoryStore.LastWrittenAtAsync"/> including
/// the cache hydration behaviour described in Spec §8.1.
/// </summary>
[Trait("Category", "Functional")]
public sealed class PostgresMemoryStoreTests_LastWritten : IAsyncLifetime
{
    private NpgsqlDataSource _dataSource = default!;
    private readonly string _runId = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Initialises the store.</summary>
    public async ValueTask InitializeAsync()
    {
        this._dataSource = TestHelpers.BuildDataSource<PostgresMemoryStoreTests_LastWritten>();
        await TestHelpers.ResetSchemaAsync(this._dataSource, 1536, TestContext.Current.CancellationToken);
    }

    /// <summary>Disposes the data source.</summary>
    public async ValueTask DisposeAsync() => await this._dataSource.DisposeAsync();

    // ── tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// LastWrittenAt returns null for a user who has never written any records.
    /// </summary>
    [Fact]
    public async Task LastWrittenAt_UnknownUser_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = MakeStore();
        var result = await store.LastWrittenAtAsync($"nobody-{this._runId}", ct);
        Assert.Null(result);
    }

    /// <summary>
    /// LastWrittenAt returns a value after an upsert that is >= before the upsert.
    /// </summary>
    [Fact]
    public async Task LastWrittenAt_AfterUpsert_ReturnsUpsertTime()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = MakeStore();
        var uid = UniqueUser();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        await store.UpsertAsync(Make(uid, "D", "K"), ct);

        var lastWritten = await store.LastWrittenAtAsync(uid, ct);
        Assert.NotNull(lastWritten);
        Assert.True(lastWritten >= before);
    }

    /// <summary>
    /// LastWrittenAt is hydrated from the database when a fresh store instance is created
    /// over the same connection (simulating a process restart).
    /// </summary>
    [Fact]
    public async Task LastWrittenAt_CacheHydratesFromDbOnRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        var uid = UniqueUser();

        // First store instance writes a record
        var store1 = MakeStore();
        await store1.UpsertAsync(Make(uid, "D", "K"), ct);

        // Second store instance has no cache — must hydrate from DB
        var store2 = MakeStore();
        var lastWritten = await store2.LastWrittenAtAsync(uid, ct);

        Assert.NotNull(lastWritten);
    }

    /// <summary>
    /// LastWrittenAt is bumped by ForgetAsync and ForgetMeAsync.
    /// </summary>
    [Fact]
    public async Task LastWrittenAt_BumpedByForget_AndForgetMe()
    {
        var ct = TestContext.Current.CancellationToken;
        var uid1 = UniqueUser();
        var uid2 = UniqueUser();
        var store = MakeStore();

        await store.UpsertAsync(Make(uid1, "D", "K1"), ct);
        await store.UpsertAsync(Make(uid2, "D", "K2"), ct);
        var afterUpsert1 = await store.LastWrittenAtAsync(uid1, ct);
        var afterUpsert2 = await store.LastWrittenAtAsync(uid2, ct);

        await Task.Delay(10, ct);

        await store.ForgetAsync(uid1, "D", "K1", ct);
        var afterForget = await store.LastWrittenAtAsync(uid1, ct);
        Assert.True(afterForget >= afterUpsert1, "ForgetAsync must advance LastWrittenAt");

        await store.ForgetMeAsync(uid2, ct);
        var afterForgetMe = await store.LastWrittenAtAsync(uid2, ct);
        Assert.True(afterForgetMe >= afterUpsert2, "ForgetMeAsync must advance LastWrittenAt");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private string UniqueUser() => $"u-{Guid.NewGuid():N}";

    private PostgresMemoryStore MakeStore()
    {
        var embedder = CreateDeterministicEmbedder(1536);
        var options = Options.Create(new Agency.Memory.Common.Options.MemoryOptions());
        return new PostgresMemoryStore(this._dataSource, embedder, options, NullLogger<PostgresMemoryStore>.Instance);
    }

    private static MemoryRecord Make(string userId, string domain, string key)
    {
        var now = DateTimeOffset.UtcNow;
        return MemoryRecord.Create(
            id: Guid.NewGuid().ToString(),
            userId: userId,
            sessionId: null,
            contentType: ContentType.Fact,
            domain: domain,
            key: key,
            title: $"{domain}/{key}",
            value: $"Value of {key}",
            tags: [],
            importance: 0.5,
            createdAt: now,
            updatedAt: now,
            embedding: new float[1536].AsMemory());
    }

    private static IEmbeddingGenerator CreateDeterministicEmbedder(int dim)
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

    private async Task TruncateTablesAsync(CancellationToken ct)
    {
        await using var conn = await this._dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "TRUNCATE TABLE records, watermarks, dead_letter, user_state;",
            (NpgsqlConnection)conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
