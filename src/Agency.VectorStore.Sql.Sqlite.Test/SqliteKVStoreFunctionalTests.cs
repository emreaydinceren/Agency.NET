using Agency.Embeddings.Common;
using Agency.Sql.Sqlite;
using Agency.VectorStore.Common;
using Agency.VectorStore.Sql.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Moq;

namespace Agency.VectorStore.Sql.Sqlite.Test;

/// <summary>
/// Integration tests for <see cref="SqliteKVStore"/> against an in-memory SQLite database.
/// No external infrastructure is required — the database lives entirely in process.
/// </summary>
public sealed class SqliteKVStoreFunctionalTests : IClassFixture<SqliteKVStoreFunctionalTests.VectorStoreFixture>
{
    private readonly VectorStoreFixture _fixture;

    public SqliteKVStoreFunctionalTests(VectorStoreFixture fixture)
    {
        this._fixture = fixture;
    }

    // ── Schema initialization ────────────────────────────────────────────────

    [Fact]
    public async Task InitializeSchemaAsync_CreatesTable_Succeeds()
    {
        var ds = await this._fixture.Runner.QueryAsync(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'semantic_kv_store'");

        Assert.Single(ds.Rows);
        Assert.Equal("semantic_kv_store", ds["name", 0]);
    }

    // ── Upsert ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertAsync_SimpleObject_Succeeds()
    {
        var key = this._fixture.UniqueName("simple");
        await this._fixture.KVStore.UpsertAsync(key, new { name = "Test Item", score = 42 });

        var results = await this._fixture.KVStore.SearchAsync<dynamic>(new Query(key, null, null, 1));
        Assert.NotEmpty(results);
    }

    [Fact]
    public async Task UpsertAsync_WithMetadata_StoresAndRetrievesMetadata()
    {
        var key = this._fixture.UniqueName("meta");
        var metadata = new Dictionary<string, object> { ["source"] = "test", ["priority"] = "high" };

        await this._fixture.KVStore.UpsertAsync(key, new { description = "Item with metadata" }, metadata);

        var results = await this._fixture.KVStore.SearchAsync<dynamic>(new Query(key, null, null, 1, true));
        var item = results.FirstOrDefault(r => r.Key == key);

        Assert.NotNull(item);
        Assert.NotNull(item.Metadata);
        Assert.Contains("source", item.Metadata.Keys);
        Assert.Equal("test", item.Metadata["source"]);
    }

    [Fact]
    public async Task UpsertAsync_UpdateExistingEntry_RetainsOneRecord()
    {
        var key = this._fixture.UniqueName("update");

        await this._fixture.KVStore.UpsertAsync(key, new { version = 1 });
        await this._fixture.KVStore.UpsertAsync(key, new { version = 2 });

        var results = await this._fixture.KVStore.SearchAsync<dynamic>(new Query(key, null, null, 100));
        Assert.Single(results, r => r.Key == key);
    }

    // ── Search ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_NoMatches_ReturnsEmptyList()
    {
        var results = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query("xyzabc123_does_not_exist", null, null, 10));

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_WithLimit_ReturnsAtMostLimitResults()
    {
        for (int i = 0; i < 5; i++)
        {
            await this._fixture.KVStore.UpsertAsync(
                this._fixture.UniqueName($"limit_{i}"), new { index = i });
        }

        var results = await this._fixture.KVStore.SearchAsync<dynamic>(new Query(null, "index", null, 2));
        Assert.True(results.Count <= 2, $"Expected at most 2 results, got {results.Count}");
    }

    [Fact]
    public async Task SearchAsync_WithMetadataFilter_FiltersCorrectly()
    {
        var key1 = this._fixture.UniqueName("cat_important");
        var key2 = this._fixture.UniqueName("cat_archived");

        await this._fixture.KVStore.UpsertAsync(key1, new { name = "Important item" },
            new Dictionary<string, object> { ["category"] = "important" });
        await this._fixture.KVStore.UpsertAsync(key2, new { name = "Archived item" },
            new Dictionary<string, object> { ["category"] = "archived" });

        var filter = new Dictionary<string, object> { ["category"] = "important" };
        var results = await this._fixture.KVStore.SearchAsync<dynamic>(new Query(null, null, filter, 100, true));

        Assert.Contains(results, r => r.Key == key1);
        Assert.DoesNotContain(results, r => r.Key == key2);
    }

    [Fact]
    public async Task SearchAsync_WithTagsMetadata_FindsByTag()
    {
        var keyWithMedical = this._fixture.UniqueName("tags_medical");
        var keyWithoutMedical = this._fixture.UniqueName("tags_no_medical");

        await this._fixture.KVStore.UpsertAsync(keyWithMedical, new { title = "Medical report" },
            new Dictionary<string, object> { ["tags"] = new[] { "document", "pdf", "medical" } });
        await this._fixture.KVStore.UpsertAsync(keyWithoutMedical, new { title = "General report" },
            new Dictionary<string, object> { ["tags"] = new[] { "document", "pdf" } });

        // Filter: only entries whose tags array contains "medical"
        var filter = new Dictionary<string, object> { ["tags"] = new[] { "medical" } };
        var results = await this._fixture.KVStore.SearchAsync<dynamic>(new Query(null, null, filter, 100, true));

        Assert.Contains(results, r => r.Key == keyWithMedical);
        Assert.DoesNotContain(results, r => r.Key == keyWithoutMedical);
    }

    [Fact]
    public async Task SearchAsync_ResultsOrderedByDistance_Succeeds()
    {
        var key1 = this._fixture.UniqueName("order_1");
        var key2 = this._fixture.UniqueName("order_2");

        await this._fixture.KVStore.UpsertAsync(key1, new { text = "apple fruit red" });
        await this._fixture.KVStore.UpsertAsync(key2, new { text = "xyz abc 123" });

        var results = await this._fixture.KVStore.SearchAsync<dynamic>(new Query(null, "apple red", null, 100));

        if (results.Count > 1)
        {
            for (int i = 1; i < results.Count; i++)
            {
                Assert.True(
                    results[i - 1].Distance <= results[i].Distance,
                    $"Results must be ordered by distance. Got {results[i - 1].Distance} then {results[i].Distance}");
            }
        }
    }

    // ── Fixture ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Shared in-memory SQLite fixture. A keep-alive connection prevents the in-memory database
    /// from being discarded between test connections (SQLite in-memory databases are destroyed when
    /// all connections to them close).
    /// </summary>
    public sealed class VectorStoreFixture : IAsyncLifetime
    {
        private readonly SqliteConnection _keepAlive;
        private readonly string _runId = Guid.NewGuid().ToString("N")[..8];

        public VectorStoreFixture()
        {
            string dbName = $"kvstore_tests_{Guid.NewGuid():N}";
            string connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";

            // Keep the in-memory DB alive for the lifetime of the fixture
            this._keepAlive = new SqliteConnection(connectionString);
            this._keepAlive.Open();
            SqliteKVStore.RegisterVectorFunctions(this._keepAlive);

            this.Runner = new SqliteRunner(connectionString, onConnectionOpen: SqliteKVStore.RegisterVectorFunctions);

            var mockGenerator = new Mock<IEmbeddingGenerator>();
            mockGenerator
                .Setup(g => g.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>((input, _) =>
                {
                    int hash = input.GetHashCode();
                    var rng = new Random(hash);
                    float[] embedding = new float[1536];
                    for (int i = 0; i < embedding.Length; i++)
                    {
                        embedding[i] = (float)rng.NextDouble();
                    }
                    return Task.FromResult((ReadOnlyMemory<float>)embedding.AsMemory());
                });

            var logger = new Mock<ILogger<SqliteKVStore>>();
            this.KVStore = new SqliteKVStore(mockGenerator.Object, this.Runner, logger.Object);
        }

        public SqliteRunner Runner { get; }
        public SqliteKVStore KVStore { get; }

        public string UniqueName(string prefix) => $"{prefix}_{this._runId}";

        public async Task InitializeAsync()
        {
            await this.KVStore.InitializeSchemaAsync(dimensions: 1536);
        }

        public async Task DisposeAsync()
        {
            await this._keepAlive.CloseAsync();
        }
    }
}
