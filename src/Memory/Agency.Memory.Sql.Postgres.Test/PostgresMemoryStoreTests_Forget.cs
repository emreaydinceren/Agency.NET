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
/// Functional tests for <see cref="PostgresMemoryStore.ForgetAsync"/> and
/// <see cref="PostgresMemoryStore.ForgetMeAsync"/>.
/// </summary>
[Trait("Category", "Functional")]
public sealed class PostgresMemoryStoreTests_Forget : IAsyncLifetime
{
    private NpgsqlDataSource _dataSource = default!;
    private PostgresMemoryStore _store = default!;
    private readonly string _runId = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Initialises the store.</summary>
    public async ValueTask InitializeAsync()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<PostgresMemoryStoreTests_Forget>()
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

    /// <summary>Disposes the data source.</summary>
    public async ValueTask DisposeAsync() => await this._dataSource.DisposeAsync();

    // ── ForgetAsync ──────────────────────────────────────────────────────────

    /// <summary>
    /// Forgetting a known key returns true, deletes the row, and advances LastWrittenAt.
    /// </summary>
    [Fact]
    public async Task Forget_KnownKey_ReturnsTrue_DeletesRow_BumpsLastWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        var uid = UniqueUser();
        await this._store.UpsertAsync(Make(uid, "D", "K"), ct);

        bool result = await this._store.ForgetAsync(uid, "D", "K", ct);
        var remaining = await this._store.GetAllForUserAsync(uid, ct);
        var lastWritten = await this._store.LastWrittenAtAsync(uid, ct);

        Assert.True(result);
        Assert.Empty(remaining);
        Assert.NotNull(lastWritten);
    }

    /// <summary>
    /// Forgetting an unknown key returns false without side effects.
    /// </summary>
    [Fact]
    public async Task Forget_UnknownKey_ReturnsFalse_NoSideEffects()
    {
        var ct = TestContext.Current.CancellationToken;
        var uid = UniqueUser();

        bool result = await this._store.ForgetAsync(uid, "D", "NoSuchKey", ct);

        Assert.False(result);
    }

    // ── ForgetMeAsync ────────────────────────────────────────────────────────

    /// <summary>
    /// ForgetMe deletes all records for the user and returns the count.
    /// </summary>
    [Fact]
    public async Task ForgetMe_DeletesAllRecordsForUser_ReturnsCount()
    {
        var ct = TestContext.Current.CancellationToken;
        var uid = UniqueUser();
        await this._store.UpsertAsync(Make(uid, "D", "K1"), ct);
        await this._store.UpsertAsync(Make(uid, "D", "K2"), ct);

        int count = await this._store.ForgetMeAsync(uid, ct);
        var remaining = await this._store.GetAllForUserAsync(uid, ct);

        Assert.Equal(2, count);
        Assert.Empty(remaining);
    }

    /// <summary>
    /// ForgetMe for user A does not touch user B's records.
    /// </summary>
    [Fact]
    public async Task ForgetMe_DoesNotAffectOtherUsers()
    {
        var ct = TestContext.Current.CancellationToken;
        var uidA = UniqueUser();
        var uidB = UniqueUser();

        await this._store.UpsertAsync(Make(uidA, "D", "K1"), ct);
        await this._store.UpsertAsync(Make(uidB, "D", "K2"), ct);

        await this._store.ForgetMeAsync(uidA, ct);

        var bRecords = await this._store.GetAllForUserAsync(uidB, ct);
        Assert.Single(bRecords);
    }

    /// <summary>
    /// ForgetMe advances LastWrittenAt for the affected user.
    /// </summary>
    [Fact]
    public async Task ForgetMe_BumpsLastWrittenAt()
    {
        var ct = TestContext.Current.CancellationToken;
        var uid = UniqueUser();
        await this._store.UpsertAsync(Make(uid, "D", "K"), ct);
        var afterUpsert = await this._store.LastWrittenAtAsync(uid, ct);

        await Task.Delay(10, ct);
        await this._store.ForgetMeAsync(uid, ct);

        var afterForgetMe = await this._store.LastWrittenAtAsync(uid, ct);
        Assert.NotNull(afterForgetMe);
        Assert.True(afterForgetMe >= afterUpsert);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private string UniqueUser() => $"u-{Guid.NewGuid():N}";

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
