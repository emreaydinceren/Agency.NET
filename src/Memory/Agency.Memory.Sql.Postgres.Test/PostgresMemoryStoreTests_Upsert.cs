using Agency.Embeddings.Common;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Agency.Memory.Sql.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using MemoryRecord = Agency.Memory.Common.Records.Record;

namespace Agency.Memory.Sql.Postgres.Test;

/// <summary>
/// Functional tests for <see cref="PostgresMemoryStore.UpsertAsync"/>.
/// Require a running PostgreSQL instance.
/// </summary>
[Trait("Category", "Functional")]
public sealed class PostgresMemoryStoreTests_Upsert : IAsyncLifetime
{
    private NpgsqlDataSource _dataSource = default!;
    private PostgresMemoryStore _store = default!;
    private readonly string _runId = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Initialises the store for each test.</summary>
    public async ValueTask InitializeAsync()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<PostgresMemoryStoreTests_Upsert>()
            .AddEnvironmentVariables()
            .Build();

        var cs = config.GetConnectionString("PostgreSql")
            ?? throw new InvalidOperationException("Connection string 'PostgreSql' not found.");

        var builder = new NpgsqlDataSourceBuilder(cs);
        builder.UseVector();
        this._dataSource = builder.Build();

        var embedder = CreateDeterministicEmbedder(1536);
        var options = Options.Create(new Agency.Memory.Common.Options.MemoryOptions());
        this._store = new PostgresMemoryStore(this._dataSource, embedder, options, NullLogger<PostgresMemoryStore>.Instance);

        await TestHelpers.ResetSchemaAsync(this._dataSource, 1536, TestContext.Current.CancellationToken);
    }

    /// <summary>Disposes the data source after each test.</summary>
    public async ValueTask DisposeAsync()
    {
        await this._dataSource.DisposeAsync();
    }

    /// <summary>
    /// Upserting a new record assigns an Id, a CreatedAt, and the row exists in the database.
    /// </summary>
    [Fact]
    public async Task Upsert_NewRecord_AssignsIdAndTimestamps_AndRowExistsInDb()
    {
        var ct = TestContext.Current.CancellationToken;
        var record = MakeRecord("u1", null, "D", UniqueKey("K1"));

        var result = await this._store.UpsertAsync(record, ct);

        Assert.NotEmpty(result.Id);
        Assert.NotEqual(DateTimeOffset.MinValue, result.CreatedAt);
        Assert.NotEqual(DateTimeOffset.MinValue, result.UpdatedAt);

        // Confirm the row really exists
        var row = await this._store.GetByKeyAsync("u1", null, "D", record.Key, ct);
        Assert.NotNull(row);
        Assert.Equal(result.Id, row.Id);
    }

    /// <summary>
    /// Upserting the same upsert key twice preserves the Id, updates Value, and bumps UpdatedAt.
    /// </summary>
    [Fact]
    public async Task Upsert_SameUpsertKey_OverwritesValue_PreservesId_BumpsUpdatedAt()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = UniqueKey("K2");
        var first = await this._store.UpsertAsync(MakeRecord("u1", null, "D", key), ct);

        await Task.Delay(10, ct); // ensure clock advances
        var updated = first with { Value = "updated value" };
        var second = await this._store.UpsertAsync(updated, ct);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("updated value", second.Value);
        Assert.True(second.UpdatedAt >= first.UpdatedAt);
    }

    /// <summary>
    /// Upserting with a different SessionId for the same domain+key creates a second row.
    /// </summary>
    [Fact]
    public async Task Upsert_DifferentSessionId_SameDomainKey_CreatesSecondRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = UniqueKey("K3");
        await this._store.UpsertAsync(MakeRecord("u1", "session-a", "D", key), ct);
        await this._store.UpsertAsync(MakeRecord("u1", "session-b", "D", key), ct);

        var all = await this._store.GetAllForUserAsync("u1", ct);
        var matching = all.Where(r => r.Key == key).ToList();
        Assert.Equal(2, matching.Count);
    }

    /// <summary>
    /// Upserting updates the LastWrittenAt cache and persists to the database.
    /// </summary>
    [Fact]
    public async Task Upsert_BumpsLastWrittenAtCacheAndDb()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await this._store.UpsertAsync(MakeRecord("u-lw", null, "D", UniqueKey("K4")), ct);

        var lastWritten = await this._store.LastWrittenAtAsync("u-lw", ct);
        Assert.NotNull(lastWritten);
        Assert.True(lastWritten >= before);
    }

    /// <summary>
    /// Two concurrent upserts of the same key result in exactly one row.
    /// </summary>
    [Fact]
    public async Task Upsert_TwoConcurrentInsertsSameKey_OneWins_OtherUpdates()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = UniqueKey("K5");
        var r1 = MakeRecord("u1", null, "D", key, value: "first");
        var r2 = MakeRecord("u1", null, "D", key, value: "second");

        await Task.WhenAll(
            this._store.UpsertAsync(r1, ct),
            this._store.UpsertAsync(r2, ct));

        var all = await this._store.GetAllForUserAsync("u1", ct);
        var matching = all.Where(r => r.Key == key).ToList();
        Assert.Single(matching);
    }

    /// <summary>
    /// Upserting a record with an empty embedding calls the embedding generator.
    /// </summary>
    [Fact]
    public async Task Upsert_GeneratesEmbedding_WhenEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        var mockEmbedder = new Mock<IEmbeddingGenerator>();
        var vec = new float[1536];
        vec[0] = 0.5f;
        mockEmbedder
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ReadOnlyMemory<float>)vec.AsMemory());

        var options = Options.Create(new Agency.Memory.Common.Options.MemoryOptions());
        var store = new PostgresMemoryStore(this._dataSource, mockEmbedder.Object, options, NullLogger<PostgresMemoryStore>.Instance);

        // Build record manually so embedding is truly empty (not replaced by MakeRecord helper)
        var now = DateTimeOffset.UtcNow;
        var record = MemoryRecord.Create(
            id: Guid.NewGuid().ToString(),
            userId: "u-emb",
            sessionId: null,
            contentType: ContentType.Fact,
            domain: "D",
            key: UniqueKey("K6"),
            title: "D/K6",
            value: "some value",
            tags: [],
            importance: 0.5,
            createdAt: now,
            updatedAt: now,
            embedding: ReadOnlyMemory<float>.Empty);

        await store.UpsertAsync(record, ct);

        mockEmbedder.Verify(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private string UniqueKey(string prefix) => $"{prefix}_{this._runId}";

    private static MemoryRecord MakeRecord(
        string userId,
        string? sessionId,
        string domain,
        string key,
        string? value = null,
        ReadOnlyMemory<float> embedding = default)
    {
        var now = DateTimeOffset.UtcNow;
        return MemoryRecord.Create(
            id: Guid.NewGuid().ToString(),
            userId: userId,
            sessionId: sessionId,
            contentType: ContentType.Fact,
            domain: domain,
            key: key,
            title: $"{domain}/{key}",
            value: value ?? $"Value of {key}",
            tags: [],
            importance: 0.5,
            createdAt: now,
            updatedAt: now,
            embedding: embedding.IsEmpty ? new float[1536].AsMemory() : embedding);
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
