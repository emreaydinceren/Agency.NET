using Agency.KeyValueStore.Common;
using Agency.KeyValueStore.Sql.Sqlite;
using Agency.Sql.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Moq;
using System.Text.Json;

namespace Agency.Mcp.Memory.Test;

/// <summary>
/// Functional tests for <see cref="MemoryTool"/> using a real <see cref="SqliteKVStore"/> backed by an in-memory SQLite database.
/// No external infrastructure is required — the database lives entirely in process.
/// </summary>
public sealed class MemoryToolFunctionalTests : IClassFixture<MemoryToolFunctionalTests.MemoryToolFixture>
{
    private readonly MemoryToolFixture _fixture;

    /// <summary>
    /// Initializes a new instance of <see cref="MemoryToolFunctionalTests"/> with the shared fixture.
    /// </summary>
    public MemoryToolFunctionalTests(MemoryToolFixture fixture)
    {
        this._fixture = fixture;
    }

    // ── Round-trip ────────────────────────────────────────────────────────────

    /// <summary>
    /// Storing a value and recalling it by exact scope/domain/key should return that value in the JSON result.
    /// </summary>
    [Fact]
    public async Task Memorize_Then_Recall_ReturnsStoredValue()
    {
        var scope = new MemoryScope { UserId = "u1", SessionId = "s1" };
        var record = new MemoryRecord
        {
            Scope = scope,
            Domain = "roundtrip",
            Key = this._fixture.UniqueName("key"),
            Value = "expected-value"
        };

        _ = await this._fixture.Tool.Memorize(record);
        string json = await this._fixture.Tool.Recall(scope, "roundtrip", record.Key, null);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Contains(root.EnumerateArray(), e => e.GetProperty("Value").GetString() == "expected-value");
    }

    /// <summary>
    /// After memorizing and then forgetting an entry, recalling it should return an empty JSON array.
    /// </summary>
    [Fact]
    public async Task Memorize_Then_Forget_Then_Recall_ReturnsNoHits()
    {
        var scope = new MemoryScope { UserId = "u2", SessionId = "s2" };
        string key = this._fixture.UniqueName("forget-key");
        var record = new MemoryRecord
        {
            Scope = scope,
            Domain = "forget-domain",
            Key = key,
            Value = "to-be-forgotten"
        };

        _ = await this._fixture.Tool.Memorize(record);
        _ = await this._fixture.Tool.Forget(scope, "forget-domain", key);
        string json = await this._fixture.Tool.Recall(scope, "forget-domain", key, null);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetArrayLength());
    }

    /// <summary>
    /// Storing entries across two sessions for the same user and recalling by one session should return only entries for that session.
    /// </summary>
    [Fact]
    public async Task Recall_BySessionScope_ReturnsAllEntriesForSession()
    {
        var scopeA = new MemoryScope { UserId = "u3", SessionId = "sess-a" };
        var scopeB = new MemoryScope { UserId = "u3", SessionId = "sess-b" };

        string suffix = this._fixture.UniqueName("scope");

        await this._fixture.Tool.Memorize(new MemoryRecord { Scope = scopeA, Domain = "d", Key = $"k1-{suffix}", Value = "v1" });
        await this._fixture.Tool.Memorize(new MemoryRecord { Scope = scopeA, Domain = "d", Key = $"k2-{suffix}", Value = "v2" });
        await this._fixture.Tool.Memorize(new MemoryRecord { Scope = scopeB, Domain = "d", Key = $"k3-{suffix}", Value = "v3" });

        string json = await this._fixture.Tool.Recall(scopeA, null, null, null);

        using var doc = JsonDocument.Parse(json);
        var hits = doc.RootElement.EnumerateArray().ToList();

        // Should contain both entries for scopeA but not the scopeB entry
        Assert.Contains(hits, e => e.GetProperty("Key").GetString()!.Contains($"k1-{suffix}"));
        Assert.Contains(hits, e => e.GetProperty("Key").GetString()!.Contains($"k2-{suffix}"));
        Assert.DoesNotContain(hits, e => e.GetProperty("Key").GetString()!.Contains($"k3-{suffix}"));
    }

    /// <summary>
    /// When recalling with a specific tag only entries that include that tag should be returned.
    /// </summary>
    [Fact]
    public async Task Recall_ByTags_NarrowsResults()
    {
        var scope = new MemoryScope { UserId = "u4", SessionId = "s4" };
        string suffix = this._fixture.UniqueName("tags");

        await this._fixture.Tool.Memorize(new MemoryRecord
        {
            Scope = scope,
            Domain = "d",
            Key = $"tagged-{suffix}",
            Value = "with-important-tag",
            Tags = ["important", "work"]
        });

        await this._fixture.Tool.Memorize(new MemoryRecord
        {
            Scope = scope,
            Domain = "d",
            Key = $"untagged-{suffix}",
            Value = "without-important-tag",
            Tags = ["personal"]
        });

        string json = await this._fixture.Tool.Recall(scope, null, null, ["important"]);

        using var doc = JsonDocument.Parse(json);
        var hits = doc.RootElement.EnumerateArray().ToList();

        Assert.Contains(hits, e => e.GetProperty("Key").GetString()!.Contains($"tagged-{suffix}"));
        Assert.DoesNotContain(hits, e => e.GetProperty("Key").GetString()!.Contains($"untagged-{suffix}"));
    }

    /// <summary>
    /// Memorizing the same composite key twice should result in the second value being returned on recall.
    /// </summary>
    [Fact]
    public async Task Memorize_OverwritesExistingKey()
    {
        var scope = new MemoryScope { UserId = "u5", SessionId = "s5" };
        string key = this._fixture.UniqueName("overwrite");

        await this._fixture.Tool.Memorize(new MemoryRecord
        {
            Scope = scope,
            Domain = "d",
            Key = key,
            Value = "first-value"
        });

        await this._fixture.Tool.Memorize(new MemoryRecord
        {
            Scope = scope,
            Domain = "d",
            Key = key,
            Value = "second-value"
        });

        string json = await this._fixture.Tool.Recall(scope, "d", key, null);

        using var doc = JsonDocument.Parse(json);
        var hits = doc.RootElement.EnumerateArray().ToList();
        Assert.Single(hits);
        Assert.Equal("second-value", hits[0].GetProperty("Value").GetString());
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Shared in-memory SQLite fixture. A keep-alive connection prevents the in-memory database
    /// from being discarded between test connections.
    /// </summary>
    public sealed class MemoryToolFixture : IAsyncLifetime
    {
        private readonly SqliteConnection _keepAlive;
        private readonly string _runId = Guid.NewGuid().ToString("N")[..8];

        /// <summary>
        /// Initializes a new instance of <see cref="MemoryToolFixture"/> with a shared in-memory SQLite store.
        /// </summary>
        public MemoryToolFixture()
        {
            string dbName = $"memory_tool_tests_{Guid.NewGuid():N}";
            string connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";

            this._keepAlive = new SqliteConnection(connectionString);
            this._keepAlive.Open();

            this.Runner = new SqliteRunner(connectionString);

            var logger = new Mock<ILogger<SqliteKVStore>>();
            this.KVStore = new SqliteKVStore(this.Runner, logger.Object);
            this.Tool = new MemoryTool(this.KVStore);
        }

        /// <summary>Gets the SQLite runner used for schema operations.</summary>
        public SqliteRunner Runner { get; }

        /// <summary>Gets the in-memory KV store.</summary>
        public SqliteKVStore KVStore { get; }

        /// <summary>Gets the <see cref="MemoryTool"/> under test.</summary>
        public MemoryTool Tool { get; }

        /// <summary>Returns a run-scoped unique name for test isolation.</summary>
        public string UniqueName(string prefix) => $"{prefix}_{this._runId}";

        /// <summary>Initializes the store schema before tests run.</summary>
        public async ValueTask InitializeAsync()
        {
            await this.KVStore.InitializeSchemaAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>Closes the keep-alive connection after all tests complete.</summary>
        public async ValueTask DisposeAsync()
        {
            await this._keepAlive.CloseAsync();
        }
    }
}
