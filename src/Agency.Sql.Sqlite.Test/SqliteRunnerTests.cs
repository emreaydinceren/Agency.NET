using Microsoft.Data.Sqlite;

namespace Agency.Sql.Sqlite.Test;

/// <summary>
/// Integration tests for <see cref="SqliteRunner"/> using a named in-memory SQLite database.
/// No external server required — runs fully in-process.
/// </summary>
public sealed class SqliteRunnerTests : IClassFixture<SqliteRunnerTests.DatabaseFixture>
{
    private readonly DatabaseFixture _fixture;

    /// <summary>
    /// Creates the test class with its shared database fixture.
    /// </summary>
    public SqliteRunnerTests(DatabaseFixture fixture)
    {
        this._fixture = fixture;
    }

    // ── Constructor validation ──────────────────────────────────────────────

    /// <summary>
    /// Verifies that an invalid connection string is rejected.
    /// </summary>
    [Fact]
    public void Constructor_NullOrWhitespaceConnectionString_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new SqliteRunner(null!));
        Assert.Throws<ArgumentException>(() => new SqliteRunner("   "));
    }

    // ── ExecuteAsync validation ─────────────────────────────────────────────

    /// <summary>
    /// Verifies that SQL validation rejects empty statements for ExecuteAsync.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NullOrWhitespaceSql_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => this._fixture.Runner.ExecuteAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => this._fixture.Runner.ExecuteAsync("   "));
    }

    // ── QueryAsync validation ───────────────────────────────────────────────

    /// <summary>
    /// Verifies that SQL validation rejects empty statements for QueryAsync.
    /// </summary>
    [Fact]
    public async Task QueryAsync_NullOrWhitespaceSql_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => this._fixture.Runner.QueryAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => this._fixture.Runner.QueryAsync("   "));
    }

    // ── Functional: DDL ────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that DDL can create and drop a table.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CreateAndDropTable_Succeeds()
    {
        var table = this._fixture.UniqueName("ddl_test");

        await this._fixture.Runner.ExecuteAsync($"""
            CREATE TABLE IF NOT EXISTS {table} (
                id   INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL
            )
            """);

        // Verify table exists by querying sqlite_master
        var ds = await this._fixture.Runner.QueryAsync($"""
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
              AND name = '{table}'
            """);

        Assert.Single(ds.Rows);

        await this._fixture.Runner.ExecuteAsync($"DROP TABLE {table}");
    }

    // ── Functional: INSERT and row-count ───────────────────────────────────

    /// <summary>
    /// Verifies that INSERT statements report the affected row count.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_InsertRows_ReturnsRowsAffected()
    {
        var name1 = this._fixture.UniqueName("insert1");
        var name2 = this._fixture.UniqueName("insert2");
        int affected = await this._fixture.Runner.ExecuteAsync($"""
            INSERT INTO {this._fixture.Table} (name, score)
            VALUES ('{name1}', 1.1), ('{name2}', 2.2)
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
        var uniqueName = this._fixture.UniqueName("insert_test");
        await this._fixture.Runner.ExecuteAsync($"""
            INSERT INTO {this._fixture.Table} (name, score) VALUES ('{uniqueName}', 3.3)
            """);

        var ds = await this._fixture.Runner.QueryAsync($"""
            SELECT name, score FROM {this._fixture.Table} WHERE name = '{uniqueName}'
            """);

        Assert.Single(ds.Rows);
        Assert.Equal(uniqueName, ds["name", 0]);
        Assert.Equal(3.3, Convert.ToDouble(ds["score", 0]), precision: 5);
    }

    // ── Functional: SELECT with named parameter ─────────────────────────────

    /// <summary>
    /// Verifies that named parameters filter rows correctly.
    /// </summary>
    [Fact]
    public async Task QueryAsync_WithParameter_FiltersCorrectly()
    {
        var uniqueName1 = this._fixture.UniqueName("param_test1");
        var uniqueName2 = this._fixture.UniqueName("param_test2");
        await this._fixture.Runner.ExecuteAsync($"""
            INSERT INTO {this._fixture.Table} (name, score)
            VALUES ('{uniqueName1}', 4.4), ('{uniqueName2}', 5.5)
            """);

        var ds = await this._fixture.Runner.QueryAsync(
            $"SELECT name FROM {this._fixture.Table} WHERE name = @name",
            new Dictionary<string, object?> { ["name"] = uniqueName1 });

        Assert.Single(ds.Rows);
        Assert.Equal(uniqueName1, ds["name", 0]);
    }

    // ── Functional: NULL round-trip ────────────────────────────────────────

    /// <summary>
    /// Verifies that NULL values round-trip as null.
    /// </summary>
    [Fact]
    public async Task QueryAsync_NullColumnValue_ReturnedAsNull()
    {
        var uniqueName = this._fixture.UniqueName("null_value_test");
        await this._fixture.Runner.ExecuteAsync($"""
            INSERT INTO {this._fixture.Table} (name, score) VALUES ('{uniqueName}', NULL)
            """);

        var ds = await this._fixture.Runner.QueryAsync($"""
            SELECT score FROM {this._fixture.Table} WHERE name = '{uniqueName}'
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
        var ds = await this._fixture.Runner.QueryAsync($"""
            SELECT * FROM {this._fixture.Table} WHERE name = 'does_not_exist'
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
        var uniqueName1 = this._fixture.UniqueName("update_test1");
        var uniqueName2 = this._fixture.UniqueName("update_test2");
        await this._fixture.Runner.ExecuteAsync($"""
            INSERT INTO {this._fixture.Table} (name, score) VALUES ('{uniqueName1}', 6.6), ('{uniqueName2}', 7.7)
            """);

        int affected = await this._fixture.Runner.ExecuteAsync(
            $"UPDATE {this._fixture.Table} SET score = @score WHERE name = @name",
            new Dictionary<string, object?> { ["score"] = 99.9, ["name"] = uniqueName1 });

        Assert.Equal(1, affected);

        var ds = await this._fixture.Runner.QueryAsync($"""
            SELECT score FROM {this._fixture.Table} WHERE name = '{uniqueName1}'
            """);

        Assert.Equal(99.9, Convert.ToDouble(ds["score", 0]), precision: 5);
    }

    // ── QueryAsync<T> validation ───────────────────────────────────────────

    /// <summary>
    /// Verifies that SQL validation rejects empty statements for QueryAsync&lt;T&gt;.
    /// </summary>
    [Fact]
    public async Task QueryAsyncGeneric_NullOrWhitespaceSql_ThrowsArgumentException()
    {
        var predicate = (System.Data.Common.DbDataReader reader) => Task.FromResult(new TestModel { Name = "", Score = 0 });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            this._fixture.Runner.QueryAsync<TestModel>(null!, predicate));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            this._fixture.Runner.QueryAsync<TestModel>("   ", predicate));
    }

    /// <summary>
    /// Verifies that null predicate is rejected for QueryAsync&lt;T&gt;.
    /// </summary>
    [Fact]
    public async Task QueryAsyncGeneric_NullPredicate_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            this._fixture.Runner.QueryAsync<TestModel>($"SELECT * FROM {this._fixture.Table}", null!));
    }

    // ── Functional: QueryAsync<T> basic mapping ───────────────────────────

    /// <summary>
    /// Verifies that rows can be mapped to a custom model using the predicate.
    /// </summary>
    [Fact]
    public async Task QueryAsyncGeneric_BasicMapping_ReturnsTypedResults()
    {
        var uniqueName = this._fixture.UniqueName("model_test");
        await this._fixture.Runner.ExecuteAsync($"""
            INSERT INTO {this._fixture.Table} (name, score) VALUES ('{uniqueName}', 42.5)
            """);

        var results = await this._fixture.Runner.QueryAsync<TestModel>(
            $"SELECT name, score FROM {this._fixture.Table} WHERE name = '{uniqueName}'",
            async reader => new TestModel
            {
                Name = reader.GetString(0),
                Score = reader.GetDouble(1)
            });

        Assert.Single(results);
        Assert.Equal(uniqueName, results[0].Name);
        Assert.Equal(42.5, results[0].Score, precision: 5);
    }

    /// <summary>
    /// Verifies that multiple rows are mapped correctly.
    /// </summary>
    [Fact]
    public async Task QueryAsyncGeneric_MultipleRows_ReturnsAllMappedRows()
    {
        var row1 = this._fixture.UniqueName("row1");
        var row2 = this._fixture.UniqueName("row2");
        var row3 = this._fixture.UniqueName("row3");
        await this._fixture.Runner.ExecuteAsync($"""
            INSERT INTO {this._fixture.Table} (name, score)
            VALUES ('{row1}', 1.1), ('{row2}', 2.2), ('{row3}', 3.3)
            """);

        var results = await this._fixture.Runner.QueryAsync<TestModel>(
            $"""
            SELECT name, score FROM {this._fixture.Table}
            WHERE name IN ('{row1}', '{row2}', '{row3}')
            ORDER BY name
            """,
            async reader => new TestModel
            {
                Name = reader.GetString(0),
                Score = reader.GetDouble(1)
            });

        Assert.Equal(3, results.Count);
        Assert.Equal(row1, results[0].Name);
        Assert.Equal(row2, results[1].Name);
        Assert.Equal(row3, results[2].Name);
    }

    /// <summary>
    /// Verifies that empty result sets return an empty list.
    /// </summary>
    [Fact]
    public async Task QueryAsyncGeneric_NoMatchingRows_ReturnsEmptyList()
    {
        var results = await this._fixture.Runner.QueryAsync<TestModel>(
            $"SELECT name, score FROM {this._fixture.Table} WHERE name = 'nonexistent'",
            async reader => new TestModel
            {
                Name = reader.GetString(0),
                Score = reader.GetDouble(1)
            });

        Assert.Empty(results);
    }

    // ── Functional: QueryAsync<T> with NULL values ─────────────────────────

    /// <summary>
    /// Verifies that NULL values are handled correctly in the predicate.
    /// </summary>
    [Fact]
    public async Task QueryAsyncGeneric_NullColumnValue_HandledInPredicate()
    {
        var uniqueName = this._fixture.UniqueName("generic_null_value");
        await this._fixture.Runner.ExecuteAsync($"""
            INSERT INTO {this._fixture.Table} (name, score) VALUES ('{uniqueName}', NULL)
            """);

        var results = await this._fixture.Runner.QueryAsync<TestModel>(
            $"SELECT name, score FROM {this._fixture.Table} WHERE name = '{uniqueName}'",
            async reader => new TestModel
            {
                Name = reader.GetString(0),
                Score = reader.IsDBNull(1) ? 0 : reader.GetDouble(1)
            });

        Assert.Single(results);
        Assert.Equal(uniqueName, results[0].Name);
        Assert.Equal(0, results[0].Score);
    }

    // ── Functional: QueryAsync<T> with parameters ──────────────────────────

    /// <summary>
    /// Verifies that parameterized queries work with the generic method.
    /// </summary>
    [Fact]
    public async Task QueryAsyncGeneric_WithParameters_FiltersCorrectly()
    {
        var uniqueName1 = this._fixture.UniqueName("param1");
        var uniqueName2 = this._fixture.UniqueName("param2");
        await this._fixture.Runner.ExecuteAsync($"""
            INSERT INTO {this._fixture.Table} (name, score)
            VALUES ('{uniqueName1}', 10.5), ('{uniqueName2}', 20.5)
            """);

        var results = await this._fixture.Runner.QueryAsync<TestModel>(
            $"SELECT name, score FROM {this._fixture.Table} WHERE name = @name",
            async reader => new TestModel
            {
                Name = reader.GetString(0),
                Score = reader.GetDouble(1)
            },
            new Dictionary<string, object?> { ["name"] = uniqueName1 });

        Assert.Single(results);
        Assert.Equal(uniqueName1, results[0].Name);
        Assert.Equal(10.5, results[0].Score, precision: 5);
    }

    // ── Functional: QueryAsync<T> with async predicate ────────────────────

    /// <summary>
    /// Verifies that the predicate can perform async operations.
    /// </summary>
    [Fact]
    public async Task QueryAsyncGeneric_AsyncPredicate_SupportsAsyncOperations()
    {
        var uniqueName = this._fixture.UniqueName("async_test");
        await this._fixture.Runner.ExecuteAsync($"""
            INSERT INTO {this._fixture.Table} (name, score) VALUES ('{uniqueName}', 55.5)
            """);

        var results = await this._fixture.Runner.QueryAsync<TestModel>(
            $"SELECT name, score FROM {this._fixture.Table} WHERE name = '{uniqueName}'",
            async reader =>
            {
                // Simulate async operation
                await Task.Delay(1);
                return new TestModel
                {
                    Name = reader.GetString(0),
                    Score = reader.GetDouble(1)
                };
            });

        Assert.Single(results);
        Assert.Equal(uniqueName, results[0].Name);
    }

    /// <summary>
    /// Verifies that exceptions in the predicate propagate correctly.
    /// </summary>
    [Fact]
    public async Task QueryAsyncGeneric_PredicateThrowsException_ExceptionPropagates()
    {
        var uniqueName = this._fixture.UniqueName("error_test");
        await this._fixture.Runner.ExecuteAsync($"""
            INSERT INTO {this._fixture.Table} (name, score) VALUES ('{uniqueName}', 1.0)
            """);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            this._fixture.Runner.QueryAsync<TestModel>(
                $"SELECT name, score FROM {this._fixture.Table} WHERE name = '{uniqueName}'",
                async reader => throw new InvalidOperationException("Predicate error")));
    }

    /// <summary>
    /// Verifies that the predicate can access columns by name.
    /// </summary>
    [Fact]
    public async Task QueryAsyncGeneric_AccessColumnByName_ReturnsCorrectValues()
    {
        var uniqueName = this._fixture.UniqueName("column_name_test");
        await this._fixture.Runner.ExecuteAsync($"""
            INSERT INTO {this._fixture.Table} (name, score) VALUES ('{uniqueName}', 77.7)
            """);

        var results = await this._fixture.Runner.QueryAsync<TestModel>(
            $"SELECT name, score FROM {this._fixture.Table} WHERE name = '{uniqueName}'",
            async reader => new TestModel
            {
                Name = reader["name"].ToString()!,
                Score = (double)reader["score"]
            });

        Assert.Single(results);
        Assert.Equal(uniqueName, results[0].Name);
        Assert.Equal(77.7, results[0].Score, precision: 5);
    }

    // ── Models and Fixture ──────────────────────────────────────────────────

    /// <summary>
    /// Simple test model for QueryAsync&lt;T&gt; tests.
    /// </summary>
    public class TestModel
    {
        /// <summary>Gets or sets the name.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Gets or sets the score.</summary>
        public double Score { get; set; }
    }

    /// <summary>
    /// Creates a named in-memory SQLite database and a dedicated test table before the test class runs.
    /// Holds a keep-alive connection so the in-memory database persists for the lifetime of the fixture.
    /// </summary>
    public sealed class DatabaseFixture : IAsyncLifetime
    {
        private readonly SqliteConnection _keepAlive;
        private readonly string _runId = Guid.NewGuid().ToString("N")[..8];

        public DatabaseFixture()
        {
            var dbName = $"runner_tests_{Guid.NewGuid():N}";
            var connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";

            // Keep one connection open so the in-memory database persists between runner operations
            this._keepAlive = new SqliteConnection(connectionString);
            this._keepAlive.Open();

            this.Runner = new SqliteRunner(connectionString);
        }

        /// <summary>
        /// Gets the shared SQLite runner.
        /// </summary>
        public SqliteRunner Runner { get; }

        /// <summary>Fully-qualified table name used by the shared test data table.</summary>
        public string Table { get; private set; } = default!;

        /// <summary>Returns a unique name scoped to this test run.</summary>
        public string UniqueName(string prefix) => $"{prefix}_{this._runId}";

        /// <summary>
        /// Creates the dedicated test table.
        /// </summary>
        public async Task InitializeAsync()
        {
            this.Table = this.UniqueName("runner_tests");

            await this.Runner.ExecuteAsync($"""
                CREATE TABLE {this.Table} (
                    id    INTEGER PRIMARY KEY AUTOINCREMENT,
                    name  TEXT NOT NULL,
                    score REAL NULL
                )
                """);
        }

        /// <summary>
        /// Drops the dedicated test table and closes the keep-alive connection.
        /// </summary>
        public async Task DisposeAsync()
        {
            await this.Runner.ExecuteAsync($"DROP TABLE IF EXISTS {this.Table}");
            await this._keepAlive.CloseAsync();
        }
    }
}
