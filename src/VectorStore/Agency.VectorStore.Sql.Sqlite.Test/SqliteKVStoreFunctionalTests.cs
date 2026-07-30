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
    private static readonly string[] _tagsWithMedical = ["document", "pdf", "medical"];
    private static readonly string[] _tagsWithoutMedical = ["document", "pdf"];

    private readonly VectorStoreFixture _fixture;

    /// <summary>
    /// Creates the test class with its shared in-memory SQLite fixture.
    /// </summary>
    public SqliteKVStoreFunctionalTests(VectorStoreFixture fixture)
    {
        this._fixture = fixture;
    }

    // ── Schema initialization ────────────────────────────────────────────────

    /// <summary>
    /// Verifies that InitializeSchemaAsync creates the <c>semantic_kv_store</c> table.
    /// </summary>
    [Fact]
    public async Task InitializeSchemaAsync_CreatesTable_Succeeds()
    {
        var ds = await this._fixture.Runner.QueryAsync(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'semantic_kv_store'",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(ds.Rows);
        Assert.Equal("semantic_kv_store", ds["name", 0]);
    }

    /// <summary>
    /// Verifies that calling InitializeSchemaAsync a second time does not destroy rows already
    /// stored by a prior UpsertAsync call. Captures the non-destructive schema-init contract from
    /// docs/ProjectLifecycle-Specifications.md §6.0 (PR-0.1).
    /// </summary>
    [Trait("Category", "Functional")]
    [Fact]
    public async Task InitializeSchemaAsync_CalledTwice_PreservesExistingRows()
    {
        var key = this._fixture.UniqueName("reinit_preserve");

        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", key, new { text = "should survive reinit" },
            cancellationToken: TestContext.Current.CancellationToken);

        await this._fixture.KVStore.InitializeSchemaAsync(dimensions: 1536, TestContext.Current.CancellationToken);

        var results = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query("test-user", "test-session", key, null, null, 1),
            TestContext.Current.CancellationToken);

        Assert.Contains(results, r => r.Key == key);
    }

    /// <summary>
    /// Verifies that calling InitializeSchemaAsync a second time leaves a usable
    /// <c>semantic_kv_projects</c> registry table behind (created once, not recreated destructively).
    /// Captures the registry-table contract from docs/ProjectLifecycle-Specifications.md §6.0, §7.2
    /// (PR-0.2).
    /// </summary>
    [Trait("Category", "Functional")]
    [Fact]
    public async Task InitializeSchemaAsync_CalledTwice_CreatesProjectsTableOnce()
    {
        await this._fixture.KVStore.InitializeSchemaAsync(dimensions: 1536, TestContext.Current.CancellationToken);
        await this._fixture.KVStore.InitializeSchemaAsync(dimensions: 1536, TestContext.Current.CancellationToken);

        int rowsAffected = await this._fixture.Runner.ExecuteAsync(
            "INSERT INTO semantic_kv_projects (user_id, project_id) VALUES (@u, @p)",
            new Dictionary<string, object?> { ["u"] = "test-user", ["p"] = this._fixture.UniqueName("registry_proj") },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, rowsAffected);
    }

    // ── Upsert ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that UpsertAsync stores and retrieves a simple object.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_SimpleObject_Succeeds()
    {
        var key = this._fixture.UniqueName("simple");
        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", key, new { name = "Test Item", score = 42 }, cancellationToken: TestContext.Current.CancellationToken);

        var results = await this._fixture.KVStore.SearchAsync<dynamic>(new Query("test-user", "test-session", key, null, null, 1), TestContext.Current.CancellationToken);
        Assert.NotEmpty(results);
    }

    /// <summary>
    /// Verifies that UpsertAsync with metadata stores and retrieves the metadata correctly.
    /// </summary>
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

    /// <summary>
    /// Verifies that UpsertAsync on an existing key updates the entry in place rather than
    /// creating a second record.
    /// </summary>
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

    /// <summary>
    /// Verifies that SearchAsync returns an empty list when no entry matches the query key.
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
    /// Verifies that SearchAsync respects the <see cref="Query.Limit"/> parameter.
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
    /// Verifies that SearchAsync with a metadata filter returns only entries whose metadata
    /// matches the filter.
    /// </summary>
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

    /// <summary>
    /// Verifies that an entry tagged with multiple values (e.g. "document", "pdf", "medical") is
    /// found by filtering on a single tag ("medical"), while entries without that tag are excluded.
    /// </summary>
    [Fact]
    public async Task SearchAsync_WithTagsMetadata_FindsByTag()
    {
        var keyWithMedical = this._fixture.UniqueName("tags_medical");
        var keyWithoutMedical = this._fixture.UniqueName("tags_no_medical");

        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", keyWithMedical, new { title = "Medical report" },
            new Dictionary<string, object> { ["tags"] = _tagsWithMedical }, cancellationToken: TestContext.Current.CancellationToken);
        await this._fixture.KVStore.UpsertAsync("test-user", "test-session", keyWithoutMedical, new { title = "General report" },
            new Dictionary<string, object> { ["tags"] = _tagsWithoutMedical }, cancellationToken: TestContext.Current.CancellationToken);

        // Filter: only entries whose tags array contains "medical"
        var filter = new Dictionary<string, object> { ["tags"] = new[] { "medical" } };
        var results = await this._fixture.KVStore.SearchAsync<dynamic>(new Query("test-user", "test-session", null, null, filter, 100, true), TestContext.Current.CancellationToken);

        Assert.Contains(results, r => r.Key == keyWithMedical);
        Assert.DoesNotContain(results, r => r.Key == keyWithoutMedical);
    }

    /// <summary>
    /// Verifies that SearchAsync returns results ordered by ascending cosine distance.
    /// </summary>
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

        // Null sessionId resolves to the global sentinel '*' — only global-scope entries are returned.
        var globalOnlyResults = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query("test-user", null, null, null, null, 100),
            TestContext.Current.CancellationToken);

        Assert.Contains(globalOnlyResults, r => r.Key == keyGlobal);
        Assert.DoesNotContain(globalOnlyResults, r => r.Key == keySession);

        // Specific sessionId activates the three-scope union: global + that session are both visible.
        var sessionResults = await this._fixture.KVStore.SearchAsync<dynamic>(
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

        // Searching with session-a activates the three-scope union (global + session-a),
        // so both the null-session entry and the session-a entry are visible.
        var allResults = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query("test-user", "session-a", key, null, null, 100),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, allResults.Count(r => r.Key == key));

        // Deleting the global entry leaves the session-a entry intact.
        await this._fixture.KVStore.DeleteAsync("test-user", null, key, cancellationToken: TestContext.Current.CancellationToken);

        var afterDelete = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query("test-user", "session-a", key, null, null, 100),
            TestContext.Current.CancellationToken);

        var remaining = afterDelete.Where(r => r.Key == key).ToList();
        Assert.Single(remaining);
        Assert.Equal("session-a", remaining[0].SessionId);
    }

    // ── Project scope — UpsertAsync and SearchAsync ──────────────────────────

    /// <summary>
    /// Verifies that UpsertAsync with a <c>projectId</c> stores the entry under that project scope
    /// and that it is retrievable by including the project id in the search query.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_WithProjectId_StoredUnderProjectScope()
    {
        string userId = Guid.NewGuid().ToString("N");
        string key = this._fixture.UniqueName("proj_entry");

        await this._fixture.KVStore.UpsertAsync(userId, null, key, new { text = "project entry" },
            projectId: "proj-alpha",
            cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<SearchHit<dynamic>> results = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query(userId, null, key, null, null, 1, false, ["proj-alpha"]),
            TestContext.Current.CancellationToken);

        Assert.Contains(results, r => r.Key == key);
    }

    /// <summary>
    /// Verifies that a search with a specific session id and project ids returns entries from all
    /// three scopes: user-global, session-scoped, and project-scoped.
    /// </summary>
    [Fact]
    public async Task SearchAsync_ThreeScopeUnion_ReturnsAllScopes()
    {
        string userId = Guid.NewGuid().ToString("N");
        string key1 = this._fixture.UniqueName("global");
        string key2 = this._fixture.UniqueName("session");
        string key3 = this._fixture.UniqueName("project");

        await this._fixture.KVStore.UpsertAsync(userId, null, key1, new { text = "global" },
            cancellationToken: TestContext.Current.CancellationToken);

        await this._fixture.KVStore.UpsertAsync(userId, "sess-abc", key2, new { text = "session" },
            cancellationToken: TestContext.Current.CancellationToken);

        await this._fixture.KVStore.UpsertAsync(userId, null, key3, new { text = "project" },
            projectId: "myproj",
            cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<SearchHit<dynamic>> results = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query(userId, "sess-abc", null, null, null, 100, false, ["myproj"]),
            TestContext.Current.CancellationToken);

        Assert.Contains(results, r => r.Key == key1);
        Assert.Contains(results, r => r.Key == key2);
        Assert.Contains(results, r => r.Key == key3);
    }

    /// <summary>
    /// Verifies that SearchAsync with <see cref="Query.ProjectIds"/> set returns only entries scoped
    /// to the requested projects, excluding entries scoped to other projects.
    /// </summary>
    [Fact]
    public async Task SearchAsync_WithProjectIds_ExcludesOtherProjects()
    {
        string userId = Guid.NewGuid().ToString("N");
        string keyA = this._fixture.UniqueName("proj_a");
        string keyB = this._fixture.UniqueName("proj_b");

        await this._fixture.KVStore.UpsertAsync(userId, null, keyA, new { text = "project A" },
            projectId: "proj-A",
            cancellationToken: TestContext.Current.CancellationToken);

        await this._fixture.KVStore.UpsertAsync(userId, null, keyB, new { text = "project B" },
            projectId: "proj-B",
            cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<SearchHit<dynamic>> results = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query(userId, null, null, null, null, 100, false, ["proj-A"]),
            TestContext.Current.CancellationToken);

        Assert.Contains(results, r => r.Key == keyA);
        Assert.DoesNotContain(results, r => r.Key == keyB);
    }

    /// <summary>
    /// Verifies that SearchAsync without <see cref="Query.ProjectIds"/> excludes entries that were
    /// upserted under a project scope.
    /// </summary>
    [Fact]
    public async Task SearchAsync_WithoutProjectIds_ExcludesProjectScopedEntries()
    {
        string userId = Guid.NewGuid().ToString("N");
        string key = this._fixture.UniqueName("hidden");

        await this._fixture.KVStore.UpsertAsync(userId, null, key, new { text = "hidden project entry" },
            projectId: "hidden-proj",
            cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<SearchHit<dynamic>> results = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query(userId, "check-session", null, null, null, 100, false, null),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(results, r => r.Key == key);
    }

    /// <summary>
    /// Verifies that DeleteAsync with a <c>projectId</c> removes only the project-scoped entry for
    /// that key, leaving the global-scoped entry with the same key intact.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_WithProjectId_RemovesOnlyProjectEntry()
    {
        string userId = Guid.NewGuid().ToString("N");
        string key = this._fixture.UniqueName("del_key");

        await this._fixture.KVStore.UpsertAsync(userId, null, key, new { text = "global" },
            cancellationToken: TestContext.Current.CancellationToken);

        await this._fixture.KVStore.UpsertAsync(userId, null, key, new { text = "project" },
            projectId: "del-proj",
            cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<SearchHit<dynamic>> before = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query(userId, null, key, null, null, 100, false, ["del-proj"]),
            TestContext.Current.CancellationToken);
        Assert.Equal(2, before.Count(r => r.Key == key));

        bool deleted = await this._fixture.KVStore.DeleteAsync(userId, null, key,
            projectId: "del-proj",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(deleted);

        IReadOnlyList<SearchHit<dynamic>> after = await this._fixture.KVStore.SearchAsync<dynamic>(
            new Query(userId, null, key, null, null, 100, false, null),
            TestContext.Current.CancellationToken);
        Assert.Single(after, r => r.Key == key);
    }

    // ── ListProjectsAsync ────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that ListProjectsAsync returns the distinct project ids for a user, excluding the
    /// global project sentinel.
    /// </summary>
    [Fact]
    public async Task ListProjectsAsync_ReturnsDistinctProjectNames()
    {
        string userId = Guid.NewGuid().ToString("N");

        await this._fixture.KVStore.UpsertAsync(userId, null, this._fixture.UniqueName("k1"), new { },
            projectId: "proj-x", cancellationToken: TestContext.Current.CancellationToken);
        await this._fixture.KVStore.UpsertAsync(userId, null, this._fixture.UniqueName("k2"), new { },
            projectId: "proj-y", cancellationToken: TestContext.Current.CancellationToken);
        await this._fixture.KVStore.UpsertAsync(userId, null, this._fixture.UniqueName("k3"), new { },
            projectId: "proj-x", cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<string> projects = await this._fixture.KVStore.ListProjectsAsync(userId, TestContext.Current.CancellationToken);

        Assert.Contains("proj-x", projects);
        Assert.Contains("proj-y", projects);
        Assert.DoesNotContain("*", projects);
        Assert.Equal(2, projects.Count);
    }

    /// <summary>
    /// Verifies that ListProjectsAsync returns an empty list for a user with no project-scoped entries.
    /// </summary>
    [Fact]
    public async Task ListProjectsAsync_NoProjects_ReturnsEmpty()
    {
        IReadOnlyList<string> projects = await this._fixture.KVStore.ListProjectsAsync(
            Guid.NewGuid().ToString("N"),
            TestContext.Current.CancellationToken);

        Assert.Empty(projects);
    }

    /// <summary>
    /// Verifies that ListProjectsAsync only returns projects belonging to the requested user.
    /// </summary>
    [Fact]
    public async Task ListProjectsAsync_IsolatedByUserId()
    {
        string userId1 = Guid.NewGuid().ToString("N");
        string userId2 = Guid.NewGuid().ToString("N");

        await this._fixture.KVStore.UpsertAsync(userId1, null, this._fixture.UniqueName("k"), new { },
            projectId: "proj-user1", cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<string> projects = await this._fixture.KVStore.ListProjectsAsync(userId2, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("proj-user1", projects);
    }

    /// <summary>
    /// Verifies that a project declared only via a registry row — no chunk ever upserted under its id —
    /// appears in <see cref="IVectorStore.ListProjectsAsync"/>. Captures the "declared" half of the
    /// declared ∪ derived union (docs/ProjectLifecycle-Specifications.md §6.5, 6.5.1).
    /// </summary>
    [Trait("Category", "Functional")]
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
    [Trait("Category", "Functional")]
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
    [Trait("Category", "Functional")]
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
    [Trait("Category", "Functional")]
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
    [Trait("Category", "Functional")]
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

    // ── ListDocumentsAsync ───────────────────────────────────────────────────

    /// <summary>
    /// Verifies that ListDocumentsAsync returns an entry that carries a <c>source_file</c> metadata
    /// value, with the global session and project scope reflected on the returned <see cref="DocumentInfo"/>.
    /// </summary>
    [Fact]
    public async Task ListDocumentsAsync_ReturnsEntriesWithSourceFileMetadata()
    {
        string userId = Guid.NewGuid().ToString("N");
        string key = this._fixture.UniqueName("doc");

        await this._fixture.KVStore.UpsertAsync(userId, null, key, new { text = "document content" },
            metadata: new Dictionary<string, object> { ["source_file"] = "docs/Home.md" },
            cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<DocumentInfo> docs = await this._fixture.KVStore.ListDocumentsAsync(
            userId, "test-sess", null, TestContext.Current.CancellationToken);

        DocumentInfo doc = Assert.Single(docs, d => d.SourceFile == "docs/Home.md");
        Assert.Equal("*", doc.SessionId);
        Assert.Equal("*", doc.ProjectId);
    }

    /// <summary>
    /// Verifies that ListDocumentsAsync returns an entry scoped to a project when that project id is
    /// included in the request.
    /// </summary>
    [Fact]
    public async Task ListDocumentsAsync_ReturnsProjectScopedEntries()
    {
        string userId = Guid.NewGuid().ToString("N");
        string key = this._fixture.UniqueName("proj_doc");

        await this._fixture.KVStore.UpsertAsync(userId, null, key, new { text = "project document" },
            metadata: new Dictionary<string, object> { ["source_file"] = "docs/Project.md" },
            projectId: "listproj",
            cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<DocumentInfo> docs = await this._fixture.KVStore.ListDocumentsAsync(
            userId, "test-sess", ["listproj"], TestContext.Current.CancellationToken);

        DocumentInfo doc = Assert.Single(docs, d => d.SourceFile == "docs/Project.md");
        Assert.Equal("*", doc.SessionId);
        Assert.Equal("listproj", doc.ProjectId);
    }

    /// <summary>
    /// Verifies that ListDocumentsAsync excludes entries that do not carry a <c>source_file</c>
    /// metadata value.
    /// </summary>
    [Fact]
    public async Task ListDocumentsAsync_ExcludesEntriesWithoutSourceFileMetadata()
    {
        string userId = Guid.NewGuid().ToString("N");
        string keyWithFile = this._fixture.UniqueName("with_file");
        string keyWithoutFile = this._fixture.UniqueName("without_file");

        await this._fixture.KVStore.UpsertAsync(userId, null, keyWithFile, new { text = "has source" },
            metadata: new Dictionary<string, object> { ["source_file"] = "notes/keep.md" },
            cancellationToken: TestContext.Current.CancellationToken);

        await this._fixture.KVStore.UpsertAsync(userId, null, keyWithoutFile, new { text = "no source" },
            metadata: new Dictionary<string, object> { ["other"] = "value" },
            cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<DocumentInfo> docs = await this._fixture.KVStore.ListDocumentsAsync(
            userId, null, null, TestContext.Current.CancellationToken);

        DocumentInfo doc = Assert.Single(docs);
        Assert.Equal("notes/keep.md", doc.SourceFile);
    }

    /// <summary>
    /// Verifies that ListDocumentsAsync returns an empty list for a user with no matching entries.
    /// </summary>
    [Fact]
    public async Task ListDocumentsAsync_NoEntries_ReturnsEmpty()
    {
        IReadOnlyList<DocumentInfo> docs = await this._fixture.KVStore.ListDocumentsAsync(
            Guid.NewGuid().ToString("N"), "sess", null, TestContext.Current.CancellationToken);

        Assert.Empty(docs);
    }

    // ── CreateProjectAsync ───────────────────────────────────────────────────

    /// <summary>
    /// Verifies that declaring a brand-new project returns <see langword="true"/> and that the project
    /// subsequently appears in <see cref="IVectorStore.ListProjectsAsync"/>. Captures
    /// docs/ProjectLifecycle-Specifications.md §6.2 (6.2.1).
    /// </summary>
    [Trait("Category", "Functional")]
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
    /// docs/ProjectLifecycle-Specifications.md §6.2 (6.2.2).
    /// </summary>
    [Trait("Category", "Functional")]
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
    /// Captures docs/ProjectLifecycle-Specifications.md §6.2 (6.2.3) — the derived-existence probe.
    /// </summary>
    [Trait("Category", "Functional")]
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
    [Trait("Category", "Functional")]
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
    /// docs/ProjectLifecycle-Specifications.md §6.2 (6.2.5).
    /// </summary>
    [Trait("Category", "Functional")]
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
    /// (6.2.7).
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

    // ── DeleteProjectAsync ───────────────────────────────────────────────────

    /// <summary>
    /// Verifies that deleting a project removes every chunk tagged with its id and returns the count of
    /// chunks removed, while a different project's chunks survive untouched. Captures
    /// docs/ProjectLifecycle-Specifications.md §6.3 (6.3.1).
    /// </summary>
    [Trait("Category", "Functional")]
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
    [Trait("Category", "Functional")]
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
    [Trait("Category", "Functional")]
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
    [Trait("Category", "Functional")]
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
    [Trait("Category", "Functional")]
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
    [Trait("Category", "Functional")]
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
    [Trait("Category", "Functional")]
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
    [Trait("Category", "Functional")]
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
    [Trait("Category", "Functional")]
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

        /// <summary>
        /// Creates an isolated in-memory SQLite database with vector functions registered, and
        /// configures a deterministic mock <see cref="IEmbeddingGenerator"/> for the fixture's lifetime.
        /// </summary>
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

        /// <summary>
        /// Gets the SQLite runner backing the in-memory database.
        /// </summary>
        public SqliteRunner Runner { get; }

        /// <summary>
        /// Gets the shared SQLite vector store instance under test.
        /// </summary>
        public SqliteKVStore KVStore { get; }

        /// <summary>
        /// Returns a unique key scoped to this test run.
        /// </summary>
        /// <param name="prefix">The prefix to prepend to the generated unique suffix.</param>
        public string UniqueName(string prefix) => $"{prefix}_{this._runId}";

        /// <summary>
        /// Initializes the vector store schema before the first test runs.
        /// </summary>
        public async ValueTask InitializeAsync()
        {
            await this.KVStore.InitializeSchemaAsync(dimensions: 1536, TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Closes the keep-alive connection, allowing the in-memory database to be discarded.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await this._keepAlive.CloseAsync();
        }
    }
}
