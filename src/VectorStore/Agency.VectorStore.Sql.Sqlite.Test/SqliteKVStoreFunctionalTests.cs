using Agency.Embeddings.Common;
using Agency.Sql.Sqlite;
using Agency.VectorStore.Common;
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
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'semantic_kv_store'",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(ds.Rows);
        Assert.Equal("semantic_kv_store", ds["name", 0]);
    }

    // ── Upsert ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertAsync_SimpleObject_Succeeds()
    {
        var key = this._fixture.UniqueName("simple");
        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", key, new { name = "Test Item", score = 42 }, cancellationToken: TestContext.Current.CancellationToken);

        var results = await this._fixture.KVStore.SearchAsync<dynamic>(new Query("test-user", "test-session", key, null, null, 1), TestContext.Current.CancellationToken);
        Assert.NotEmpty(results);
    }

    [Fact]
    public async Task UpsertAsync_WithMetadata_StoresAndRetrievesMetadata()
    {
        var key = this._fixture.UniqueName("meta");
        var metadata = new Dictionary<string, object> { ["source"] = "test", ["priority"] = "high" };

        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", key, new { description = "Item with metadata" }, metadata, cancellationToken: TestContext.Current.CancellationToken);

        var results = await this._fixture.KVStore.SearchAsync<dynamic>(new Query("test-user", "test-session", key, null, null, 1, true), TestContext.Current.CancellationToken);
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

        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", key, new { version = 1 }, cancellationToken: TestContext.Current.CancellationToken);
        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", key, new { version = 2 }, cancellationToken: TestContext.Current.CancellationToken);

        var results = await this._fixture.KVStore.SearchAsync<dynamic>(new Query("test-user", "test-session", key, null, null, 100), TestContext.Current.CancellationToken);
        Assert.Single(results, r => r.Key == key);
    }

    // ── Search ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_NoMatches_ReturnsEmptyList()
    {
        var results = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query("test-user", "test-session", "xyzabc123_does_not_exist", null, null, 10),
            TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_WithLimit_ReturnsAtMostLimitResults()
    {
        for (int i = 0; i < 5; i++)
        {
            await this._fixture.KVStore.UpsertAsync(
                "test-user", "test-session", this._fixture.UniqueName($"limit_{i}"), new { index = i }, cancellationToken: TestContext.Current.CancellationToken);
        }

        var results = await this._fixture.KVStore.SearchAsync<dynamic>(new Query("test-user", "test-session", null, "index", null, 2), TestContext.Current.CancellationToken);
        Assert.True(results.Count <= 2, $"Expected at most 2 results, got {results.Count}");
    }

    [Fact]
    public async Task SearchAsync_WithMetadataFilter_FiltersCorrectly()
    {
        var key1 = this._fixture.UniqueName("cat_important");
        var key2 = this._fixture.UniqueName("cat_archived");

        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", key1, new { name = "Important item" },
            new Dictionary<string, object> { ["category"] = "important" }, cancellationToken: TestContext.Current.CancellationToken);
        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", key2, new { name = "Archived item" },
            new Dictionary<string, object> { ["category"] = "archived" }, cancellationToken: TestContext.Current.CancellationToken);

        var filter = new Dictionary<string, object> { ["category"] = "important" };
        var results = await this._fixture.KVStore.SearchAsync<dynamic>(new Query("test-user", "test-session", null, null, filter, 100, true), TestContext.Current.CancellationToken);

        Assert.Contains(results, r => r.Key == key1);
        Assert.DoesNotContain(results, r => r.Key == key2);
    }

    [Fact]
    public async Task SearchAsync_WithTagsMetadata_FindsByTag()
    {
        var keyWithMedical = this._fixture.UniqueName("tags_medical");
        var keyWithoutMedical = this._fixture.UniqueName("tags_no_medical");

        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", keyWithMedical, new { title = "Medical report" },
            new Dictionary<string, object> { ["tags"] = new[] { "document", "pdf", "medical" } }, cancellationToken: TestContext.Current.CancellationToken);
        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", keyWithoutMedical, new { title = "General report" },
            new Dictionary<string, object> { ["tags"] = new[] { "document", "pdf" } }, cancellationToken: TestContext.Current.CancellationToken);

        // Filter: only entries whose tags array contains "medical"
        var filter = new Dictionary<string, object> { ["tags"] = new[] { "medical" } };
        var results = await this._fixture.KVStore.SearchAsync<dynamic>(new Query("test-user", "test-session", null, null, filter, 100, true), TestContext.Current.CancellationToken);

        Assert.Contains(results, r => r.Key == keyWithMedical);
        Assert.DoesNotContain(results, r => r.Key == keyWithoutMedical);
    }

    [Fact]
    public async Task SearchAsync_ResultsOrderedByDistance_Succeeds()
    {
        var key1 = this._fixture.UniqueName("order_1");
        var key2 = this._fixture.UniqueName("order_2");

        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", key1, new { text = "apple fruit red" }, cancellationToken: TestContext.Current.CancellationToken);
        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", key2, new { text = "xyz abc 123" }, cancellationToken: TestContext.Current.CancellationToken);

        var results = await this._fixture.KVStore.SearchAsync<dynamic>(new Query("test-user", "test-session", null, "apple red", null, 100), TestContext.Current.CancellationToken);

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

    // ── Null sessionId (global / no-session) ────────────────────────────────

    /// <summary>
    /// Verifies that an entry upserted with a null <c>sessionId</c> produces a
    /// <see cref="SearchHit{TValue}"/> whose <see cref="SearchHit{TValue}.SessionId"/> is
    /// <see langword="null"/> when retrieved.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_NullSessionId_SearchHitHasNullSessionId()
    {
        var key = this._fixture.UniqueName("global_session");
        await this._fixture.KVStore.UpsertAsync("test-user", null, key, new { text = "global entry" }, cancellationToken: TestContext.Current.CancellationToken);

        var results = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query("test-user", null, key, null, null, 1),
            TestContext.Current.CancellationToken);

        var hit = Assert.Single(results, r => r.Key == key);
        Assert.Null(hit.SessionId);
        Assert.Equal("test-user", hit.UserId);
    }

    /// <summary>
    /// Verifies that a null <see cref="Query.SessionId"/> returns entries from all sessions,
    /// while a specific <see cref="Query.SessionId"/> returns only that session's entries.
    /// </summary>
    [Fact]
    public async Task SearchAsync_NullSessionId_ReturnsEntriesAcrossAllSessions()
    {
        var keyGlobal = this._fixture.UniqueName("scope_global");
        var keySession = this._fixture.UniqueName("scope_session");

        await this._fixture.KVStore.UpsertAsync("test-user", null, keyGlobal, new { text = "global" }, cancellationToken: TestContext.Current.CancellationToken);
        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", keySession, new { text = "session" }, cancellationToken: TestContext.Current.CancellationToken);

        // Null sessionId → both entries visible
        var allResults = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query("test-user", null, null, null, null, 100),
            TestContext.Current.CancellationToken);

        Assert.Contains(allResults, r => r.Key == keyGlobal);
        Assert.Contains(allResults, r => r.Key == keySession);

        // Specific sessionId → only session-scoped entry visible
        var sessionResults = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query("test-user", "test-session", null, null, null, 100),
            TestContext.Current.CancellationToken);

        Assert.Contains(sessionResults, r => r.Key == keySession);
        Assert.DoesNotContain(sessionResults, r => r.Key == keyGlobal);
    }

    /// <summary>
    /// Verifies that <see cref="IVectorStore.DeleteAsync"/> with a null <c>sessionId</c> removes
    /// only the global (no-session) entry and leaves session-scoped entries intact.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_NullSessionId_RemovesOnlyGlobalEntry()
    {
        var key = this._fixture.UniqueName("global_delete");

        await this._fixture.KVStore.UpsertAsync("test-user", null, key, new { text = "global" }, cancellationToken: TestContext.Current.CancellationToken);
        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", key, new { text = "session" }, cancellationToken: TestContext.Current.CancellationToken);

        bool deleted = await this._fixture.KVStore.DeleteAsync("test-user", null, key, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(deleted);

        // Global entry is gone
        var globalResults = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query("test-user", null, key, null, null, 100),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(globalResults, r => r.Key == key && r.SessionId == null);

        // Session-scoped entry is still there
        var sessionResults = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query("test-user", "test-session", key, null, null, 1),
            TestContext.Current.CancellationToken);

        Assert.Contains(sessionResults, r => r.Key == key);
    }

    /// <summary>
    /// Verifies that a null-session entry and an explicit-session entry sharing the same user
    /// and key are stored as independent rows under the compound primary key.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_NullAndExplicitSessionId_AreIndependentEntries()
    {
        var key = this._fixture.UniqueName("session_independence");

        await this._fixture.KVStore.UpsertAsync("test-user", null, key, new { text = "global version" }, cancellationToken: TestContext.Current.CancellationToken);
        await this._fixture.KVStore.UpsertAsync("test-user", "session-a", key, new { text = "session-a version" }, cancellationToken: TestContext.Current.CancellationToken);

        // Searching with null sessionId returns both entries for this key
        var allResults = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query("test-user", null, key, null, null, 100),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, allResults.Count(r => r.Key == key));

        // Deleting the global entry leaves the session-a entry intact
        await this._fixture.KVStore.DeleteAsync("test-user", null, key, cancellationToken: TestContext.Current.CancellationToken);

        var afterDelete = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query("test-user", null, key, null, null, 100),
            TestContext.Current.CancellationToken);

        var remaining = afterDelete.Where(r => r.Key == key).ToList();
        Assert.Single(remaining);
        Assert.Equal("session-a", remaining[0].SessionId);
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

        public async ValueTask InitializeAsync()
        {
            await this.KVStore.InitializeSchemaAsync(dimensions: 1536, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await this._keepAlive.CloseAsync();
        }
    }
}
