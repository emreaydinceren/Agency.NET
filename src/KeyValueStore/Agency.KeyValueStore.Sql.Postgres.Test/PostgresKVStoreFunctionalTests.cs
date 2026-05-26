
using Agency.KeyValueStore.Common;
using Agency.Sql.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace Agency.KeyValueStore.Sql.Postgres.Test;
/// <summary>
/// Functional tests that run against the real PostgreSQL instance defined in docker-compose.yml.
/// Requires the container to be running: docker compose up -d
/// Connection: Host=llm-host.example;Port=5432;Username=dev_user;Password=dev_password;Database=dev_db
/// Run with: dotnet test --filter "Category=Functional"
/// Skip with: dotnet test --filter "Category!=Functional"
/// </summary>
[Trait("Category", "Functional")]
public sealed class PostgresKVStoreFunctionalTests : IClassFixture<PostgresKVStoreFunctionalTests.KVStoreFixture>
{
    private readonly KVStoreFixture _fixture;

    /// <summary>
    /// Creates the test class with its shared database fixture.
    /// </summary>
    public PostgresKVStoreFunctionalTests(KVStoreFixture fixture)
    {
        this._fixture = fixture;
    }

    // ── Functional: Schema initialization ───────────────────────────────────

    /// <summary>
    /// Verifies that InitializeSchemaAsync creates the required table and indexes.
    /// </summary>
    [Fact]
    public async Task InitializeSchemaAsync_CreatesTable_Succeeds()
    {
        // Table is already created by fixture; verify it works by upserting and recalling a row.
        var kvStore = this._fixture.KVStore;

        var testKey = this._fixture.UniqueName("init_check");
        var testValue = new { message = "initialization test" };
        await kvStore.UpsertAsync("test-user", "test-session", testKey, testValue, cancellationToken: TestContext.Current.CancellationToken);

        var query = new Query("test-user", "test-session", testKey, null, null, 1);
        var results = await kvStore.SearchAsync<dynamic>(query, TestContext.Current.CancellationToken);

        Assert.NotEmpty(results);
    }

    // ── Functional: Upsert ──────────────────────────────────────────────────

    /// <summary>
    /// Verifies that UpsertAsync stores and retrieves a simple object.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_SimpleObject_Succeeds()
    {
        var kvStore = this._fixture.KVStore;
        var key = this._fixture.UniqueName("simple_object");
        var value = new { name = "Test Item", score = 42 };

        await kvStore.UpsertAsync("test-user", "test-session", key, value, cancellationToken: TestContext.Current.CancellationToken);

        var query = new Query("test-user", "test-session", key, null, null, 100);
        var results = await kvStore.SearchAsync<dynamic>(query, TestContext.Current.CancellationToken);
        Assert.NotEmpty(results);
    }

    /// <summary>
    /// Verifies that UpsertAsync with metadata stores and retrieves metadata correctly.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_WithMetadata_StoresAndRetrievesMetadata()
    {
        var kvStore = this._fixture.KVStore;
        var key = this._fixture.UniqueName("with_metadata");
        var value = new { description = "Item with metadata" };
        var metadata = new Dictionary<string, object> { ["source"] = "test", ["priority"] = "high" };

        await kvStore.UpsertAsync("test-user", "test-session", key, value, metadata, TestContext.Current.CancellationToken);

        var query = new Query("test-user", "test-session", key, null, null, 100);
        var results = await kvStore.SearchAsync<dynamic>(query, TestContext.Current.CancellationToken);

        var item = results.FirstOrDefault(r => r.Key == key);
        Assert.NotNull(item);
        Assert.NotNull(item.Metadata);
        Assert.Contains("source", item.Metadata.Keys);
        Assert.Equal("test", item.Metadata["source"]);
    }

    /// <summary>
    /// Verifies that UpsertAsync updates existing entries, retaining a single row per key.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_UpdateExistingEntry_Succeeds()
    {
        var kvStore = this._fixture.KVStore;
        var key = this._fixture.UniqueName("update_test");
        var value1 = new { version = 1, message = "First version" };
        var value2 = new { version = 2, message = "Updated version" };

        await kvStore.UpsertAsync("test-user", "test-session", key, value1, cancellationToken: TestContext.Current.CancellationToken);
        await kvStore.UpsertAsync("test-user", "test-session", key, value2, cancellationToken: TestContext.Current.CancellationToken);

        var query = new Query("test-user", "test-session", key, null, null, 100);
        var results = await kvStore.SearchAsync<dynamic>(query, TestContext.Current.CancellationToken);
        var items = results.Where(r => r.Key == key).ToList();

        Assert.Single(items);
    }

    // ── Functional: Search ──────────────────────────────────────────────────

    /// <summary>
    /// Verifies that SearchAsync returns empty results when no matches exist.
    /// </summary>
    [Fact]
    public async Task SearchAsync_NoMatches_ReturnsEmptyList()
    {
        var kvStore = this._fixture.KVStore;

        await this._fixture.ClearStoreAsync(TestContext.Current.CancellationToken);

        var query = new Query("test-user", "test-session", "xyzabc123nonexistent", null, null, 10);
        var results = await kvStore.SearchAsync<dynamic>(query, TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    /// <summary>
    /// Verifies that SearchAsync respects the limit parameter.
    /// </summary>
    [Fact]
    public async Task SearchAsync_WithLimit_ReturnsAtMostLimitResults()
    {
        var kvStore = this._fixture.KVStore;

        for (int i = 0; i < 5; i++)
        {
            var key = this._fixture.UniqueName($"limit_test_{i}");
            await kvStore.UpsertAsync("test-user", "test-session", key, new { index = i }, cancellationToken: TestContext.Current.CancellationToken);
        }

        var query = new Query("test-user", "test-session", null, null, null, 2);
        var results = await kvStore.SearchAsync<dynamic>(query, TestContext.Current.CancellationToken);

        Assert.True(results.Count <= 2, $"Expected at most 2 results, got {results.Count}");
    }

    /// <summary>
    /// Verifies that SearchAsync with metadata filter returns only matching items.
    /// </summary>
    [Fact]
    public async Task SearchAsync_WithMetadataFilter_FiltersCorrectly()
    {
        var kvStore = this._fixture.KVStore;

        var key1 = this._fixture.UniqueName("filter_test_1");
        var key2 = this._fixture.UniqueName("filter_test_2");

        var metadata1 = new Dictionary<string, object> { ["category"] = "important" };
        var metadata2 = new Dictionary<string, object> { ["category"] = "archived" };

        await kvStore.UpsertAsync("test-user", "test-session", key1, new { name = "Important item" }, metadata1, TestContext.Current.CancellationToken);
        await kvStore.UpsertAsync("test-user", "test-session", key2, new { name = "Archived item" }, metadata2, TestContext.Current.CancellationToken);

        var filterDict = new Dictionary<string, object> { ["category"] = "important" };
        var query = new Query("test-user", "test-session", null, null, filterDict, 100);
        var results = await kvStore.SearchAsync<dynamic>(query, TestContext.Current.CancellationToken);

        foreach (var result in results)
        {
            if (result.Metadata != null && result.Metadata.ContainsKey("category"))
            {
                Assert.Equal("important", result.Metadata["category"]);
            }
        }
    }

    /// <summary>
    /// Verifies that a document tagged with multiple tags can be found by searching for a single tag,
    /// while entries that do not carry that tag are excluded.
    /// Tags are stored as a JSON array under the "tags" metadata key. PostgreSQL's JSONB containment
    /// operator (<c>@&gt;</c>) treats array matching as subset checking.
    /// </summary>
    [Fact]
    public async Task SearchAsync_WithTagsMetadata_FindsByTag()
    {
        var kvStore = this._fixture.KVStore;

        var keyWithMedical = this._fixture.UniqueName("tags_medical");
        var keyWithoutMedical = this._fixture.UniqueName("tags_no_medical");

        var tagsWithMedical = new Dictionary<string, object> { ["tags"] = new[] { "document", "pdf", "medical" } };
        var tagsWithoutMedical = new Dictionary<string, object> { ["tags"] = new[] { "document", "pdf" } };

        await kvStore.UpsertAsync("test-user", "test-session", keyWithMedical, new { title = "Medical report" }, tagsWithMedical, TestContext.Current.CancellationToken);
        await kvStore.UpsertAsync("test-user", "test-session", keyWithoutMedical, new { title = "General report" }, tagsWithoutMedical, TestContext.Current.CancellationToken);

        var filter = new Dictionary<string, object> { ["tags"] = new[] { "medical" } };
        var query = new Query("test-user", "test-session", null, null, filter, 100, true);
        var results = await kvStore.SearchAsync<dynamic>(query, TestContext.Current.CancellationToken);

        Assert.Contains(results, r => r.Key == keyWithMedical);
        Assert.DoesNotContain(results, r => r.Key == keyWithoutMedical);
    }

    /// <summary>
    /// Verifies that SearchAsync with a value substring filter returns only entries whose serialized
    /// value contains the specified substring (case-insensitive ILIKE match).
    /// </summary>
    [Fact]
    public async Task SearchAsync_WithValueSubstring_Matches()
    {
        var kvStore = this._fixture.KVStore;

        var keyMatch = this._fixture.UniqueName("substring_match");
        var keyNoMatch = this._fixture.UniqueName("substring_nomatch");

        await kvStore.UpsertAsync("test-user", "test-session", keyMatch, new { note = "important release notes" }, cancellationToken: TestContext.Current.CancellationToken);
        await kvStore.UpsertAsync("test-user", "test-session", keyNoMatch, new { note = "unrelated" }, cancellationToken: TestContext.Current.CancellationToken);

        var query = new Query("test-user", "test-session", null, "release", null, 100);
        var results = await kvStore.SearchAsync<dynamic>(query, TestContext.Current.CancellationToken);

        Assert.Contains(results, r => r.Key == keyMatch);
        Assert.DoesNotContain(results, r => r.Key == keyNoMatch);
    }

    /// <summary>
    /// Verifies that SearchAsync returns results ordered by <c>updated_on DESC</c> (newest first).
    /// </summary>
    [Fact]
    public async Task SearchAsync_OrderedByUpdatedOnDesc()
    {
        var kvStore = this._fixture.KVStore;

        var keyOld = this._fixture.UniqueName("order_old");
        var keyNew = this._fixture.UniqueName("order_new");

        await kvStore.UpsertAsync("test-user", "test-session", keyOld, new { seq = 1 }, cancellationToken: TestContext.Current.CancellationToken);

        // Small delay to ensure updated_on differs
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await kvStore.UpsertAsync("test-user", "test-session", keyNew, new { seq = 2 }, cancellationToken: TestContext.Current.CancellationToken);

        var query = new Query("test-user", "test-session", null, null, null, 100);
        var results = await kvStore.SearchAsync<dynamic>(query, TestContext.Current.CancellationToken);

        var oldIndex = results.ToList().FindIndex(r => r.Key == keyOld);
        var newIndex = results.ToList().FindIndex(r => r.Key == keyNew);

        Assert.True(oldIndex >= 0, "Old key not found in results");
        Assert.True(newIndex >= 0, "New key not found in results");
        Assert.True(newIndex < oldIndex, $"Expected newer entry (index {newIndex}) to appear before older entry (index {oldIndex})");
    }

    // ── Functional: Metadata listing ───────────────────────────────────────

    /// <summary>
    /// Verifies that GetMetadataAsync returns metadata entries for the specified user and session.
    /// </summary>
    [Fact]
    public async Task GetMetadataAsync_WithSessionId_ReturnsMatchingEntries()
    {
        var kvStore = this._fixture.KVStore;

        var keyA = this._fixture.UniqueName("metadata_session_a");
        var keyB = this._fixture.UniqueName("metadata_session_b");

        await kvStore.UpsertAsync(
            "test-user",
            "metadata-session",
            keyA,
            new { content = "a" },
            new Dictionary<string, object> { ["source"] = "alpha" },
            TestContext.Current.CancellationToken);

        await kvStore.UpsertAsync(
            "test-user",
            "metadata-session",
            keyB,
            new { content = "b" },
            new Dictionary<string, object> { ["source"] = "beta" },
            TestContext.Current.CancellationToken);

        var results = await kvStore.GetMetadataAsync("test-user", "metadata-session", TestContext.Current.CancellationToken);

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
        var kvStore = this._fixture.KVStore;

        var keyGlobal = this._fixture.UniqueName("metadata_scope_global");
        var keySession = this._fixture.UniqueName("metadata_scope_session");

        await kvStore.UpsertAsync("test-user", null, keyGlobal, new { note = "global" }, cancellationToken: TestContext.Current.CancellationToken);
        await kvStore.UpsertAsync("test-user", "metadata-session", keySession, new { note = "session" }, cancellationToken: TestContext.Current.CancellationToken);

        var results = await kvStore.GetMetadataAsync("test-user", null, TestContext.Current.CancellationToken);

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
        var kvStore = this._fixture.KVStore;
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

            await kvStore.UpsertAsync(
                "test-user",
                sessionId,
                entity.Key,
                entity.Value,
                metadata,
                TestContext.Current.CancellationToken);
        }

        var results = await kvStore.GetMetadataAsync("test-user", sessionId, TestContext.Current.CancellationToken);

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
        var kvStore = this._fixture.KVStore;

        var keyOld = this._fixture.UniqueName("metadata_order_old");
        var keyNew = this._fixture.UniqueName("metadata_order_new");

        await kvStore.UpsertAsync("test-user", "metadata-order", keyOld, new { seq = 1 }, cancellationToken: TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await kvStore.UpsertAsync("test-user", "metadata-order", keyNew, new { seq = 2 }, cancellationToken: TestContext.Current.CancellationToken);

        var results = await kvStore.GetMetadataAsync("test-user", "metadata-order", TestContext.Current.CancellationToken);

        var oldIndex = results.ToList().FindIndex(r => r.Key == keyOld);
        var newIndex = results.ToList().FindIndex(r => r.Key == keyNew);

        Assert.True(oldIndex >= 0, "Old key not found in metadata results");
        Assert.True(newIndex >= 0, "New key not found in metadata results");
        Assert.True(newIndex < oldIndex, $"Expected newer entry (index {newIndex}) to appear before older entry (index {oldIndex})");
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
        var kvStore = this._fixture.KVStore;
        string key = this._fixture.UniqueName("global_session");
        await kvStore.UpsertAsync("test-user", null, key, new { note = "global" }, cancellationToken: TestContext.Current.CancellationToken);

        var results = await kvStore.SearchAsync<dynamic>(
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
        var kvStore = this._fixture.KVStore;
        string keyGlobal = this._fixture.UniqueName("scope_global");
        string keySession = this._fixture.UniqueName("scope_session");

        await kvStore.UpsertAsync("test-user", null, keyGlobal, new { note = "global" }, cancellationToken: TestContext.Current.CancellationToken);
        await kvStore.UpsertAsync("test-user", "test-session", keySession, new { note = "session" }, cancellationToken: TestContext.Current.CancellationToken);

        // Null sessionId → both entries visible
        var allResults = await kvStore.SearchAsync<dynamic>(
            new Query("test-user", null, null, null, null, 100),
            TestContext.Current.CancellationToken);

        Assert.Contains(allResults, r => r.Key == keyGlobal);
        Assert.Contains(allResults, r => r.Key == keySession);

        // Specific sessionId → only session-scoped entry visible
        var sessionResults = await kvStore.SearchAsync<dynamic>(
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
        var kvStore = this._fixture.KVStore;
        string key = this._fixture.UniqueName("global_delete");

        await kvStore.UpsertAsync("test-user", null, key, new { note = "global" }, cancellationToken: TestContext.Current.CancellationToken);
        await kvStore.UpsertAsync("test-user", "test-session", key, new { note = "session" }, cancellationToken: TestContext.Current.CancellationToken);

        bool deleted = await kvStore.DeleteAsync("test-user", null, key, TestContext.Current.CancellationToken);
        Assert.True(deleted);

        // Global entry is gone
        var globalResults = await kvStore.SearchAsync<dynamic>(
            new Query("test-user", null, key, null, null, 100),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(globalResults, r => r.Key == key && r.SessionId == null);

        // Session-scoped entry is still there
        var sessionResults = await kvStore.SearchAsync<dynamic>(
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
        var kvStore = this._fixture.KVStore;
        string key = this._fixture.UniqueName("session_independence");

        await kvStore.UpsertAsync("test-user", null, key, new { version = "global" }, cancellationToken: TestContext.Current.CancellationToken);
        await kvStore.UpsertAsync("test-user", "session-a", key, new { version = "session-a" }, cancellationToken: TestContext.Current.CancellationToken);

        // Searching with null sessionId returns both entries for this key
        var allResults = await kvStore.SearchAsync<dynamic>(
            new Query("test-user", null, key, null, null, 100),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, allResults.Count(r => r.Key == key));

        // Deleting the global entry leaves the session-a entry intact
        await kvStore.DeleteAsync("test-user", null, key, TestContext.Current.CancellationToken);

        var afterDelete = await kvStore.SearchAsync<dynamic>(
            new Query("test-user", null, key, null, null, 100),
            TestContext.Current.CancellationToken);

        var remaining = afterDelete.Where(r => r.Key == key).ToList();
        Assert.Single(remaining);
        Assert.Equal("session-a", remaining[0].SessionId);
    }

    // ── Fixture ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Shared KV store fixture for PostgreSQL integration tests.
    /// </summary>
    public sealed class KVStoreFixture : IAsyncLifetime
    {
        private readonly PostgreSqlRunner _sqlRunner;

        /// <summary>
        /// Initializes a new instance of <see cref="KVStoreFixture"/> by reading the connection string
        /// from user secrets (<c>ConnectionStrings:PostgreSql</c>) or environment variables.
        /// </summary>
        public KVStoreFixture()
        {
            var config = new ConfigurationBuilder()
                .AddUserSecrets<PostgresKVStoreFunctionalTests>()
                .AddEnvironmentVariables()
                .Build();

            var connectionString = config.GetConnectionString("PostgreSql");

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "Connection string is not configured. Please set it in user secrets or environment variables with the key 'ConnectionStrings:PostgreSql'.");
            }

            this._sqlRunner = new PostgreSqlRunner(connectionString);
        }

        private readonly string _runId = Guid.NewGuid().ToString("N")[..8];

        /// <summary>
        /// Gets the shared PostgreSQL KV store instance.
        /// </summary>
        public PostgresKVStore KVStore { get; private set; } = default!;

        /// <summary>
        /// Returns a unique key scoped to this test run.
        /// </summary>
        /// <param name="prefix">The prefix for the unique name.</param>
        public string UniqueName(string prefix) => $"{prefix}_{this._runId}";

        /// <summary>
        /// Initializes the KV store schema and tables.
        /// </summary>
        public async ValueTask InitializeAsync()
        {
            var logger = new Mock<ILogger<PostgresKVStore>>();
            this.KVStore = new PostgresKVStore(this._sqlRunner, logger.Object);

            await this.KVStore.InitializeSchemaAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Cleans up test resources.
        /// </summary>
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        /// <summary>
        /// Truncates the <c>kv_store</c> table to isolate tests that require an empty store.
        /// </summary>
        /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
        public async Task ClearStoreAsync(CancellationToken cancellationToken = default)
        {
            await this._sqlRunner.ExecuteAsync("TRUNCATE TABLE kv_store;", cancellationToken: cancellationToken);
        }
    }
}
