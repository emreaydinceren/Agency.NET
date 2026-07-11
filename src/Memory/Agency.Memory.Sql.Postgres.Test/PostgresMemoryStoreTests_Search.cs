using Agency.Embeddings.Common;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using MemoryRecord = Agency.Memory.Common.Records.Record;

namespace Agency.Memory.Sql.Postgres.Test;

/// <summary>
/// Functional tests for <see cref="PostgresMemoryStore.SearchAsync"/>.
/// Require a running PostgreSQL instance.
/// </summary>
[Trait("Category", "Functional")]
public sealed class PostgresMemoryStoreTests_Search : IAsyncLifetime
{
    private NpgsqlDataSource _dataSource = default!;
    private PostgresMemoryStore _store = default!;
    private readonly string _runId = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Initialises the store for each test.</summary>
    public async ValueTask InitializeAsync()
    {
        this._dataSource = TestHelpers.BuildDataSource<PostgresMemoryStoreTests_Search>();

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

    // ── ordering ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Search returns results ordered by cosine similarity (most similar first, i.e. lowest distance).
    /// </summary>
    [Fact]
    public async Task Search_OrdersByCosineDistance_LowestFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        var uid = UniqueUser();

        // Build two embeddings with known similarity to [1,0,0,...] query
        var query = UnitVec(0, 1536);
        var close = Lerp(query, UnitVec(1, 1536), 0.05f); // very similar to query
        var far = UnitVec(1, 1536);                        // orthogonal to query

        await this._store.UpsertAsync(Make(uid, "D", "Close", embedding: close), ct);
        await this._store.UpsertAsync(Make(uid, "D", "Far", embedding: far), ct);

        var results = await this._store.SearchAsync(new SearchQuery(uid, query, TopK: 10), ct);

        Assert.True(results.Count >= 2, $"Expected at least 2 hits, got {results.Count}");
        Assert.True(results[0].Similarity >= results[1].Similarity,
            $"Expected descending similarity, got {results[0].Similarity} then {results[1].Similarity}");
    }

    // ── content-type filter ──────────────────────────────────────────────────

    /// <summary>
    /// Search filtered by ContentType.Fact returns only Fact records.
    /// </summary>
    [Fact]
    public async Task Search_FilterByContentType_ExcludesOthers()
    {
        var ct = TestContext.Current.CancellationToken;
        var uid = UniqueUser();
        var emb = UnitVec(0, 1536);

        await this._store.UpsertAsync(Make(uid, "D", "F1", contentType: ContentType.Fact, embedding: emb), ct);
        await this._store.UpsertAsync(Make(uid, "D", "M1", contentType: ContentType.Memory, embedding: emb), ct);

        var results = await this._store.SearchAsync(
            new SearchQuery(uid, emb, TopK: 10, ContentType: ContentType.Fact), ct);

        Assert.NotEmpty(results);
        Assert.All(results, h => Assert.Equal(ContentType.Fact, h.Record.ContentType));
    }

    // ── domain filter ────────────────────────────────────────────────────────

    /// <summary>
    /// Search filtered by Domain returns only records from that domain.
    /// </summary>
    [Fact]
    public async Task Search_FilterByDomain_ExcludesOthers()
    {
        var ct = TestContext.Current.CancellationToken;
        var uid = UniqueUser();
        var emb = UnitVec(0, 1536);

        await this._store.UpsertAsync(Make(uid, "Target", "K1", embedding: emb), ct);
        await this._store.UpsertAsync(Make(uid, "Other", "K2", embedding: emb), ct);

        var results = await this._store.SearchAsync(
            new SearchQuery(uid, emb, TopK: 10, Domain: "Target"), ct);

        Assert.NotEmpty(results);
        Assert.All(results, h => Assert.Equal("Target", h.Record.Domain));
    }

    // ── topK ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Search respects the TopK limit.
    /// </summary>
    [Fact]
    public async Task Search_RespectsTopK()
    {
        var ct = TestContext.Current.CancellationToken;
        var uid = UniqueUser();
        var emb = UnitVec(0, 1536);

        for (int i = 0; i < 5; i++)
        {
            await this._store.UpsertAsync(Make(uid, "D", $"K{i}", embedding: emb), ct);
        }

        var results = await this._store.SearchAsync(new SearchQuery(uid, emb, TopK: 2), ct);

        Assert.True(results.Count <= 2, $"Expected at most 2 results, got {results.Count}");
    }

    // ── last_accessed_at ─────────────────────────────────────────────────────

    /// <summary>
    /// After a search the <c>last_accessed_at</c> column is updated asynchronously for hit rows.
    /// </summary>
    [Fact]
    public async Task Search_BumpsLastAccessedAt_AsynchronouslyForHits()
    {
        var ct = TestContext.Current.CancellationToken;
        var uid = UniqueUser();
        var emb = UnitVec(0, 1536);
        const string key = "Accessed";

        var inserted = await this._store.UpsertAsync(Make(uid, "D", key, embedding: emb), ct);
        Assert.Null(inserted.LastAccessedAt);

        await this._store.SearchAsync(new SearchQuery(uid, emb, TopK: 5), ct);

        // Poll until last_accessed_at is set (fire-and-forget; allow up to 5 s)
        MemoryRecord? updated = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            updated = await this._store.GetByKeyAsync(uid, null, "D", key, ct);
            if (updated?.LastAccessedAt is not null)
            {
                break;
            }

            await Task.Delay(100, ct);
        }

        Assert.NotNull(updated?.LastAccessedAt);
    }

    // ── empty store ──────────────────────────────────────────────────────────

    /// <summary>
    /// Searching an empty store returns an empty list.
    /// </summary>
    [Fact]
    public async Task Search_EmptyStore_ReturnsEmptyList()
    {
        var ct = TestContext.Current.CancellationToken;
        var uid = $"nobody-{this._runId}";
        var emb = UnitVec(0, 1536);

        var results = await this._store.SearchAsync(new SearchQuery(uid, emb, TopK: 10), ct);

        Assert.Empty(results);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string UniqueUser() => $"u-{Guid.NewGuid():N}";

    private string UniqueKey(string prefix) => $"{prefix}_{this._runId}";

    private static MemoryRecord Make(
        string userId,
        string domain,
        string key,
        ContentType contentType = ContentType.Fact,
        ReadOnlyMemory<float> embedding = default)
    {
        var now = DateTimeOffset.UtcNow;
        return MemoryRecord.Create(
            id: Guid.NewGuid().ToString(),
            userId: userId,
            sessionId: null,
            contentType: contentType,
            domain: domain,
            key: key,
            title: $"{domain}/{key}",
            value: $"Value of {key}",
            tags: [],
            importance: 0.5,
            createdAt: now,
            updatedAt: now,
            embedding: embedding.IsEmpty ? UnitVec(0, 1536) : embedding);
    }

    /// <summary>Returns a unit vector with all weight on the <paramref name="dimension"/> axis.</summary>
    private static ReadOnlyMemory<float> UnitVec(int dimension, int length)
    {
        var arr = new float[length];
        arr[dimension] = 1.0f;
        return arr.AsMemory();
    }

    /// <summary>Linear interpolation between two vectors.</summary>
    private static ReadOnlyMemory<float> Lerp(ReadOnlyMemory<float> a, ReadOnlyMemory<float> b, float t)
    {
        var aSpan = a.Span;
        var bSpan = b.Span;
        var result = new float[aSpan.Length];
        float norm = 0f;
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = aSpan[i] * (1 - t) + bSpan[i] * t;
            norm += result[i] * result[i];
        }

        norm = MathF.Sqrt(norm);
        if (norm > 0)
        {
            for (int i = 0; i < result.Length; i++)
            {
                result[i] /= norm;
            }
        }

        return result.AsMemory();
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
