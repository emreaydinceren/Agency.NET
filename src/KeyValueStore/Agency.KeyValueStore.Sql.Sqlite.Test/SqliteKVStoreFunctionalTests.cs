
using Agency.KeyValueStore.Common;
using Agency.Sql.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Moq;

namespace Agency.KeyValueStore.Sql.Sqlite.Test;
/// <summary>
/// Integration tests for <see cref="SqliteKVStore"/> against an in-memory SQLite database.
/// No external infrastructure is required — the database lives entirely in process.
/// </summary>
public sealed class SqliteKVStoreFunctionalTests : IClassFixture<SqliteKVStoreFunctionalTests.KVStoreFixture>
{
    private readonly KVStoreFixture _fixture;

    /// <summary>
    /// Initializes a new instance of <see cref="SqliteKVStoreFunctionalTests"/> with the shared fixture.
    /// </summary>
    /// <param name="fixture">The shared fixture providing the KV store and runner.</param>
    public SqliteKVStoreFunctionalTests(KVStoreFixture fixture)
    {
        this._fixture = fixture;
    }

    // ── Schema initialization ────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="SqliteKVStore.InitializeSchemaAsync"/> creates the <c>kv_store</c> table.
    /// </summary>
    [Fact]
    public async Task InitializeSchemaAsync_CreatesTable_Succeeds()
    {
        var ds = await this._fixture.Runner.QueryAsync(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'kv_store'",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(ds.Rows);
        Assert.Equal("kv_store", ds["name", 0]);
    }

    // ── Upsert ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that a simple object can be upserted and subsequently retrieved by key.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_SimpleObject_Succeeds()
    {
        string key = this._fixture.UniqueName("simple");
        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", key, new { name = "Test Item", score = 42 }, cancellationToken: TestContext.Current.CancellationToken);

        var results = await this._fixture.KVStore.SearchAsync<dynamic>(new Query("test-user", "test-session", key, null, null, 1), TestContext.Current.CancellationToken);
        Assert.NotEmpty(results);
    }

    /// <summary>
    /// Verifies that metadata is stored alongside the value and retrieved correctly.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_WithMetadata_StoresAndRetrievesMetadata()
    {
        string key = this._fixture.UniqueName("meta");
        var metadata = new Dictionary<string, object> { ["source"] = "test", ["priority"] = "high" };

        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", key, new { description = "Item with metadata" }, metadata, TestContext.Current.CancellationToken);

        var results = await this._fixture.KVStore.SearchAsync<dynamic>(new Query("test-user", "test-session", key, null, null, 1, true), TestContext.Current.CancellationToken);
        SearchHit<dynamic>? item = results.FirstOrDefault(r => r.Key == key);

        Assert.NotNull(item);
        Assert.NotNull(item.Metadata);
        Assert.Contains("source", item.Metadata.Keys);
        Assert.Equal("test", item.Metadata["source"]);
    }

    /// <summary>
    /// Verifies that upserting the same key twice keeps only one record.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_UpdateExistingEntry_RetainsOneRecord()
    {
        string key = this._fixture.UniqueName("update");

        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", key, new { version = 1 }, cancellationToken: TestContext.Current.CancellationToken);
        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", key, new { version = 2 }, cancellationToken: TestContext.Current.CancellationToken);

        var results = await this._fixture.KVStore.SearchAsync<dynamic>(new Query("test-user", "test-session", key, null, null, 100), TestContext.Current.CancellationToken);
        Assert.Single(results, r => r.Key == key);
    }

    // ── Search ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that searching for a non-existent key returns an empty list.
    /// </summary>
    [Fact]
    public async Task SearchAsync_NoMatches_ReturnsEmptyList()
    {
        var results = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query("test-user", "test-session", "xyzabc123_does_not_exist", null, null, 10),
            TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    /// <summary>
    /// Verifies that the <see cref="Query.Limit"/> is respected and at most that many results are returned.
    /// </summary>
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

    /// <summary>
    /// Verifies that scalar metadata filtering returns only matching entries.
    /// </summary>
    [Fact]
    public async Task SearchAsync_WithMetadataFilter_FiltersCorrectly()
    {
        string key1 = this._fixture.UniqueName("cat_important");
        string key2 = this._fixture.UniqueName("cat_archived");

        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", key1, new { name = "Important item" },
            new Dictionary<string, object> { ["category"] = "important" }, TestContext.Current.CancellationToken);
        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", key2, new { name = "Archived item" },
            new Dictionary<string, object> { ["category"] = "archived" }, TestContext.Current.CancellationToken);

        var filter = new Dictionary<string, object> { ["category"] = "important" };
        var results = await this._fixture.KVStore.SearchAsync<dynamic>(new Query("test-user", "test-session", null, null, filter, 100, true), TestContext.Current.CancellationToken);

        Assert.Contains(results, r => r.Key == key1);
        Assert.DoesNotContain(results, r => r.Key == key2);
    }

    /// <summary>
    /// Verifies that array-valued metadata filtering (subset containment) works correctly.
    /// </summary>
    [Fact]
    public async Task SearchAsync_WithTagsMetadata_FindsByTag()
    {
        string keyWithMedical = this._fixture.UniqueName("tags_medical");
        string keyWithoutMedical = this._fixture.UniqueName("tags_no_medical");

        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", keyWithMedical, new { title = "Medical report" },
            new Dictionary<string, object> { ["tags"] = new[] { "document", "pdf", "medical" } }, TestContext.Current.CancellationToken);
        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", keyWithoutMedical, new { title = "General report" },
            new Dictionary<string, object> { ["tags"] = new[] { "document", "pdf" } }, TestContext.Current.CancellationToken);

        // Filter: only entries whose tags array contains "medical"
        var filter = new Dictionary<string, object> { ["tags"] = new[] { "medical" } };
        var results = await this._fixture.KVStore.SearchAsync<dynamic>(new Query("test-user", "test-session", null, null, filter, 100, true), TestContext.Current.CancellationToken);

        Assert.Contains(results, r => r.Key == keyWithMedical);
        Assert.DoesNotContain(results, r => r.Key == keyWithoutMedical);
    }

    /// <summary>
    /// Verifies that <see cref="Query.Value"/> is used as a substring filter against the serialized value column.
    /// </summary>
    [Fact]
    public async Task SearchAsync_WithValueSubstring_Matches()
    {
        string keyWithRelease = this._fixture.UniqueName("value_release");
        string keyUnrelated = this._fixture.UniqueName("value_unrelated");

        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", keyWithRelease, new { note = "important release notes" }, cancellationToken: TestContext.Current.CancellationToken);
        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", keyUnrelated, new { note = "unrelated" }, cancellationToken: TestContext.Current.CancellationToken);

        var results = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query("test-user", "test-session", null, "release", null, 100),
            TestContext.Current.CancellationToken);

        Assert.Contains(results, r => r.Key == keyWithRelease);
        Assert.DoesNotContain(results, r => r.Key == keyUnrelated);
    }

    /// <summary>
    /// Verifies that search results are ordered newest-first by <c>updated_on</c>.
    /// </summary>
    [Fact]
    public async Task SearchAsync_OrderedByUpdatedOnDesc()
    {
        string keyOld = this._fixture.UniqueName("order_old");
        string keyNew = this._fixture.UniqueName("order_new");

        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", keyOld, new { label = "older" }, cancellationToken: TestContext.Current.CancellationToken);

        // Force a 1-second gap so SQLite's datetime('now') produces a different value
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", keyNew, new { label = "newer" }, cancellationToken: TestContext.Current.CancellationToken);

        var results = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query("test-user", "test-session", null, null, null, 100),
            TestContext.Current.CancellationToken);

        int oldIndex = results.ToList().FindIndex(r => r.Key == keyOld);
        int newIndex = results.ToList().FindIndex(r => r.Key == keyNew);

        Assert.True(oldIndex >= 0, "Expected to find the older key in results.");
        Assert.True(newIndex >= 0, "Expected to find the newer key in results.");
        Assert.True(newIndex < oldIndex, $"Expected newer entry (index {newIndex}) to appear before older entry (index {oldIndex}).");
    }

    // ── Metadata listing ────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that GetMetadataAsync returns metadata entries for the specified user and session.
    /// </summary>
    [Fact]
    public async Task GetMetadataAsync_WithSessionId_ReturnsMatchingEntries()
    {
        string keyA = this._fixture.UniqueName("metadata_session_a");
        string keyB = this._fixture.UniqueName("metadata_session_b");

        await this._fixture.KVStore.UpsertAsync(
            "test-user",
            "metadata-session",
            keyA,
            new { content = "a" },
            new Dictionary<string, object> { ["source"] = "alpha" },
            TestContext.Current.CancellationToken);

        await this._fixture.KVStore.UpsertAsync(
            "test-user",
            "metadata-session",
            keyB,
            new { content = "b" },
            new Dictionary<string, object> { ["source"] = "beta" },
            TestContext.Current.CancellationToken);

        var results = await this._fixture.KVStore.GetMetadataAsync("test-user", "metadata-session", TestContext.Current.CancellationToken);

        Assert.Contains(results, r => r.Key == keyA && r.Metadata != null && Equals(r.Metadata["source"], "alpha"));
        Assert.Contains(results, r => r.Key == keyB && r.Metadata != null && Equals(r.Metadata["source"], "beta"));
    }

    /// <summary>
    /// Verifies that GetMetadataAsync with a null sessionId returns entries across sessions,
    /// including both user-global and session-scoped entries.
    /// </summary>
    [Fact]
    public async Task GetMetadataAsync_NullSessionId_ReturnsEntriesAcrossAllSessions()
    {
        string keyGlobal = this._fixture.UniqueName("metadata_scope_global");
        string keySession = this._fixture.UniqueName("metadata_scope_session");

        await this._fixture.KVStore.UpsertAsync("test-user", null, keyGlobal, new { note = "global" }, cancellationToken: TestContext.Current.CancellationToken);
        await this._fixture.KVStore.UpsertAsync("test-user", "metadata-session", keySession, new { note = "session" }, cancellationToken: TestContext.Current.CancellationToken);

        var results = await this._fixture.KVStore.GetMetadataAsync("test-user", null, TestContext.Current.CancellationToken);

        Assert.Contains(results, r => r.Key == keyGlobal && r.SessionId == null);
        Assert.Contains(results, r => r.Key == keySession && r.SessionId == "metadata-session");
    }

    /// <summary>
    /// Verifies that GetMetadataAsync returns entries whose keys are domain-prefixed and whose metadata
    /// preserves the Domain and Tags hierarchy.
    /// </summary>
    [Fact]
    public async Task GetMetadataAsync_WithDomainPrefixedKeysAndTags_ReturnsExpectedMetadata()
    {
        var sessionId = "metadata-domain-tags";

        var entities = new[]
        {
            new
            {
                Domain = "Work",
                Key = $"Work:Address:{this._fixture.UniqueName("domain_work_address")}",
                Value = new { text = "HQ address and parking instructions", category = "logistics", importance = 3 },
                Tags = new[] { "transportation", "taxes" }
            },
            new
            {
                Domain = "Work",
                Key = $"Work:ExpensePolicy:{this._fixture.UniqueName("domain_work_expense")}",
                Value = new { text = "Expense reimbursement and quarterly tax checklist", category = "finance", importance = 5 },
                Tags = new[] { "taxes", "shopping", "compliance" }
            },
            new
            {
                Domain = "School",
                Key = $"School:Address:{this._fixture.UniqueName("domain_school_address")}",
                Value = new { text = "Campus office location and public transit stop", category = "location", importance = 2 },
                Tags = new[] { "transportation", "family" }
            },
            new
            {
                Domain = "School",
                Key = $"School:Supplies:{this._fixture.UniqueName("domain_school_supplies")}",
                Value = new { text = "Back-to-school supplies and semester shopping list", category = "planning", importance = 4 },
                Tags = new[] { "shopping", "family" }
            },
            new
            {
                Domain = "Home",
                Key = $"Home:Address:{this._fixture.UniqueName("domain_home_address")}",
                Value = new { text = "Home address, nearest grocery and pharmacy", category = "household", importance = 1 },
                Tags = new[] { "family", "shopping" }
            },
            new
            {
                Domain = "Home",
                Key = $"Home:Vehicle:{this._fixture.UniqueName("domain_home_vehicle")}",
                Value = new { text = "Car registration renewal and commute notes", category = "personal", importance = 3 },
                Tags = new[] { "transportation", "taxes" }
            },
            new
            {
                Domain = "Health",
                Key = $"Health:Insurance:{this._fixture.UniqueName("domain_health_insurance")}",
                Value = new { text = "Insurance claim docs and annual deductible tracking", category = "records", importance = 5 },
                Tags = new[] { "family", "taxes", "medical" }
            }
        };

        foreach (var entity in entities)
        {
            var metadata = new Dictionary<string, object>
            {
                ["Domain"] = entity.Domain,
                ["Tags"] = entity.Tags
            };

            await this._fixture.KVStore.UpsertAsync(
                "test-user",
                sessionId,
                entity.Key,
                entity.Value,
                metadata,
                TestContext.Current.CancellationToken);
        }

        var results = await this._fixture.KVStore.GetMetadataAsync("test-user", sessionId, TestContext.Current.CancellationToken);

        Assert.True(results.Count >= entities.Length, $"Expected at least {entities.Length} results, got {results.Count}");

        foreach (var entity in entities)
        {
            var hit = Assert.Single(results, r => r.Key == entity.Key);

            Assert.NotNull(hit.Metadata);
            Assert.Equal(entity.Domain, hit.Metadata["Domain"]?.ToString());

            var tags = Assert.IsAssignableFrom<IEnumerable<object>>(hit.Metadata["Tags"])
                .Select(t => t?.ToString())
                .ToList();

            foreach (var expectedTag in entity.Tags)
            {
                Assert.Contains(expectedTag, tags);
            }
        }

        Assert.Contains(results, r => r.Key.StartsWith("Work:", StringComparison.Ordinal));
        Assert.Contains(results, r => r.Key.StartsWith("School:", StringComparison.Ordinal));
        Assert.Contains(results, r => r.Key.StartsWith("Home:", StringComparison.Ordinal));
        Assert.Contains(results, r => r.Key.StartsWith("Health:", StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies that GetMetadataAsync returns entries ordered by updated_on descending (newest first).
    /// </summary>
    [Fact]
    public async Task GetMetadataAsync_OrderedByUpdatedOnDesc()
    {
        string keyOld = this._fixture.UniqueName("metadata_order_old");
        string keyNew = this._fixture.UniqueName("metadata_order_new");

        await this._fixture.KVStore.UpsertAsync("test-user", "metadata-order", keyOld, new { seq = 1 }, cancellationToken: TestContext.Current.CancellationToken);

        // Force a 1-second gap so SQLite's datetime('now') produces a different value
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        await this._fixture.KVStore.UpsertAsync("test-user", "metadata-order", keyNew, new { seq = 2 }, cancellationToken: TestContext.Current.CancellationToken);

        var results = await this._fixture.KVStore.GetMetadataAsync("test-user", "metadata-order", TestContext.Current.CancellationToken);

        int oldIndex = results.ToList().FindIndex(r => r.Key == keyOld);
        int newIndex = results.ToList().FindIndex(r => r.Key == keyNew);

        Assert.True(oldIndex >= 0, "Expected to find the older key in metadata results.");
        Assert.True(newIndex >= 0, "Expected to find the newer key in metadata results.");
        Assert.True(newIndex < oldIndex, $"Expected newer entry (index {newIndex}) to appear before older entry (index {oldIndex}).");
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
        string key = this._fixture.UniqueName("global_session");
        await this._fixture.KVStore.UpsertAsync("test-user", null, key, new { note = "global" }, cancellationToken: TestContext.Current.CancellationToken);

        var results = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query("test-user", null, key, null, null, 1),
            TestContext.Current.CancellationToken);

        var hit = Assert.Single(results, r => r.Key == key);
        Assert.Null(hit.SessionId);
    }

    /// <summary>
    /// Verifies that a null <see cref="Query.SessionId"/> returns entries from all sessions,
    /// while a specific <see cref="Query.SessionId"/> returns only that session's entries.
    /// </summary>
    [Fact]
    public async Task SearchAsync_NullSessionId_ReturnsEntriesAcrossAllSessions()
    {
        string keyGlobal = this._fixture.UniqueName("scope_global");
        string keySession = this._fixture.UniqueName("scope_session");

        await this._fixture.KVStore.UpsertAsync("test-user", null, keyGlobal, new { note = "global" }, cancellationToken: TestContext.Current.CancellationToken);
        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", keySession, new { note = "session" }, cancellationToken: TestContext.Current.CancellationToken);

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
    /// Verifies that <see cref="IKVStore.DeleteAsync"/> with a null <c>sessionId</c> removes
    /// only the global (no-session) entry and leaves session-scoped entries intact.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_NullSessionId_RemovesOnlyGlobalEntry()
    {
        string key = this._fixture.UniqueName("global_delete");

        await this._fixture.KVStore.UpsertAsync("test-user", null, key, new { note = "global" }, cancellationToken: TestContext.Current.CancellationToken);
        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", key, new { note = "session" }, cancellationToken: TestContext.Current.CancellationToken);

        bool deleted = await this._fixture.KVStore.DeleteAsync("test-user", null, key, TestContext.Current.CancellationToken);
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
        string key = this._fixture.UniqueName("session_independence");

        await this._fixture.KVStore.UpsertAsync("test-user", null, key, new { version = "global" }, cancellationToken: TestContext.Current.CancellationToken);
        await this._fixture.KVStore.UpsertAsync("test-user", "session-a", key, new { version = "session-a" }, cancellationToken: TestContext.Current.CancellationToken);

        // Searching with null sessionId returns both entries for this key
        var allResults = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query("test-user", null, key, null, null, 100),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, allResults.Count(r => r.Key == key));

        // Deleting the global entry leaves the session-a entry intact
        await this._fixture.KVStore.DeleteAsync("test-user", null, key, TestContext.Current.CancellationToken);

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
    public sealed class KVStoreFixture : IAsyncLifetime
    {
        private readonly SqliteConnection _keepAlive;
        private readonly string _runId = Guid.NewGuid().ToString("N")[..8];

        /// <summary>
        /// Initializes a new instance of <see cref="KVStoreFixture"/>, creating an in-memory SQLite
        /// database and a keep-alive connection to hold it open for the lifetime of the fixture.
        /// </summary>
        public KVStoreFixture()
        {
            string dbName = $"kvstore_tests_{Guid.NewGuid():N}";
            string connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";

            // Keep the in-memory DB alive for the lifetime of the fixture
            this._keepAlive = new SqliteConnection(connectionString);
            this._keepAlive.Open();

            this.Runner = new SqliteRunner(connectionString);

            var logger = new Mock<ILogger<SqliteKVStore>>();
            this.KVStore = new SqliteKVStore(this.Runner, logger.Object);
        }

        /// <summary>Gets the <see cref="SqliteRunner"/> used by the fixture.</summary>
        public SqliteRunner Runner { get; }

        /// <summary>Gets the <see cref="SqliteKVStore"/> under test.</summary>
        public SqliteKVStore KVStore { get; }

        /// <summary>
        /// Returns a test-run-scoped unique name by combining a prefix with a short random suffix.
        /// </summary>
        /// <param name="prefix">The human-readable prefix for the name.</param>
        public string UniqueName(string prefix) => $"{prefix}_{this._runId}";

        /// <summary>Initializes the KV store schema before tests run.</summary>
        public async ValueTask InitializeAsync()
        {
            await this.KVStore.InitializeSchemaAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>Closes the keep-alive connection and releases the in-memory database.</summary>
        public async ValueTask DisposeAsync()
        {
            await this._keepAlive.CloseAsync();
        }
    }
}
