using Agency.Embeddings.Common;
using Agency.Sql.Postgres;
using Agency.VectorStore.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace Agency.VectorStore.Sql.Postgres.Test;

/// <summary>
/// Functional tests that run against the real PostgreSQL instance defined in docker-compose.yml.
/// Requires the container to be running: docker compose up -d
/// Connection configured via <c>ConnectionStrings:PostgreSql</c> in appsettings.json.
/// Run with: dotnet test --filter "Category=Functional"
/// Skip with: dotnet test --filter "Category!=Functional"
/// </summary>
[Trait("Category", "Functional")]
public sealed class PostgresKVStoreFunctionalTests : IClassFixture<PostgresKVStoreFunctionalTests.VectorStoreFixture>
{
    private readonly VectorStoreFixture _fixture;

    /// <summary>
    /// Creates the test class with its shared database fixture.
    /// </summary>
    public PostgresKVStoreFunctionalTests(VectorStoreFixture fixture)
    {
        this._fixture = fixture;
    }

    // ── Functional: Schema initialization ───────────────────────────────────

    /// <summary>
    /// Verifies that InitializeSchemaAsync creates the required table and indexes.
    /// </summary>
    [Fact]
    public async Task InitializeSchemaAsync_CreatesTableAndIndexes_Succeeds()
    {
        // Table is already created by fixture, verify it exists by inserting data
        var kvStore = this._fixture.KVStore;

        // Insert test data to verify schema is correct
        var testValue = new { message = "initialization test" };
        await kvStore.UpsertAsync("test-user", "test-session", "test_init_key", testValue, cancellationToken: TestContext.Current.CancellationToken);

        // Query back to verify with a value search
        var query = new Query("test-user", "test-session", null, "initialization test", null, 1);
        var results = await kvStore.SearchAsync<dynamic>(query, TestContext.Current.CancellationToken);

        Assert.NotEmpty(results);
    }

    /// <summary>
    /// Verifies that calling InitializeSchemaAsync a second time does not destroy rows already
    /// stored by a prior UpsertAsync call. Captures the non-destructive schema-init contract from
    /// docs/ProjectLifecycle-Specifications.md §6.0 (PR-0.1).
    /// </summary>
    [Fact]
    public async Task InitializeSchemaAsync_CalledTwice_PreservesExistingRows()
    {
        var kvStore = this._fixture.KVStore;
        var key = this._fixture.UniqueName("reinit_preserve");

        await kvStore.UpsertAsync("test-user", "test-session", key, new { text = "should survive reinit" },
            cancellationToken: TestContext.Current.CancellationToken);

        await kvStore.InitializeSchemaAsync(dimensions: 1536, TestContext.Current.CancellationToken);

        var results = await kvStore.SearchAsync<dynamic>(
            new Query("test-user", "test-session", key, null, null, 1),
            TestContext.Current.CancellationToken);

        Assert.Contains(results, r => r.Key == key);
    }

    /// <summary>
    /// Verifies that calling InitializeSchemaAsync a second time leaves a usable
    /// <c>semantic_kv_projects</c> registry table behind (created once, not recreated
    /// destructively). Captures the registry-table contract from
    /// docs/ProjectLifecycle-Specifications.md §6.0, §7.2 (PR-0.2).
    /// </summary>
    [Fact]
    public async Task InitializeSchemaAsync_CalledTwice_CreatesProjectsTableOnce()
    {
        var kvStore = this._fixture.KVStore;

        await kvStore.InitializeSchemaAsync(dimensions: 1536, TestContext.Current.CancellationToken);
        await kvStore.InitializeSchemaAsync(dimensions: 1536, TestContext.Current.CancellationToken);

        int rowsAffected = await this._fixture.Runner.ExecuteAsync(
            "INSERT INTO semantic_kv_projects (user_id, project_id) VALUES (@u, @p)",
            new Dictionary<string, object?> { ["u"] = "test-user", ["p"] = this._fixture.UniqueName("registry_proj") },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, rowsAffected);
    }

    /// <summary>
    /// Verifies that re-initialising an existing <c>semantic_kv_store</c> table with a different
    /// <c>dimensions</c> value logs a warning rather than silently proceeding, per the
    /// <c>vector(dimensions)</c> typmod mismatch hazard in docs/ProjectLifecycle-Specifications.md
    /// §6.0 (PR-0.3). Uses its own <see cref="PostgresKVStore"/> instance (sharing the fixture's
    /// connection and embedding generator) so the warning can be captured via a dedicated
    /// <see cref="ILogger{TCategoryName}"/> test double, and restores the table to the dimension
    /// every other test in this fixture expects before returning.
    /// </summary>
    [Fact]
    public async Task InitializeSchemaAsync_DimensionMismatch_LogsWarning()
    {
        var logger = new CapturingLogger<PostgresKVStore>();
        var store = new PostgresKVStore(this._fixture.EmbeddingGenerator, this._fixture.Runner, logger);

        try
        {
            await store.InitializeSchemaAsync(dimensions: 1536, TestContext.Current.CancellationToken);
            await store.InitializeSchemaAsync(dimensions: 768, TestContext.Current.CancellationToken);

            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
        }
        finally
        {
            // Restore the dimension every other test in this shared fixture expects.
            await this._fixture.KVStore.InitializeSchemaAsync(dimensions: 1536, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// A minimal <see cref="ILogger{T}"/> test double that captures every log call's level and
    /// formatted message, so tests can assert on log output without pulling in a new dependency.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => this.Entries.Add((logLevel, formatter(state, exception)));
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

        // Verify it was stored with vector search on the serialized value
        var query = new Query("test-user", "test-session", null, "Test Item", null, 100);
        var allResults = await kvStore.SearchAsync<dynamic>(query, TestContext.Current.CancellationToken);
        Assert.NotEmpty(allResults);
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

        await kvStore.UpsertAsync("test-user", "test-session", key, value, metadata, cancellationToken: TestContext.Current.CancellationToken);

        // Search with exact key match to get the item
        var query = new Query("test-user", "test-session", key, null, null, 100);
        var results = await kvStore.SearchAsync<dynamic>(query, TestContext.Current.CancellationToken);

        var item = results.FirstOrDefault(r => r.Key == key);
        Assert.NotNull(item);
        Assert.NotNull(item.Metadata);
        Assert.Contains("source", item.Metadata.Keys);
        Assert.Equal("test", item.Metadata["source"]);
    }

    /// <summary>
    /// Verifies that UpsertAsync updates existing entries.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_UpdateExistingEntry_Succeeds()
    {
        var kvStore = this._fixture.KVStore;
        var key = this._fixture.UniqueName("update_test");
        var value1 = new { version = 1, message = "First version" };
        var value2 = new { version = 2, message = "Updated version" };

        // Insert first version
        await kvStore.UpsertAsync("test-user", "test-session", key, value1, cancellationToken: TestContext.Current.CancellationToken);

        // Update with second version
        await kvStore.UpsertAsync("test-user", "test-session", key, value2, cancellationToken: TestContext.Current.CancellationToken);

        // Verify update succeeded (should have exactly one entry with this key)
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

        // Ensure isolation: this assertion is only valid when no rows are present.
        await this._fixture.ClearStoreAsync(TestContext.Current.CancellationToken);

        // Search for a key that won't exist
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

        // Insert multiple items
        for (int i = 0; i < 5; i++)
        {
            var key = this._fixture.UniqueName($"limit_test_{i}");
            await kvStore.UpsertAsync("test-user", "test-session", key, new { index = i }, cancellationToken: TestContext.Current.CancellationToken);
        }

        // Search with vector search and limit of 2
        var query = new Query("test-user", "test-session", null, "index", null, 2);
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

        // Insert items with different metadata
        var key1 = this._fixture.UniqueName("filter_test_1");
        var key2 = this._fixture.UniqueName("filter_test_2");

        var metadata1 = new Dictionary<string, object> { ["category"] = "important" };
        var metadata2 = new Dictionary<string, object> { ["category"] = "archived" };

        await kvStore.UpsertAsync("test-user", "test-session", key1, new { name = "Important item" }, metadata1, cancellationToken: TestContext.Current.CancellationToken);
        await kvStore.UpsertAsync("test-user", "test-session", key2, new { name = "Archived item" }, metadata2, cancellationToken: TestContext.Current.CancellationToken);

        // Search with vector search and metadata filter for "important"
        var filterDict = new Dictionary<string, object> { ["category"] = "important" };
        var query = new Query("test-user", "test-session", null, "item", filterDict, 100);
        var results = await kvStore.SearchAsync<dynamic>(query, TestContext.Current.CancellationToken);

        // All results should have category = important
        foreach (var result in results)
        {
            if (result.Metadata != null && result.Metadata.TryGetValue("category", out var category))
            {
                Assert.Equal("important", category);
            }
        }
    }

    /// <summary>
    /// Verifies that SearchAsync returns results with distance metric.
    /// </summary>
    [Fact]
    public async Task SearchAsync_ReturnsDistance_Succeeds()
    {
        var kvStore = this._fixture.KVStore;
        var key = this._fixture.UniqueName("distance_test");

        // Insert an item
        await kvStore.UpsertAsync("test-user", "test-session", key, new { content = "similarity search test" }, cancellationToken: TestContext.Current.CancellationToken);

        // Search for similar content
        var query = new Query("test-user", "test-session", null, "similarity search", null, 10);
        var results = await kvStore.SearchAsync<dynamic>(query, TestContext.Current.CancellationToken);

        if (results.Count > 0)
        {
            // Distance should be between 0 and 2 for cosine distance
            var firstResult = results[0];
            Assert.True(firstResult.Distance >= 0, "Distance should be >= 0");
            Assert.True(firstResult.Distance <= 2, "Distance should be <= 2");

            // SimilarityPercentage should be 0-100
            Assert.True(firstResult.SimilarityPercentage >= 0, "Similarity should be >= 0");
            Assert.True(firstResult.SimilarityPercentage <= 100, "Similarity should be <= 100");
        }
    }

    /// <summary>
    /// Verifies that SearchAsync returns results in order of increasing distance (similarity).
    /// </summary>
    [Fact]
    public async Task SearchAsync_ResultsOrderedByDistance_Succeeds()
    {
        var kvStore = this._fixture.KVStore;

        // Insert multiple items with varying similarity to our query
        var key1 = this._fixture.UniqueName("order_test_1");
        var key2 = this._fixture.UniqueName("order_test_2");

        await kvStore.UpsertAsync("test-user", "test-session", key1, new { text = "apple fruit red" }, cancellationToken: TestContext.Current.CancellationToken);
        await kvStore.UpsertAsync("test-user", "test-session", key2, new { text = "xyz abc 123" }, cancellationToken: TestContext.Current.CancellationToken);

        // Search for something similar to the first item using vector search
        var query = new Query("test-user", "test-session", null, "apple red", null, 100);
        var results = await kvStore.SearchAsync<dynamic>(query, TestContext.Current.CancellationToken);

        // Verify results are sorted by distance (ascending)
        if (results.Count > 1)
        {
            for (int i = 1; i < results.Count; i++)
            {
                Assert.True(
                    results[i - 1].Distance <= results[i].Distance,
                    $"Results should be ordered by distance. Got {results[i - 1].Distance} then {results[i].Distance}");
            }
        }
    }

    /// <summary>
    /// Verifies that a document tagged with multiple tags (e.g. "document", "pdf", "medical") can be
    /// found by searching for a single tag ("medical"), while entries that do not carry that tag are excluded.
    /// Tags are stored as a JSON array under the "tags" metadata key. PostgreSQL's JSONB containment
    /// operator (@>) treats array matching as subset checking, so {"tags":["medical"]} matches any entry
    /// whose tags array contains "medical".
    /// </summary>
    [Fact]
    public async Task SearchAsync_WithTagsMetadata_FindsByTag()
    {
        var kvStore = this._fixture.KVStore;

        var keyWithMedical = this._fixture.UniqueName("tags_medical");
        var keyWithoutMedical = this._fixture.UniqueName("tags_no_medical");

        var tagsWithMedical = new Dictionary<string, object> { ["tags"] = new[] { "document", "pdf", "medical" } };
        var tagsWithoutMedical = new Dictionary<string, object> { ["tags"] = new[] { "document", "pdf" } };

        await kvStore.UpsertAsync("test-user", "test-session", keyWithMedical, new { title = "Medical report" }, tagsWithMedical, cancellationToken: TestContext.Current.CancellationToken);
        await kvStore.UpsertAsync("test-user", "test-session", keyWithoutMedical, new { title = "General report" }, tagsWithoutMedical, cancellationToken: TestContext.Current.CancellationToken);

        // Filter: only entries whose tags array contains "medical"
        var filter = new Dictionary<string, object> { ["tags"] = new[] { "medical" } };
        var query = new Query("test-user", "test-session", null, null, filter, 100, true);
        var results = await kvStore.SearchAsync<dynamic>(query, TestContext.Current.CancellationToken);

        Assert.Contains(results, r => r.Key == keyWithMedical);
        Assert.DoesNotContain(results, r => r.Key == keyWithoutMedical);
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
        var key = this._fixture.UniqueName("global_session");
        await kvStore.UpsertAsync("test-user", null, key, new { text = "global entry" }, cancellationToken: TestContext.Current.CancellationToken);

        var results = await kvStore.SearchAsync<dynamic>(
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
        var kvStore = this._fixture.KVStore;
        var keyGlobal = this._fixture.UniqueName("scope_global");
        var keySession = this._fixture.UniqueName("scope_session");

        await kvStore.UpsertAsync("test-user", null, keyGlobal, new { text = "global" }, cancellationToken: TestContext.Current.CancellationToken);
        await kvStore.UpsertAsync("test-user", "test-session", keySession, new { text = "session" }, cancellationToken: TestContext.Current.CancellationToken);

        // Null sessionId → both entries visible
        var allResults = await kvStore.SearchAsync<dynamic>(
            new Query("test-user", null, null, null, null, 100),
            TestContext.Current.CancellationToken);

        Assert.Contains(allResults, r => r.Key == keyGlobal);
        Assert.Contains(allResults, r => r.Key == keySession);

        // Specific sessionId activates the three-scope union: global + that session are both visible.
        var sessionResults = await kvStore.SearchAsync<dynamic>(
            new Query("test-user", "test-session", null, null, null, 100),
            TestContext.Current.CancellationToken);

        Assert.Contains(sessionResults, r => r.Key == keySession);
        Assert.Contains(sessionResults, r => r.Key == keyGlobal);
    }

    /// <summary>
    /// Verifies that <see cref="IVectorStore.DeleteAsync"/> with a null <c>sessionId</c> removes
    /// only the global (no-session) entry and leaves session-scoped entries intact.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_NullSessionId_RemovesOnlyGlobalEntry()
    {
        var kvStore = this._fixture.KVStore;
        var key = this._fixture.UniqueName("global_delete");

        await kvStore.UpsertAsync("test-user", null, key, new { text = "global" }, cancellationToken: TestContext.Current.CancellationToken);
        await kvStore.UpsertAsync("test-user", "test-session", key, new { text = "session" }, cancellationToken: TestContext.Current.CancellationToken);

        bool deleted = await kvStore.DeleteAsync("test-user", null, key, cancellationToken: TestContext.Current.CancellationToken);
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
        var key = this._fixture.UniqueName("session_independence");

        await kvStore.UpsertAsync("test-user", null, key, new { text = "global version" }, cancellationToken: TestContext.Current.CancellationToken);
        await kvStore.UpsertAsync("test-user", "session-a", key, new { text = "session-a version" }, cancellationToken: TestContext.Current.CancellationToken);

        // Searching with null sessionId returns both entries for this key
        var allResults = await kvStore.SearchAsync<dynamic>(
            new Query("test-user", null, key, null, null, 100),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, allResults.Count(r => r.Key == key));

        // Deleting the global entry leaves the session-a entry intact
        await kvStore.DeleteAsync("test-user", null, key, cancellationToken: TestContext.Current.CancellationToken);

        var afterDelete = await kvStore.SearchAsync<dynamic>(
            new Query("test-user", null, key, null, null, 100),
            TestContext.Current.CancellationToken);

        var remaining = afterDelete.Where(r => r.Key == key).ToList();
        Assert.Single(remaining);
        Assert.Equal("session-a", remaining[0].SessionId);
    }

    // ── CreateProjectAsync ───────────────────────────────────────────────────

    /// <summary>
    /// Verifies that declaring a brand-new project returns <see langword="true"/> and that the project
    /// subsequently appears in <see cref="IVectorStore.ListProjectsAsync"/>. Captures
    /// docs/ProjectLifecycle-Specifications.md §6.2 (6.2.1, 6.2.6).
    /// </summary>
    [Fact]
    public async Task CreateProjectAsync_NewName_ReturnsTrue_AndAppearsInListProjects()
    {
        string userId = Guid.NewGuid().ToString("N");
        string projectId = this._fixture.UniqueName("new-project");

        bool created = await this._fixture.KVStore.CreateProjectAsync(userId, projectId, TestContext.Current.CancellationToken);
        Assert.True(created);

        IReadOnlyList<string> projects = await this._fixture.KVStore.ListProjectsAsync(userId, TestContext.Current.CancellationToken);
        Assert.Contains(projectId, projects);
    }

    /// <summary>
    /// Verifies that declaring the same project a second time returns <see langword="false"/>, pinning
    /// the idempotence of <see cref="IVectorStore.CreateProjectAsync"/>. Captures
    /// docs/ProjectLifecycle-Specifications.md §6.2 (6.2.2, 6.2.6).
    /// </summary>
    [Fact]
    public async Task CreateProjectAsync_CalledTwice_SecondReturnsFalse()
    {
        string userId = Guid.NewGuid().ToString("N");
        string projectId = this._fixture.UniqueName("twice-project");

        bool first = await this._fixture.KVStore.CreateProjectAsync(userId, projectId, TestContext.Current.CancellationToken);
        bool second = await this._fixture.KVStore.CreateProjectAsync(userId, projectId, TestContext.Current.CancellationToken);

        Assert.True(first);
        Assert.False(second);
    }

    /// <summary>
    /// Verifies that a project which already exists only because a chunk was upserted under its id
    /// (a "derived" project, never declared via <see cref="IVectorStore.CreateProjectAsync"/>) is treated
    /// as already known: creating it returns <see langword="false"/> rather than a false "created".
    /// Captures docs/ProjectLifecycle-Specifications.md §6.2 (6.2.3, 6.2.6) — the derived-existence probe.
    /// </summary>
    [Fact]
    public async Task CreateProjectAsync_ProjectDerivedFromExistingChunks_ReturnsFalse()
    {
        string userId = Guid.NewGuid().ToString("N");
        string projectId = this._fixture.UniqueName("derived-project");

        await this._fixture.KVStore.UpsertAsync(userId, null, this._fixture.UniqueName("derived-chunk"), new { text = "derived chunk" },
            projectId: projectId,
            cancellationToken: TestContext.Current.CancellationToken);

        bool created = await this._fixture.KVStore.CreateProjectAsync(userId, projectId, TestContext.Current.CancellationToken);

        Assert.False(created);
    }

    /// <summary>
    /// Verifies that declaring an empty project and including its id in <see cref="Query.ProjectIds"/>
    /// does not change the set of search hits compared to the same search without that project id — a
    /// registry row is not a chunk and must never surface as, or affect, a search result. A non-empty
    /// global and session baseline is seeded first so that "both empty" cannot pass vacuously. This is
    /// the regression guard for the rejected sentinel-row design
    /// (docs/ProjectLifecycle-Specifications.md §7.3, §14.2) — do not weaken or remove it.
    /// </summary>
    [Fact]
    public async Task CreateProjectAsync_DoesNotAffectSearchResults()
    {
        string userId = Guid.NewGuid().ToString("N");
        string sessionId = this._fixture.UniqueName("baseline-session");
        string projectId = this._fixture.UniqueName("empty-project");
        string globalKey = this._fixture.UniqueName("baseline_global");
        string sessionKey = this._fixture.UniqueName("baseline_session");

        await this._fixture.KVStore.UpsertAsync(userId, null, globalKey, new { text = "global baseline" },
            cancellationToken: TestContext.Current.CancellationToken);
        await this._fixture.KVStore.UpsertAsync(userId, sessionId, sessionKey, new { text = "session baseline" },
            cancellationToken: TestContext.Current.CancellationToken);

        bool created = await this._fixture.KVStore.CreateProjectAsync(userId, projectId, TestContext.Current.CancellationToken);
        Assert.True(created);

        IReadOnlyList<SearchHit<dynamic>> withoutProject = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query(userId, sessionId, null, null, null, 100, false, null),
            TestContext.Current.CancellationToken);

        IReadOnlyList<SearchHit<dynamic>> withProject = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query(userId, sessionId, null, null, null, 100, false, [projectId]),
            TestContext.Current.CancellationToken);

        // Guard against a vacuous "both empty" comparison: the baseline must actually be present.
        Assert.Contains(withoutProject, r => r.Key == globalKey);
        Assert.Contains(withoutProject, r => r.Key == sessionKey);

        List<string> withoutKeys = withoutProject.Select(r => r.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        List<string> withKeys = withProject.Select(r => r.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.Equal(withoutKeys, withKeys);
    }

    /// <summary>
    /// Verifies that creating a project for one user does not make it appear for a different user with
    /// the same project name, pinning the <c>user_id</c> partition (P3). Captures
    /// docs/ProjectLifecycle-Specifications.md §6.2 (6.2.5, 6.2.6).
    /// </summary>
    [Fact]
    public async Task CreateProjectAsync_OtherUsersProjectSameName_IsIndependent()
    {
        string userA = Guid.NewGuid().ToString("N");
        string userB = Guid.NewGuid().ToString("N");
        string projectId = this._fixture.UniqueName("shared-name");

        bool created = await this._fixture.KVStore.CreateProjectAsync(userA, projectId, TestContext.Current.CancellationToken);
        Assert.True(created);

        IReadOnlyList<string> projectsForB = await this._fixture.KVStore.ListProjectsAsync(userB, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(projectId, projectsForB);
    }

    /// <summary>
    /// Verifies that an invalid project id (the reserved global sentinel <c>"*"</c>, or an empty string)
    /// is rejected with <see cref="ArgumentException"/> before any I/O — validation precedes the database
    /// call, so this is a pure unit-level check. Captures docs/ProjectLifecycle-Specifications.md §6.2
    /// (6.2.7, 6.2.6).
    /// </summary>
    [Theory]
    [InlineData("*")]
    [InlineData("")]
    public async Task CreateProjectAsync_InvalidProjectId_ThrowsArgumentException(string invalidProjectId)
    {
        string userId = Guid.NewGuid().ToString("N");

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await this._fixture.KVStore.CreateProjectAsync(userId, invalidProjectId, TestContext.Current.CancellationToken));
    }

    // ── ListProjectsAsync ────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that a project declared only via a registry row — no chunk ever upserted under its id —
    /// appears in <see cref="IVectorStore.ListProjectsAsync"/>. Captures the "declared" half of the
    /// declared ∪ derived union (docs/ProjectLifecycle-Specifications.md §6.5, 6.5.1).
    /// </summary>
    [Fact]
    public async Task ListProjectsAsync_IncludesDeclaredProjectWithNoEntries()
    {
        string userId = Guid.NewGuid().ToString("N");
        string projectId = this._fixture.UniqueName("declared-only");

        await this._fixture.Runner.ExecuteAsync(
            "INSERT INTO semantic_kv_projects (user_id, project_id) VALUES (@u, @p)",
            new Dictionary<string, object?> { ["u"] = userId, ["p"] = projectId },
            TestContext.Current.CancellationToken);

        IReadOnlyList<string> projects = await this._fixture.KVStore.ListProjectsAsync(userId, TestContext.Current.CancellationToken);

        Assert.Contains(projectId, projects);
    }

    /// <summary>
    /// Verifies that a project which has a chunk upserted under its id, but no registry row (as in a
    /// pre-existing store from before <see cref="IVectorStore.CreateProjectAsync"/> existed), still
    /// appears in <see cref="IVectorStore.ListProjectsAsync"/>. Captures the "derived" half of the union
    /// and the backward-compatibility guarantee (docs/ProjectLifecycle-Specifications.md §6.5, 6.5.2, L1).
    /// </summary>
    [Fact]
    public async Task ListProjectsAsync_IncludesDerivedProjectWithNoRegistryRow()
    {
        string userId = Guid.NewGuid().ToString("N");
        string projectId = this._fixture.UniqueName("derived-only");

        await this._fixture.KVStore.UpsertAsync(userId, null, this._fixture.UniqueName("derived-chunk"), new { text = "derived" },
            projectId: projectId,
            cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<string> projects = await this._fixture.KVStore.ListProjectsAsync(userId, TestContext.Current.CancellationToken);

        Assert.Contains(projectId, projects);
    }

    /// <summary>
    /// Verifies that a project which is both declared (a registry row) and populated (a chunk under the
    /// same id) appears exactly once in <see cref="IVectorStore.ListProjectsAsync"/> — the <c>UNION</c>
    /// de-duplicates the overlap. Captures docs/ProjectLifecycle-Specifications.md §6.5, 6.5.3.
    /// </summary>
    [Fact]
    public async Task ListProjectsAsync_ProjectBothDeclaredAndPopulated_AppearsOnce()
    {
        string userId = Guid.NewGuid().ToString("N");
        string projectId = this._fixture.UniqueName("declared-and-populated");

        await this._fixture.Runner.ExecuteAsync(
            "INSERT INTO semantic_kv_projects (user_id, project_id) VALUES (@u, @p)",
            new Dictionary<string, object?> { ["u"] = userId, ["p"] = projectId },
            TestContext.Current.CancellationToken);

        await this._fixture.KVStore.UpsertAsync(userId, null, this._fixture.UniqueName("declared-and-populated-chunk"), new { text = "populated" },
            projectId: projectId,
            cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<string> projects = await this._fixture.KVStore.ListProjectsAsync(userId, TestContext.Current.CancellationToken);

        Assert.Single(projects, p => p == projectId);
    }

    /// <summary>
    /// Verifies that the global scope sentinel <c>"*"</c> never appears in
    /// <see cref="IVectorStore.ListProjectsAsync"/>, even when global-scoped data exists for the user.
    /// Captures docs/ProjectLifecycle-Specifications.md §6.5, 6.5.4.
    /// </summary>
    [Fact]
    public async Task ListProjectsAsync_ExcludesGlobalSentinel()
    {
        string userId = Guid.NewGuid().ToString("N");

        await this._fixture.KVStore.UpsertAsync(userId, null, this._fixture.UniqueName("global-only"), new { text = "global" },
            cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<string> projects = await this._fixture.KVStore.ListProjectsAsync(userId, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("*", projects);
    }

    /// <summary>
    /// Verifies that <see cref="IVectorStore.ListProjectsAsync"/> returns projects in ascending order,
    /// regardless of the order in which they were inserted. Captures
    /// docs/ProjectLifecycle-Specifications.md §6.5, 6.5.5.
    /// </summary>
    [Fact]
    public async Task ListProjectsAsync_IsOrderedAscending()
    {
        string userId = Guid.NewGuid().ToString("N");
        string[] projectIds = ["zebra", "apple", "mango"];

        foreach (string projectId in projectIds)
        {
            await this._fixture.KVStore.UpsertAsync(userId, null, this._fixture.UniqueName($"order-{projectId}"), new { },
                projectId: projectId, cancellationToken: TestContext.Current.CancellationToken);
        }

        IReadOnlyList<string> projects = await this._fixture.KVStore.ListProjectsAsync(userId, TestContext.Current.CancellationToken);

        Assert.Equal(["apple", "mango", "zebra"], projects);
    }

    // ── DeleteProjectAsync ───────────────────────────────────────────────────

    /// <summary>
    /// Verifies that deleting a project removes every chunk tagged with its id and returns the count of
    /// chunks removed, while a different project's chunks survive untouched. Captures
    /// docs/ProjectLifecycle-Specifications.md §6.3 (6.3.1).
    /// </summary>
    [Fact]
    public async Task DeleteProjectAsync_RemovesAllChunksForProject_ReturnsCount()
    {
        string userId = Guid.NewGuid().ToString("N");
        string p1 = this._fixture.UniqueName("p1");
        string p2 = this._fixture.UniqueName("p2");

        for (int i = 0; i < 3; i++)
        {
            await this._fixture.KVStore.UpsertAsync(userId, null, this._fixture.UniqueName($"p1_chunk_{i}"), new { text = $"p1 chunk {i}" },
                projectId: p1, cancellationToken: TestContext.Current.CancellationToken);
        }
        for (int i = 0; i < 2; i++)
        {
            await this._fixture.KVStore.UpsertAsync(userId, null, this._fixture.UniqueName($"p2_chunk_{i}"), new { text = $"p2 chunk {i}" },
                projectId: p2, cancellationToken: TestContext.Current.CancellationToken);
        }

        int removed = await this._fixture.KVStore.DeleteProjectAsync(userId, p1, TestContext.Current.CancellationToken);
        Assert.Equal(3, removed);

        IReadOnlyList<SearchHit<dynamic>> p2Results = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query(userId, null, null, null, null, 100, false, [p2]),
            TestContext.Current.CancellationToken);
        Assert.Equal(2, p2Results.Count);
    }

    /// <summary>
    /// Verifies that deleting a project never removes global-scoped or session-scoped entries, since
    /// those scopes have no project id to match. Captures
    /// docs/ProjectLifecycle-Specifications.md §6.3 (6.3.2), P2.
    /// </summary>
    [Fact]
    public async Task DeleteProjectAsync_LeavesGlobalAndSessionScopesUntouched()
    {
        string userId = Guid.NewGuid().ToString("N");
        string sessionId = this._fixture.UniqueName("session");
        string globalKey = this._fixture.UniqueName("global_untouched");
        string sessionKey = this._fixture.UniqueName("session_untouched");
        string unrelatedProject = this._fixture.UniqueName("unrelated-project");

        await this._fixture.KVStore.UpsertAsync(userId, null, globalKey, new { text = "global" },
            cancellationToken: TestContext.Current.CancellationToken);
        await this._fixture.KVStore.UpsertAsync(userId, sessionId, sessionKey, new { text = "session" },
            cancellationToken: TestContext.Current.CancellationToken);

        await this._fixture.KVStore.DeleteProjectAsync(userId, unrelatedProject, TestContext.Current.CancellationToken);

        IReadOnlyList<SearchHit<dynamic>> results = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query(userId, sessionId, null, null, null, 100),
            TestContext.Current.CancellationToken);

        Assert.Contains(results, r => r.Key == globalKey);
        Assert.Contains(results, r => r.Key == sessionKey);
    }

    /// <summary>
    /// Verifies that deleting a project for one user does not remove another user's chunks under a
    /// project of the same name, pinning the <c>user_id</c> partition (P3). Captures
    /// docs/ProjectLifecycle-Specifications.md §6.3 (6.3.3).
    /// </summary>
    [Fact]
    public async Task DeleteProjectAsync_OtherUserSameProjectName_Unaffected()
    {
        string userA = Guid.NewGuid().ToString("N");
        string userB = Guid.NewGuid().ToString("N");
        string projectId = this._fixture.UniqueName("shared-project-name");
        string keyA = this._fixture.UniqueName("userA_chunk");

        await this._fixture.KVStore.UpsertAsync(userA, null, keyA, new { text = "user A chunk" },
            projectId: projectId, cancellationToken: TestContext.Current.CancellationToken);

        int removedForB = await this._fixture.KVStore.DeleteProjectAsync(userB, projectId, TestContext.Current.CancellationToken);
        Assert.Equal(0, removedForB);

        IReadOnlyList<SearchHit<dynamic>> resultsForA = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query(userA, null, null, null, null, 100, false, [projectId]),
            TestContext.Current.CancellationToken);

        Assert.Contains(resultsForA, r => r.Key == keyA);
    }

    /// <summary>
    /// Verifies that deleting a project that has never been created or populated returns 0 and does not
    /// throw. Captures docs/ProjectLifecycle-Specifications.md §6.3 (6.3.4), L2.
    /// </summary>
    [Fact]
    public async Task DeleteProjectAsync_UnknownProject_ReturnsZero_DoesNotThrow()
    {
        string userId = Guid.NewGuid().ToString("N");
        string projectId = this._fixture.UniqueName("never-existed");

        int removed = await this._fixture.KVStore.DeleteProjectAsync(userId, projectId, TestContext.Current.CancellationToken);

        Assert.Equal(0, removed);
    }

    /// <summary>
    /// Verifies that deleting a project removes its <c>semantic_kv_projects</c> registry row, so it no
    /// longer appears in <see cref="IVectorStore.ListProjectsAsync"/>. Captures
    /// docs/ProjectLifecycle-Specifications.md §6.3 (6.3.5).
    /// </summary>
    [Fact]
    public async Task DeleteProjectAsync_RemovesRegistryRow_ProjectNoLongerListed()
    {
        string userId = Guid.NewGuid().ToString("N");
        string projectId = this._fixture.UniqueName("registry-remove");

        await this._fixture.KVStore.CreateProjectAsync(userId, projectId, TestContext.Current.CancellationToken);
        await this._fixture.KVStore.UpsertAsync(userId, null, this._fixture.UniqueName("registry-remove-chunk"), new { text = "chunk" },
            projectId: projectId, cancellationToken: TestContext.Current.CancellationToken);

        await this._fixture.KVStore.DeleteProjectAsync(userId, projectId, TestContext.Current.CancellationToken);

        IReadOnlyList<string> projects = await this._fixture.KVStore.ListProjectsAsync(userId, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(projectId, projects);
    }

    /// <summary>
    /// Verifies that deleting a project which was declared via <see cref="IVectorStore.CreateProjectAsync"/>
    /// but never had a chunk ingested into it returns 0 (no chunks to remove) and unlists the project.
    /// Captures docs/ProjectLifecycle-Specifications.md §6.3 (6.3.6).
    /// </summary>
    [Fact]
    public async Task DeleteProjectAsync_DeclaredButEmptyProject_ReturnsZero_AndUnlists()
    {
        string userId = Guid.NewGuid().ToString("N");
        string projectId = this._fixture.UniqueName("declared-empty");

        await this._fixture.KVStore.CreateProjectAsync(userId, projectId, TestContext.Current.CancellationToken);

        int removed = await this._fixture.KVStore.DeleteProjectAsync(userId, projectId, TestContext.Current.CancellationToken);
        Assert.Equal(0, removed);

        IReadOnlyList<string> projects = await this._fixture.KVStore.ListProjectsAsync(userId, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(projectId, projects);
    }

    /// <summary>
    /// Verifies that once a project is deleted, a subsequent <see cref="IVectorStore.SearchAsync{TValue}"/>
    /// call that still passes the deleted id in <see cref="Query.ProjectIds"/> returns zero hits from that
    /// project and does not throw — the dangling reference degrades to an empty set (L2). Captures
    /// docs/ProjectLifecycle-Specifications.md §6.3 (6.3.7).
    /// </summary>
    [Fact]
    public async Task DeleteProjectAsync_ThenSearch_ReturnsNoHitsFromThatProject()
    {
        string userId = Guid.NewGuid().ToString("N");
        string projectId = this._fixture.UniqueName("dangling-reference");
        string key = this._fixture.UniqueName("dangling-reference-chunk");

        await this._fixture.KVStore.UpsertAsync(userId, null, key, new { text = "will be deleted" },
            projectId: projectId, cancellationToken: TestContext.Current.CancellationToken);

        await this._fixture.KVStore.DeleteProjectAsync(userId, projectId, TestContext.Current.CancellationToken);

        IReadOnlyList<SearchHit<dynamic>> results = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query(userId, null, null, null, null, 100, false, [projectId]),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(results, r => r.Key == key);
    }

    /// <summary>
    /// Verifies that passing a pre-cancelled <see cref="CancellationToken"/> to
    /// <see cref="IVectorStore.DeleteProjectAsync"/> throws <see cref="OperationCanceledException"/> and
    /// removes no rows. Captures docs/ProjectLifecycle-Specifications.md §6.3 (6.3.8).
    /// </summary>
    [Fact]
    public async Task DeleteProjectAsync_HonoursCancellation()
    {
        string userId = Guid.NewGuid().ToString("N");
        string projectId = this._fixture.UniqueName("cancel-project");
        string key = this._fixture.UniqueName("cancel-project-chunk");

        await this._fixture.KVStore.UpsertAsync(userId, null, key, new { text = "should survive cancellation" },
            projectId: projectId, cancellationToken: TestContext.Current.CancellationToken);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await this._fixture.KVStore.DeleteProjectAsync(userId, projectId, cancelled.Token));

        IReadOnlyList<SearchHit<dynamic>> results = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query(userId, null, null, null, null, 100, false, [projectId]),
            TestContext.Current.CancellationToken);

        Assert.Contains(results, r => r.Key == key);
    }

    /// <summary>
    /// Verifies that the reserved global sentinel <c>"*"</c> is rejected with
    /// <see cref="ArgumentException"/> before any I/O — validation precedes the database call, so this is
    /// a pure unit-level check. Captures docs/ProjectLifecycle-Specifications.md §6.3 (6.3.10).
    /// </summary>
    [Fact]
    public async Task DeleteProjectAsync_GlobalSentinel_ThrowsArgumentException()
    {
        string userId = Guid.NewGuid().ToString("N");

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await this._fixture.KVStore.DeleteProjectAsync(userId, "*", TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies that a project deleted via <see cref="IVectorStore.DeleteProjectAsync"/> and then
    /// re-ingested into via <see cref="IVectorStore.UpsertAsync{TValue}"/> under the same project id
    /// reappears in <see cref="IVectorStore.ListProjectsAsync"/> as a <b>derived</b> project — no registry
    /// row is recreated, only a chunk exists. This pins the "accept, detect, and report" resurrection
    /// behaviour as specified — not a bug — so a future change that accidentally prevents it (e.g. a
    /// registry-row precondition on <c>UpsertAsync</c>) fails this test rather than silently changing the
    /// contract. Captures docs/ProjectLifecycle-Specifications.md §9.2.
    /// </summary>
    [Fact]
    public async Task DeleteThenUpsert_ProjectReappearsAsDerived()
    {
        string userId = Guid.NewGuid().ToString("N");
        string projectId = this._fixture.UniqueName("resurrection");

        await this._fixture.KVStore.UpsertAsync(userId, null, this._fixture.UniqueName("resurrection-seed"), new { text = "seed chunk" },
            projectId: projectId, cancellationToken: TestContext.Current.CancellationToken);

        await this._fixture.KVStore.DeleteProjectAsync(userId, projectId, TestContext.Current.CancellationToken);

        await this._fixture.KVStore.UpsertAsync(userId, null, this._fixture.UniqueName("resurrection-concurrent"), new { text = "concurrent ingest" },
            projectId: projectId, cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<string> projects = await this._fixture.KVStore.ListProjectsAsync(userId, TestContext.Current.CancellationToken);

        Assert.Contains(projectId, projects);
    }

    // ── Fixture ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Shared vector store fixture for PostgreSQL integration tests.
    /// Sets up a mock embedding generator for deterministic testing.
    /// </summary>
    public sealed class VectorStoreFixture : IAsyncLifetime
    {
        private readonly PostgreSqlRunner _sqlRunner;
        private readonly IEmbeddingGenerator _embeddingGenerator;

        /// <summary>
        /// Resolves the PostgreSQL connection string from user secrets or environment variables and
        /// configures a deterministic mock <see cref="IEmbeddingGenerator"/> for the fixture's lifetime.
        /// </summary>
        public VectorStoreFixture()
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

            // Use a mock embedding generator that produces deterministic embeddings based on input hash
            var mockGenerator = new Mock<IEmbeddingGenerator>();
            mockGenerator
                .Setup(g => g.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>((input, ct) =>
                {
                    // Create deterministic embeddings from input hash
                    var hash = input.GetHashCode();
                    var random = new Random(hash);
                    var embeddings = new float[1536]; // Standard embedding dimension
                    for (int i = 0; i < embeddings.Length; i++)
                    {
                        embeddings[i] = (float)random.NextDouble();
                    }
                    return Task.FromResult((ReadOnlyMemory<float>)embeddings.AsMemory());
                });

            this._embeddingGenerator = mockGenerator.Object;
        }

        private readonly string _runId = Guid.NewGuid().ToString("N")[..8];

        /// <summary>
        /// Gets the shared PostgreSQL vector store instance.
        /// </summary>
        public PostgresKVStore KVStore { get; private set; } = default!;

        /// <summary>
        /// Gets the PostgreSQL runner backing the shared connection.
        /// </summary>
        public PostgreSqlRunner Runner => this._sqlRunner;

        /// <summary>
        /// Gets the deterministic mock embedding generator shared by this fixture's stores.
        /// </summary>
        public IEmbeddingGenerator EmbeddingGenerator => this._embeddingGenerator;

        /// <summary>
        /// Returns a unique key scoped to this test run.
        /// </summary>
        public string UniqueName(string prefix) => $"{prefix}_{this._runId}";

        /// <summary>
        /// Initializes the vector store schema and tables.
        /// </summary>
        public async ValueTask InitializeAsync()
        {
            var logger = new Mock<ILogger<PostgresKVStore>>();
            this.KVStore = new PostgresKVStore(this._embeddingGenerator, this._sqlRunner, logger.Object);

            // Initialize schema with standard embedding dimension
            await this.KVStore.InitializeSchemaAsync(dimensions: 1536, TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Cleans up test data. Note: We don't drop the table to preserve any test data for debugging.
        /// In a production scenario, you might truncate or drop the table.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            // Optional: Clean up if desired. For now, we leave the table intact for debugging.
            // await this._sqlRunner.ExecuteAsync("TRUNCATE TABLE semantic_kv_store");
            await ValueTask.CompletedTask;
        }

        /// <summary>
        /// Truncates the <c>semantic_kv_store</c> table so tests that assert on an empty store are isolated
        /// from data left behind by other tests.
        /// </summary>
        /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
        public async Task ClearStoreAsync(CancellationToken cancellationToken = default)
        {
            await this._sqlRunner.ExecuteAsync("TRUNCATE TABLE semantic_kv_store;", cancellationToken: cancellationToken);
        }
    }
}
