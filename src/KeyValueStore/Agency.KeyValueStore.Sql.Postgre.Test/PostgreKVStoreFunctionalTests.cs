namespace Agency.KeyValueStore.Sql.Postgre.Test;

using Agency.KeyValueStore.Common;
using Agency.Sql.Postgre;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// Functional tests that run against the real PostgreSQL instance defined in docker-compose.yml.
/// Requires the container to be running: docker compose up -d
/// Connection: Host=llm-host.example;Port=5432;Username=dev_user;Password=dev_password;Database=dev_db
/// Run with: dotnet test --filter "Category=Functional"
/// Skip with: dotnet test --filter "Category!=Functional"
/// </summary>
[Trait("Category", "Functional")]
public sealed class PostgreKVStoreFunctionalTests : IClassFixture<PostgreKVStoreFunctionalTests.KVStoreFixture>
{
    private readonly KVStoreFixture _fixture;

    /// <summary>
    /// Creates the test class with its shared database fixture.
    /// </summary>
    public PostgreKVStoreFunctionalTests(KVStoreFixture fixture)
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
        await kvStore.UpsertAsync(testKey, testValue, cancellationToken: TestContext.Current.CancellationToken);

        var query = new Query(testKey, null, null, 1);
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

        await kvStore.UpsertAsync(key, value, cancellationToken: TestContext.Current.CancellationToken);

        var query = new Query(key, null, null, 100);
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

        await kvStore.UpsertAsync(key, value, metadata, TestContext.Current.CancellationToken);

        var query = new Query(key, null, null, 100);
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

        await kvStore.UpsertAsync(key, value1, cancellationToken: TestContext.Current.CancellationToken);
        await kvStore.UpsertAsync(key, value2, cancellationToken: TestContext.Current.CancellationToken);

        var query = new Query(key, null, null, 100);
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

        var query = new Query("xyzabc123nonexistent", null, null, 10);
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
            await kvStore.UpsertAsync(key, new { index = i }, cancellationToken: TestContext.Current.CancellationToken);
        }

        var query = new Query(null, null, null, 2);
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

        await kvStore.UpsertAsync(key1, new { name = "Important item" }, metadata1, TestContext.Current.CancellationToken);
        await kvStore.UpsertAsync(key2, new { name = "Archived item" }, metadata2, TestContext.Current.CancellationToken);

        var filterDict = new Dictionary<string, object> { ["category"] = "important" };
        var query = new Query(null, null, filterDict, 100);
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

        await kvStore.UpsertAsync(keyWithMedical, new { title = "Medical report" }, tagsWithMedical, TestContext.Current.CancellationToken);
        await kvStore.UpsertAsync(keyWithoutMedical, new { title = "General report" }, tagsWithoutMedical, TestContext.Current.CancellationToken);

        var filter = new Dictionary<string, object> { ["tags"] = new[] { "medical" } };
        var query = new Query(null, null, filter, 100, true);
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

        await kvStore.UpsertAsync(keyMatch, new { note = "important release notes" }, cancellationToken: TestContext.Current.CancellationToken);
        await kvStore.UpsertAsync(keyNoMatch, new { note = "unrelated" }, cancellationToken: TestContext.Current.CancellationToken);

        var query = new Query(null, "release", null, 100);
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

        await kvStore.UpsertAsync(keyOld, new { seq = 1 }, cancellationToken: TestContext.Current.CancellationToken);

        // Small delay to ensure updated_on differs
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await kvStore.UpsertAsync(keyNew, new { seq = 2 }, cancellationToken: TestContext.Current.CancellationToken);

        var query = new Query(null, null, null, 100);
        var results = await kvStore.SearchAsync<dynamic>(query, TestContext.Current.CancellationToken);

        var oldIndex = results.ToList().FindIndex(r => r.Key == keyOld);
        var newIndex = results.ToList().FindIndex(r => r.Key == keyNew);

        Assert.True(oldIndex >= 0, "Old key not found in results");
        Assert.True(newIndex >= 0, "New key not found in results");
        Assert.True(newIndex < oldIndex, $"Expected newer entry (index {newIndex}) to appear before older entry (index {oldIndex})");
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
                .AddUserSecrets<PostgreKVStoreFunctionalTests>()
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
        public PostgreKVStore KVStore { get; private set; } = default!;

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
            var logger = new Mock<ILogger<PostgreKVStore>>();
            this.KVStore = new PostgreKVStore(this._sqlRunner, logger.Object);

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
