using Agency.Sql;

namespace Agency.Sql.Test;

/// <summary>
/// Functional tests that run against the real PostgreSQL instance defined in docker-compose.yml.
/// Requires the container to be running: docker compose up -d
/// Connection: Host=llm-host.example;Port=5432;Username=dev_user;Password=dev_password;Database=dev_db
/// Run with:  dotnet test --filter "Category=Functional"
/// Skip with: dotnet test --filter "Category!=Functional"
/// </summary>
[Trait("Category", "Functional")]
/// <summary>
/// Functional tests for <see cref="Agency.Sql.PostgreSqlRunner"/>.
/// </summary>
public sealed class PostgreSqlRunnerTests : IClassFixture<PostgreSqlRunnerTests.DatabaseFixture>
{
    private readonly DatabaseFixture _fixture;

    /// <summary>
    /// Creates the test class with its shared database fixture.
    /// </summary>
    public PostgreSqlRunnerTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    // ── Constructor validation ──────────────────────────────────────────────

    /// <summary>
    /// Verifies that an invalid connection string is rejected.
    /// </summary>
    [Fact]
    public void Constructor_NullOrWhitespaceConnectionString_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new PostgreSqlRunner(null!));
        Assert.Throws<ArgumentException>(() => new PostgreSqlRunner("   "));
    }

    // ── ExecuteAsync validation ─────────────────────────────────────────────

    /// <summary>
    /// Verifies that SQL validation rejects empty statements for ExecuteAsync.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NullOrWhitespaceSql_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _fixture.Runner.ExecuteAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => _fixture.Runner.ExecuteAsync("   "));
    }

    // ── QueryAsync validation ───────────────────────────────────────────────

    /// <summary>
    /// Verifies that SQL validation rejects empty statements for QueryAsync.
    /// </summary>
    [Fact]
    public async Task QueryAsync_NullOrWhitespaceSql_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _fixture.Runner.QueryAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => _fixture.Runner.QueryAsync("   "));
    }

    // ── Functional: DDL ────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that DDL can create and drop a table.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CreateAndDropTable_Succeeds()
    {
        var table = _fixture.UniqueName("ddl_test");

        await _fixture.Runner.ExecuteAsync($"""
            CREATE TABLE IF NOT EXISTS {table} (
                id   SERIAL PRIMARY KEY,
                name TEXT NOT NULL
            )
            """);

        // Verify table exists by querying information_schema
        var ds = await _fixture.Runner.QueryAsync($"""
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'public'
              AND table_name = '{table}'
            """);

        Assert.Single(ds.Rows);

        await _fixture.Runner.ExecuteAsync($"DROP TABLE {table}");
    }

    // ── Functional: INSERT and row-count ───────────────────────────────────

    /// <summary>
    /// Verifies that INSERT statements report the affected row count.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_InsertRows_ReturnsRowsAffected()
    {
        int affected = await _fixture.Runner.ExecuteAsync($"""
            INSERT INTO {_fixture.Table} (name, score)
            VALUES ('alpha', 1.1), ('beta', 2.2)
            """);

        Assert.Equal(2, affected);
    }

    // ── Functional: SELECT all rows ────────────────────────────────────────

    /// <summary>
    /// Verifies that inserted rows can be queried back.
    /// </summary>
    [Fact]
    public async Task QueryAsync_AfterInsert_ReturnsExpectedRows()
    {
        await _fixture.Runner.ExecuteAsync($"""
            INSERT INTO {_fixture.Table} (name, score) VALUES ('gamma', 3.3)
            """);

        var ds = await _fixture.Runner.QueryAsync($"""
            SELECT name, score FROM {_fixture.Table} WHERE name = 'gamma'
            """);

        Assert.Single(ds.Rows);
        Assert.Equal("gamma", ds["name", 0]);
        Assert.Equal(3.3, Convert.ToDouble(ds["score", 0]), precision: 5);
    }

    // ── Functional: SELECT with named parameter ─────────────────────────────

    /// <summary>
    /// Verifies that named parameters filter rows correctly.
    /// </summary>
    [Fact]
    public async Task QueryAsync_WithParameter_FiltersCorrectly()
    {
        await _fixture.Runner.ExecuteAsync($"""
            INSERT INTO {_fixture.Table} (name, score)
            VALUES ('delta', 4.4), ('epsilon', 5.5)
            """);

        var ds = await _fixture.Runner.QueryAsync(
            $"SELECT name FROM {_fixture.Table} WHERE name = @name",
            new Dictionary<string, object?> { ["name"] = "delta" });

        Assert.Single(ds.Rows);
        Assert.Equal("delta", ds["name", 0]);
    }

    // ── Functional: NULL round-trip ────────────────────────────────────────

    /// <summary>
    /// Verifies that NULL values round-trip as null.
    /// </summary>
    [Fact]
    public async Task QueryAsync_NullColumnValue_ReturnedAsNull()
    {
        await _fixture.Runner.ExecuteAsync($"""
            INSERT INTO {_fixture.Table} (name, score) VALUES ('null_score', NULL)
            """);

        var ds = await _fixture.Runner.QueryAsync($"""
            SELECT score FROM {_fixture.Table} WHERE name = 'null_score'
            """);

        Assert.Single(ds.Rows);
        Assert.Null(ds["score", 0]);
    }

    // ── Functional: empty result set ───────────────────────────────────────

    /// <summary>
    /// Verifies that no matching rows produces an empty result set.
    /// </summary>
    [Fact]
    public async Task QueryAsync_NoMatchingRows_ReturnsEmptyList()
    {
        var ds = await _fixture.Runner.QueryAsync($"""
            SELECT * FROM {_fixture.Table} WHERE name = 'does_not_exist'
            """);

        Assert.Empty(ds.Rows);
    }

    // ── Functional: UPDATE ─────────────────────────────────────────────────

    /// <summary>
    /// Verifies that UPDATE statements report the correct row count.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_Update_ReturnsCorrectRowCount()
    {
        await _fixture.Runner.ExecuteAsync($"""
            INSERT INTO {_fixture.Table} (name, score) VALUES ('zeta', 6.6), ('eta', 7.7)
            """);

        int affected = await _fixture.Runner.ExecuteAsync(
            $"UPDATE {_fixture.Table} SET score = @score WHERE name = @name",
            new Dictionary<string, object?> { ["score"] = 99.9, ["name"] = "zeta" });

        Assert.Equal(1, affected);

        var ds = await _fixture.Runner.QueryAsync($"""
            SELECT score FROM {_fixture.Table} WHERE name = 'zeta'
            """);

        Assert.Equal(99.9, Convert.ToDouble(ds["score", 0]), precision: 5);
    }

    // ── Fixture ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a dedicated test table before the test class runs and drops it afterwards.
    /// Each test run uses a unique table name to support parallel CI execution.
    /// </summary>
    /// <summary>
    /// Shared database fixture for PostgreSQL integration tests.
    /// </summary>
    public sealed class DatabaseFixture : IAsyncLifetime
    {
        private const string ConnectionString =
            "Host=llm-host.example;Port=5432;Username=dev_user;Password=dev_password;Database=dev_db";

        private readonly string _runId = Guid.NewGuid().ToString("N")[..8];

        /// <summary>
        /// Gets the shared PostgreSQL runner.
        /// </summary>
        public PostgreSqlRunner Runner { get; } = new(ConnectionString);

        /// <summary>Fully-qualified table name used by the shared test data table.</summary>
        public string Table { get; private set; } = default!;

        /// <summary>Returns a unique table name scoped to this test run.</summary>
        public string UniqueName(string prefix) => $"{prefix}_{_runId}";

        /// <summary>
        /// Creates the dedicated test table.
        /// </summary>
        public async Task InitializeAsync()
        {
            Table = UniqueName("runner_tests");

            await Runner.ExecuteAsync($"""
                CREATE TABLE {Table} (
                    id    SERIAL PRIMARY KEY,
                    name  TEXT    NOT NULL,
                    score DOUBLE PRECISION NULL
                )
                """);
        }

        /// <summary>
        /// Drops the dedicated test table.
        /// </summary>
        public async Task DisposeAsync()
        {
            await Runner.ExecuteAsync($"DROP TABLE IF EXISTS {Table}");
        }
    }
}
