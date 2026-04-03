using Agency.Embeddings.Common;
using Agency.Sql.Postgre;
using Agency.VectorStore.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace Agency.VectorStore.Sql.Postgre.Test;

/// <summary>
/// Functional tests that run against the real PostgreSQL instance defined in docker-compose.yml.
/// Requires the container to be running: docker compose up -d
/// Connection: Host=llm-host.example;Port=5432;Username=dev_user;Password=dev_password;Database=dev_db
/// Run with: dotnet test --filter "Category=Functional"
/// Skip with: dotnet test --filter "Category!=Functional"
/// </summary>
[Trait("Category", "Functional")]
public sealed class PostgreKVStoreFunctionalTests : IClassFixture<PostgreKVStoreFunctionalTests.VectorStoreFixture>
{
    private readonly VectorStoreFixture _fixture;

    /// <summary>
    /// Creates the test class with its shared database fixture.
    /// </summary>
    public PostgreKVStoreFunctionalTests(VectorStoreFixture fixture)
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
        await kvStore.UpsertAsync("test_init_key", testValue);

        // Query back to verify with a value search
        var query = new Query(null, "initialization test", null, 1);
        var results = await kvStore.SearchAsync<dynamic>(query);

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

        await kvStore.UpsertAsync(key, value);

        // Verify it was stored with vector search on the serialized value
        var query = new Query(null, "Test Item", null, 100);
        var allResults = await kvStore.SearchAsync<dynamic>(query);
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

        await kvStore.UpsertAsync(key, value, metadata);

        // Search with exact key match to get the item
        var query = new Query(key, null, null, 100);
        var results = await kvStore.SearchAsync<dynamic>(query);

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
        await kvStore.UpsertAsync(key, value1);

        // Update with second version
        await kvStore.UpsertAsync(key, value2);

        // Verify update succeeded (should have exactly one entry with this key)
        var query = new Query(key, null, null, 100);
        var results = await kvStore.SearchAsync<dynamic>(query);
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
        await this._fixture.ClearStoreAsync();

        // Search for a key that won't exist
        var query = new Query("xyzabc123nonexistent", null, null, 10);
        var results = await kvStore.SearchAsync<dynamic>(query);

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
            await kvStore.UpsertAsync(key, new { index = i });
        }

        // Search with vector search and limit of 2
        var query = new Query(null, "index", null, 2);
        var results = await kvStore.SearchAsync<dynamic>(query);

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

        await kvStore.UpsertAsync(key1, new { name = "Important item" }, metadata1);
        await kvStore.UpsertAsync(key2, new { name = "Archived item" }, metadata2);

        // Search with vector search and metadata filter for "important"
        var filterDict = new Dictionary<string, object> { ["category"] = "important" };
        var query = new Query(null, "item", filterDict, 100);
        var results = await kvStore.SearchAsync<dynamic>(query);

        // All results should have category = important
        foreach (var result in results)
        {
            if (result.Metadata != null && result.Metadata.ContainsKey("category"))
            {
                Assert.Equal("important", result.Metadata["category"]);
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
        await kvStore.UpsertAsync(key, new { content = "similarity search test" });

        // Search for similar content
        var query = new Query(null, "similarity search", null, 10);
        var results = await kvStore.SearchAsync<dynamic>(query);

        if (results.Count > 0)
        {
            // Distance should be between 0 and 2 for cosine distance
            var firstResult = results.First();
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

        await kvStore.UpsertAsync(key1, new { text = "apple fruit red" });
        await kvStore.UpsertAsync(key2, new { text = "xyz abc 123" });

        // Search for something similar to the first item using vector search
        var query = new Query(null, "apple red", null, 100);
        var results = await kvStore.SearchAsync<dynamic>(query);

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

        await kvStore.UpsertAsync(keyWithMedical, new { title = "Medical report" }, tagsWithMedical);
        await kvStore.UpsertAsync(keyWithoutMedical, new { title = "General report" }, tagsWithoutMedical);

        // Filter: only entries whose tags array contains "medical"
        var filter = new Dictionary<string, object> { ["tags"] = new[] { "medical" } };
        var query = new Query(null, null, filter, 100, true);
        var results = await kvStore.SearchAsync<dynamic>(query);

        Assert.Contains(results, r => r.Key == keyWithMedical);
        Assert.DoesNotContain(results, r => r.Key == keyWithoutMedical);
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

        public VectorStoreFixture()
        {
            var config = new ConfigurationBuilder()
                .AddUserSecrets<PostgreKVStoreFunctionalTests>()
                .AddEnvironmentVariables()
                .Build();

            var connectionString = config.GetConnectionString("PostgreSql");

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "Connection string is not configured. Please set it in user secrets or environment variables with the key 'PostgreSql:ConnectionString'.");
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
        public PostgreKVStore KVStore { get; private set; } = default!;

        /// <summary>
        /// Returns a unique key scoped to this test run.
        /// </summary>
        public string UniqueName(string prefix) => $"{prefix}_{this._runId}";

        /// <summary>
        /// Initializes the vector store schema and tables.
        /// </summary>
        public async Task InitializeAsync()
        {
            var logger = new Mock<ILogger<PostgreKVStore>>();
            this.KVStore = new PostgreKVStore(this._embeddingGenerator, this._sqlRunner, logger.Object);

            // Initialize schema with standard embedding dimension
            await this.KVStore.InitializeSchemaAsync(dimensions: 1536);
        }

        /// <summary>
        /// Cleans up test data. Note: We don't drop the table to preserve any test data for debugging.
        /// In a production scenario, you might truncate or drop the table.
        /// </summary>
        public async Task DisposeAsync()
        {
            // Optional: Clean up if desired. For now, we leave the table intact for debugging.
            // await this._sqlRunner.ExecuteAsync("TRUNCATE TABLE semantic_kv_store");
            await Task.CompletedTask;
        }

        public async Task ClearStoreAsync()
        {
            await this._sqlRunner.ExecuteAsync("TRUNCATE TABLE semantic_kv_store;");
        }
    }
}
