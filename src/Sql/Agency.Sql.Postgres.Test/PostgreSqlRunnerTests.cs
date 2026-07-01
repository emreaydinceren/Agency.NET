using Microsoft.Extensions.Configuration;

namespace Agency.Sql.Postgres.Test;

/// <summary>
/// Functional tests for <see cref="Agency.Sql.Postgres.PostgreSqlRunner"/> that run against the real PostgreSQL
/// instance defined in docker-compose.yml. Requires the container to be running: docker compose up -d
/// Connection configured via <c>ConnectionStrings:PostgreSql</c> in appsettings.json.
/// Run with: dotnet test --filter "Category=Functional" Skip with: dotnet test --filter "Category!=Functional"
/// </summary>
[Trait("Category", "Functional")]
public sealed class PostgreSqlRunnerTests : IClassFixture<PostgreSqlRunnerTests.DatabaseFixture>
{
    private readonly DatabaseFixture _fixture;

    /// <summary>
    /// Creates the test class with its shared database fixture.
    /// </summary>
    public PostgreSqlRunnerTests(DatabaseFixture fixture)
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
        await Assert.ThrowsAsync<ArgumentException>(() => this._fixture.Runner.ExecuteAsync(null!, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => this._fixture.Runner.ExecuteAsync("   ", cancellationToken: TestContext.Current.CancellationToken));
    }

    // ── QueryAsync validation ───────────────────────────────────────────────

    /// <summary>
    /// Verifies that SQL validation rejects empty statements for QueryAsync.
    /// </summary>
    [Fact]
    public async Task QueryAsync_NullOrWhitespaceSql_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => this._fixture.Runner.QueryAsync(null!, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => this._fixture.Runner.QueryAsync("   ", cancellationToken: TestContext.Current.CancellationToken));
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
                id   SERIAL PRIMARY KEY,
                name TEXT NOT NULL
            )
            """, cancellationToken: TestContext.Current.CancellationToken);

        // Verify table exists by querying information_schema
        var ds = await this._fixture.Runner.QueryAsync($"""
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'public'
              AND table_name = '{table}'
            """, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(ds.Rows);

        await this._fixture.Runner.ExecuteAsync($"DROP TABLE {table}", cancellationToken: TestContext.Current.CancellationToken);
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
            """, cancellationToken: TestContext.Current.CancellationToken);

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
            """, cancellationToken: TestContext.Current.CancellationToken);

        var ds = await this._fixture.Runner.QueryAsync($"""
            SELECT name, score FROM {this._fixture.Table} WHERE name = '{uniqueName}'
            """, cancellationToken: TestContext.Current.CancellationToken);

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
            """, cancellationToken: TestContext.Current.CancellationToken);

        var ds = await this._fixture.Runner.QueryAsync(
            $"SELECT name FROM {this._fixture.Table} WHERE name = @name",
            new Dictionary<string, object?> { ["name"] = uniqueName1 }, cancellationToken: TestContext.Current.CancellationToken);

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
            """, cancellationToken: TestContext.Current.CancellationToken);

        var ds = await this._fixture.Runner.QueryAsync($"""
            SELECT score FROM {this._fixture.Table} WHERE name = '{uniqueName}'
            """, cancellationToken: TestContext.Current.CancellationToken);

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
            """, cancellationToken: TestContext.Current.CancellationToken);

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
            """, cancellationToken: TestContext.Current.CancellationToken);

        int affected = await this._fixture.Runner.ExecuteAsync(
            $"UPDATE {this._fixture.Table} SET score = @score WHERE name = @name",
            new Dictionary<string, object?> { ["score"] = 99.9, ["name"] = uniqueName1 }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, affected);

        var ds = await this._fixture.Runner.QueryAsync($"""
            SELECT score FROM {this._fixture.Table} WHERE name = '{uniqueName1}'
            """, cancellationToken: TestContext.Current.CancellationToken);

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
            this._fixture.Runner.QueryAsync<TestModel>(null!, predicate, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            this._fixture.Runner.QueryAsync<TestModel>("   ", predicate, cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies that null predicate is rejected for QueryAsync&lt;T&gt;.
    /// </summary>
    [Fact]
    public async Task QueryAsyncGeneric_NullPredicate_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            this._fixture.Runner.QueryAsync<TestModel>($"SELECT * FROM {this._fixture.Table}", null!, cancellationToken: TestContext.Current.CancellationToken));
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
            """, cancellationToken: TestContext.Current.CancellationToken);

        var results = await this._fixture.Runner.QueryAsync<TestModel>(
            $"SELECT name, score FROM {this._fixture.Table} WHERE name = '{uniqueName}'",
            async reader => new TestModel
            {
                Name = reader.GetString(0),
                Score = reader.GetDouble(1)
            }, cancellationToken: TestContext.Current.CancellationToken);

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
            """, cancellationToken: TestContext.Current.CancellationToken);

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
            }, cancellationToken: TestContext.Current.CancellationToken);

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
            }, cancellationToken: TestContext.Current.CancellationToken);

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
            """, cancellationToken: TestContext.Current.CancellationToken);

        var results = await this._fixture.Runner.QueryAsync<TestModel>(
            $"SELECT name, score FROM {this._fixture.Table} WHERE name = '{uniqueName}'",
            async reader => new TestModel
            {
                Name = reader.GetString(0),
                Score = reader.IsDBNull(1) ? 0 : reader.GetDouble(1)
            }, cancellationToken: TestContext.Current.CancellationToken);

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
            """, cancellationToken: TestContext.Current.CancellationToken);

        var results = await this._fixture.Runner.QueryAsync<TestModel>(
            $"SELECT name, score FROM {this._fixture.Table} WHERE name = @name",
            async reader => new TestModel
            {
                Name = reader.GetString(0),
                Score = reader.GetDouble(1)
            },
            new Dictionary<string, object?> { ["name"] = uniqueName1 }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal(uniqueName1, results[0].Name);
        Assert.Equal(10.5, results[0].Score, precision: 5);
    }

    // ── Functional: QueryAsync<T> with complex types ───────────────────────

    /// <summary>
    /// Verifies that the predicate can perform async operations.
    /// </summary>
    [Fact]
    public async Task QueryAsyncGeneric_AsyncPredicate_SupportsAsyncOperations()
    {
        var uniqueName = this._fixture.UniqueName("async_test");
        await this._fixture.Runner.ExecuteAsync($"""
            INSERT INTO {this._fixture.Table} (name, score) VALUES ('{uniqueName}', 55.5)
            """, cancellationToken: TestContext.Current.CancellationToken);

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
            }, cancellationToken: TestContext.Current.CancellationToken);

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
            """, cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            this._fixture.Runner.QueryAsync<TestModel>(
                $"SELECT name, score FROM {this._fixture.Table} WHERE name = '{uniqueName}'",
                async reader => throw new InvalidOperationException("Predicate error"), cancellationToken: TestContext.Current.CancellationToken));
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
            """, cancellationToken: TestContext.Current.CancellationToken);

        var results = await this._fixture.Runner.QueryAsync<TestModel>(
            $"SELECT name, score FROM {this._fixture.Table} WHERE name = '{uniqueName}'",
            async reader => new TestModel
            {
                Name = reader["name"].ToString()!,
                Score = (double)reader["score"]
            }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(results);
        Assert.Equal(uniqueName, results[0].Name);
        Assert.Equal(77.7, results[0].Score, precision: 5);
    }

    // ── Fixture ─────────────────────────────────────────────────────────────

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
    /// Creates a dedicated test table before the test class runs and drops it afterwards. Each test run uses a unique
    /// table name to support parallel CI execution.
    /// </summary>
    /// <summary>
    /// Shared database fixture for PostgreSQL integration tests.
    /// </summary>
    public sealed class DatabaseFixture : IAsyncLifetime
    {
        /// <summary>
        /// Creates the runner from the configured connection string.
        /// </summary>
        public DatabaseFixture() {

            var config = new ConfigurationBuilder()
               .AddUserSecrets<PostgreSqlRunnerTests>()
               .AddEnvironmentVariables()
               .Build();

            var connectionString =
                config.GetConnectionString("PostgreSql");

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "Connection string is not configured. Please set it in user secrets or environment variables with the key 'ConnectionStrings:PostgreSql'.");
            }

            this.Runner = new PostgreSqlRunner(connectionString);
        }

        private readonly string _runId = Guid.NewGuid().ToString("N")[..8];

        /// <summary>
        /// Gets the shared PostgreSQL runner.
        /// </summary>
        public PostgreSqlRunner Runner { get; }

        /// <summary>Fully-qualified table name used by the shared test data table.</summary>
        public string Table { get; private set; } = default!;

        /// <summary>Returns a unique table name scoped to this test run.</summary>
        public string UniqueName(string prefix) => $"{prefix}_{this._runId}";

        /// <summary>
        /// Creates the dedicated test table.
        /// </summary>
        public async ValueTask InitializeAsync()
        {
            this.Table = this.UniqueName("runner_tests");

            await this.Runner.ExecuteAsync($"""
                CREATE TABLE {this.Table} (
                    id    SERIAL PRIMARY KEY,
                    name  TEXT    NOT NULL,
                    score DOUBLE PRECISION NULL
                )
                """);
        }

        /// <summary>
        /// Drops the dedicated test table and disposes the runner's connection pool.
        /// </summary>
        public async Task DisposeAsync()
        {
            await this.Runner.ExecuteAsync($"DROP TABLE IF EXISTS {this.Table}");
            await this.Runner.DisposeAsync();
        }

        ValueTask IAsyncDisposable.DisposeAsync() => this.Runner.DisposeAsync();
    }
}
