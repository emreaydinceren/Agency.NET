using Agency.GraphRAG.Code.Sqlite.Migrations;
using Microsoft.Data.Sqlite;

namespace Agency.GraphRAG.Code.Sqlite.Test.Migrations;

/// <summary>
/// Integration tests for GraphRAG SQLite schema migrations.
/// </summary>
public sealed class SqliteMigrationRunnerTests : IClassFixture<SqliteMigrationRunnerTests.DatabaseFixture>
{
    private static readonly string[] ExpectedTables =
    [
        "repos",
        "projects",
        "external_packages",
        "files",
        "modules",
        "symbols",
        "edges",
        "clusters",
        "unresolved_call_sites",
    ];

    private static readonly string[] ExpectedIndexes =
    [
        "idx_symbols_file_id",
        "idx_symbols_module_id",
        "idx_symbols_name",
        "idx_edges_source_id_edge_kind",
        "idx_edges_target_id_edge_kind",
        "idx_edges_edge_kind_confidence",
    ];

    private readonly DatabaseFixture _fixture;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteMigrationRunnerTests"/> class.
    /// </summary>
    public SqliteMigrationRunnerTests(DatabaseFixture fixture)
    {
        this._fixture = fixture;
    }

    [Fact]
    public async Task MigrateToLatestAsync_CreatesExpectedBaseTables()
    {
        var names = await this._fixture.QueryNamesAsync("table");

        foreach (string expectedTable in ExpectedTables)
        {
            Assert.Contains(expectedTable, names);
        }
    }

    [Fact]
    public async Task MigrateToLatestAsync_CreatesExpectedIndexes()
    {
        var names = await this._fixture.QueryNamesAsync("index");

        foreach (string expectedIndex in ExpectedIndexes)
        {
            Assert.Contains(expectedIndex, names);
        }
    }

    [Fact]
    public async Task MigrateToLatestAsync_CreatesFtsAndVectorVirtualTables()
    {
        var objects = await this._fixture.QueryObjectDefinitionsAsync("table", ["symbols_fts", "symbols_vec", "clusters_vec"]);

        Assert.Contains("CREATE VIRTUAL TABLE symbols_fts USING fts5", objects["symbols_fts"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE VIRTUAL TABLE symbols_vec USING vec0", objects["symbols_vec"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FLOAT[8]", objects["symbols_vec"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE VIRTUAL TABLE clusters_vec USING vec0", objects["clusters_vec"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FLOAT[8]", objects["clusters_vec"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MigrateToLatestAsync_CreatesFtsSynchronizationTriggers()
    {
        var triggers = await this._fixture.QueryObjectDefinitionsAsync("trigger", ["trg_symbols_ai_fts", "trg_symbols_au_fts", "trg_symbols_ad_fts"]);

        Assert.Contains("AFTER INSERT ON symbols", triggers["trg_symbols_ai_fts"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AFTER UPDATE ON symbols", triggers["trg_symbols_au_fts"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AFTER DELETE ON symbols", triggers["trg_symbols_ad_fts"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SymbolsFtsTriggers_KeepIndexInSync()
    {
        string repoId = Guid.NewGuid().ToString("D");
        string projectId = Guid.NewGuid().ToString("D");
        string fileId = Guid.NewGuid().ToString("D");
        string symbolId = Guid.NewGuid().ToString("D");

        await this._fixture.ExecuteAsync(
            """
            INSERT INTO repos (id, remote_url, root_path, indexed_commit, indexed_at, is_shallow)
            VALUES ($repoId, NULL, 'E:\Repos\Agency', NULL, NULL, 0);

            INSERT INTO projects (id, repo_id, name, relative_path, manifest_path, language, ecosystem)
            VALUES ($projectId, $repoId, 'TestProject', 'src', NULL, 'csharp', NULL);

            INSERT INTO files (id, repo_id, project_id, path, language, content_hash, last_indexed_at)
            VALUES ($fileId, $repoId, $projectId, 'src\Test.cs', 'csharp', NULL, NULL);

            INSERT INTO symbols (
                id, file_id, module_id, name, fully_qualified_name, kind, signature, summary, one_line_summary,
                embedding, content_hash, is_utility, source_range_start, source_range_end
            )
            VALUES (
                $id, $fileId, NULL, 'OriginalName', NULL, 'Class', NULL, NULL, NULL,
                NULL, NULL, 0, 1, 10
            );
            """,
            new()
            {
                ["$repoId"] = repoId,
                ["$projectId"] = projectId,
                ["$fileId"] = fileId,
                ["$id"] = symbolId,
            });

        Assert.Equal(1L, await this._fixture.CountAsync("SELECT COUNT(*) FROM symbols_fts WHERE symbols_fts MATCH 'OriginalName';"));

        await this._fixture.ExecuteAsync(
            "UPDATE symbols SET name = 'RenamedSymbol' WHERE id = $id;",
            new() { ["$id"] = symbolId });

        Assert.Equal(0L, await this._fixture.CountAsync("SELECT COUNT(*) FROM symbols_fts WHERE symbols_fts MATCH 'OriginalName';"));
        Assert.Equal(1L, await this._fixture.CountAsync("SELECT COUNT(*) FROM symbols_fts WHERE symbols_fts MATCH 'RenamedSymbol';"));

        await this._fixture.ExecuteAsync(
            "DELETE FROM symbols WHERE id = $id;",
            new() { ["$id"] = symbolId });

        Assert.Equal(0L, await this._fixture.CountAsync("SELECT COUNT(*) FROM symbols_fts WHERE symbols_fts MATCH 'RenamedSymbol';"));
    }

    /// <summary>
    /// Shared in-memory database fixture used by the migration tests.
    /// </summary>
    public sealed class DatabaseFixture : IAsyncLifetime
    {
        private readonly SqliteConnection _keepAlive;
        private readonly string _connectionString;

        /// <summary>
        /// Initializes a new instance of the <see cref="DatabaseFixture"/> class.
        /// </summary>
        public DatabaseFixture()
        {
            string databaseName = $"graphrag_code_sqlite_{Guid.NewGuid():N}";
            this._connectionString = $"Data Source={databaseName};Mode=Memory;Cache=Shared";
            this._keepAlive = new SqliteConnection(this._connectionString);
            this._keepAlive.Open();
        }

        public async ValueTask InitializeAsync()
        {
            var runner = new SqliteMigrationRunner(this._connectionString);
            await runner.MigrateToLatestAsync(new MigrationContext { EmbeddingDimensions = 8 }, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await this._keepAlive.CloseAsync();
        }

        public async Task<HashSet<string>> QueryNamesAsync(string objectType)
        {
            await using var connection = new SqliteConnection(this._connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT name
                FROM sqlite_master
                WHERE type = $type
                  AND name NOT LIKE 'sqlite_%'
                ORDER BY name;
                """;
            command.Parameters.AddWithValue("$type", objectType);

            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            var names = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                names.Add(reader.GetString(0));
            }

            return names;
        }

        public async Task<Dictionary<string, string>> QueryObjectDefinitionsAsync(string objectType, IReadOnlyList<string> names)
        {
            await using var connection = new SqliteConnection(this._connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            await using var command = connection.CreateCommand();
            var parameterNames = new List<string>(names.Count);
            for (int index = 0; index < names.Count; index++)
            {
                string parameterName = $"$name{index}";
                parameterNames.Add(parameterName);
                command.Parameters.AddWithValue(parameterName, names[index]);
            }

            command.CommandText =
                $"""
                SELECT name, sql
                FROM sqlite_master
                WHERE type = $type
                  AND name IN ({string.Join(", ", parameterNames)})
                ORDER BY name;
                """;
            command.Parameters.AddWithValue("$type", objectType);

            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            var objects = new Dictionary<string, string>(StringComparer.Ordinal);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                objects[reader.GetString(0)] = reader.GetString(1);
            }

            return objects;
        }

        public async Task ExecuteAsync(string sql, Dictionary<string, object?> parameters)
        {
            await using var connection = new SqliteConnection(this._connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        public async Task<long> CountAsync(string sql)
        {
            await using var connection = new SqliteConnection(this._connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            object? result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            return Convert.ToInt64(result);
        }
    }
}
