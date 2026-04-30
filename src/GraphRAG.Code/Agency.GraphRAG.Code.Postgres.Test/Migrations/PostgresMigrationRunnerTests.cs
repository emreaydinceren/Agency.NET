using Agency.GraphRAG.Code.Postgres.Migrations;
using Agency.Sql.Postgre;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Agency.GraphRAG.Code.Postgres.Test.Migrations;

/// <summary>
/// Functional tests for GraphRAG.Code PostgreSQL migrations.
/// </summary>
[Trait("Category", "Functional")]
public sealed class PostgresMigrationRunnerTests : IClassFixture<PostgresMigrationRunnerTests.DatabaseFixture>
{
    private readonly DatabaseFixture _fixture;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresMigrationRunnerTests"/> class.
    /// </summary>
    /// <param name="fixture">Shared PostgreSQL fixture.</param>
    public PostgresMigrationRunnerTests(DatabaseFixture fixture)
    {
        this._fixture = fixture;
    }

    [Fact]
    public async Task MigrateAsync_CreatesExpectedTables()
    {
        var dataSet = await this._fixture.Runner.QueryAsync("""
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = current_schema()
              AND table_type = 'BASE TABLE'
              AND table_name IN (
                  'repos',
                  'projects',
                  'external_packages',
                  'files',
                  'modules',
                  'symbols',
                  'edges',
                  'clusters',
                  'unresolved_call_sites')
            ORDER BY table_name
            """, cancellationToken: TestContext.Current.CancellationToken);

        var tableNames = Enumerable.Range(0, dataSet.Rows.Count)
            .Select(rowIndex => dataSet["table_name", rowIndex]?.ToString() ?? string.Empty)
            .ToArray();

        Assert.Equal(
            [
                "clusters",
                "edges",
                "external_packages",
                "files",
                "modules",
                "projects",
                "repos",
                "symbols",
                "unresolved_call_sites",
            ],
            tableNames);
    }

    [Fact]
    public async Task MigrateAsync_CreatesExpectedExtensions()
    {
        var dataSet = await this._fixture.Runner.QueryAsync("""
            SELECT extname
            FROM pg_extension
            WHERE extname IN ('pg_trgm', 'vector')
            ORDER BY extname
            """, cancellationToken: TestContext.Current.CancellationToken);

        var extensions = Enumerable.Range(0, dataSet.Rows.Count)
            .Select(rowIndex => dataSet["extname", rowIndex]?.ToString() ?? string.Empty)
            .ToArray();

        Assert.Equal(["pg_trgm", "vector"], extensions);
    }

    [Fact]
    public async Task MigrateAsync_CreatesExpectedColumnTypes()
    {
        var columnDataSet = await this._fixture.Runner.QueryAsync("""
            SELECT table_name, column_name, data_type, udt_name
            FROM information_schema.columns
            WHERE table_schema = current_schema()
              AND (
                    (table_name = 'edges' AND column_name IN ('confidence', 'signals', 'properties'))
                 OR (table_name = 'symbols' AND column_name = 'embedding')
                 OR (table_name = 'clusters' AND column_name = 'embedding')
              )
            ORDER BY table_name, column_name
            """, cancellationToken: TestContext.Current.CancellationToken);

        var rows = Enumerable.Range(0, columnDataSet.Rows.Count)
            .Select(rowIndex => new
            {
                Table = columnDataSet["table_name", rowIndex]?.ToString(),
                Column = columnDataSet["column_name", rowIndex]?.ToString(),
                DataType = columnDataSet["data_type", rowIndex]?.ToString(),
                Udt = columnDataSet["udt_name", rowIndex]?.ToString(),
            })
            .ToDictionary(row => $"{row.Table}.{row.Column}", StringComparer.Ordinal);

        Assert.Equal("real", rows["edges.confidence"].DataType);
        Assert.Equal("jsonb", rows["edges.properties"].DataType);
        Assert.Equal("jsonb", rows["edges.signals"].DataType);
        Assert.Equal("USER-DEFINED", rows["clusters.embedding"].DataType);
        Assert.Equal("vector", rows["clusters.embedding"].Udt);
        Assert.Equal("USER-DEFINED", rows["symbols.embedding"].DataType);
        Assert.Equal("vector", rows["symbols.embedding"].Udt);

        var symbolEmbeddingTypmod = await this._fixture.Runner.QueryAsync("""
            SELECT atttypmod
            FROM pg_attribute
            WHERE attrelid = 'symbols'::regclass
              AND attname = 'embedding'
            """, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(symbolEmbeddingTypmod.Rows);
        Assert.Equal(M0001_InitialSchema.DefaultEmbeddingDimensions, Convert.ToInt32(symbolEmbeddingTypmod["atttypmod", 0]));
    }

    [Fact]
    public async Task MigrateAsync_CreatesExpectedIndexes()
    {
        var dataSet = await this._fixture.Runner.QueryAsync("""
            SELECT indexname, indexdef
            FROM pg_indexes
            WHERE schemaname = current_schema()
              AND indexname IN (
                  'ix_symbols_file_id',
                  'ix_symbols_module_id',
                  'ix_symbols_name',
                  'ix_symbols_name_trgm',
                  'ix_symbols_embedding_hnsw',
                  'ix_clusters_embedding_hnsw',
                  'ix_edges_source_id_edge_kind',
                  'ix_edges_target_id_edge_kind',
                  'ix_edges_edge_kind_confidence')
            ORDER BY indexname
            """, cancellationToken: TestContext.Current.CancellationToken);

        var indexMap = Enumerable.Range(0, dataSet.Rows.Count).ToDictionary(
            rowIndex => dataSet["indexname", rowIndex]?.ToString() ?? string.Empty,
            rowIndex => dataSet["indexdef", rowIndex]?.ToString() ?? string.Empty,
            StringComparer.Ordinal);

        Assert.Equal(9, indexMap.Count);
        Assert.Contains("USING gin (name gin_trgm_ops)", indexMap["ix_symbols_name_trgm"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("USING hnsw (embedding vector_cosine_ops)", indexMap["ix_symbols_embedding_hnsw"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("USING hnsw (embedding vector_cosine_ops)", indexMap["ix_clusters_embedding_hnsw"], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Shared PostgreSQL fixture for migration tests.
    /// </summary>
    public sealed class DatabaseFixture : IAsyncLifetime
    {
        private readonly PostgreSqlRunner _rootRunner;

        /// <summary>
        /// Initializes a new instance of the <see cref="DatabaseFixture"/> class.
        /// </summary>
        public DatabaseFixture()
        {
            var config = new ConfigurationBuilder()
                .AddUserSecrets<PostgresMigrationRunnerTests>()
                .AddEnvironmentVariables()
                .Build();

            var connectionString = config.GetConnectionString("PostgreSql");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "Connection string is not configured. Please set it in user secrets or environment variables with the key 'PostgreSql:ConnectionString'.");
            }

            this.Schema = $"graphrag_migration_{Guid.NewGuid():N}";
            this._rootRunner = new PostgreSqlRunner(connectionString);

            string schemaConnectionString = CreateSchemaScopedConnectionString(connectionString, this.Schema);
            this.Runner = new PostgreSqlRunner(schemaConnectionString);
            this.MigrationRunner = new PostgresMigrationRunner(schemaConnectionString);
        }

        /// <summary>
        /// Gets the isolated schema name used by this fixture.
        /// </summary>
        public string Schema { get; }

        /// <summary>
        /// Gets the raw PostgreSQL runner.
        /// </summary>
        public PostgreSqlRunner Runner { get; }

        /// <summary>
        /// Gets the migration runner under test.
        /// </summary>
        public PostgresMigrationRunner MigrationRunner { get; }

        /// <inheritdoc />
        public async ValueTask InitializeAsync()
        {
            await this._rootRunner.ExecuteAsync($"CREATE SCHEMA IF NOT EXISTS \"{this.Schema}\";");
            await this.MigrationRunner.MigrateAsync();
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await this._rootRunner.ExecuteAsync($"DROP SCHEMA IF EXISTS \"{this.Schema}\" CASCADE;");
            await this.Runner.DisposeAsync();
            await this._rootRunner.DisposeAsync();
        }

        private static string CreateSchemaScopedConnectionString(string baseConnectionString, string schema)
        {
            var builder = new NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                SearchPath = $"{schema},public",
                Options = $"-c search_path={schema},public",
            };

            return builder.ConnectionString;
        }
    }
}
