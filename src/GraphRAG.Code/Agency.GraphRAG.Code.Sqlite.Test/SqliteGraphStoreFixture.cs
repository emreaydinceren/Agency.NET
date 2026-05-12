using Agency.Embeddings.Common;
using Agency.GraphRAG.Code.Sqlite.Migrations;
using Agency.GraphRAG.Code.Storage;
using Agency.Sql.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Agency.GraphRAG.Code.Sqlite.Test;

/// <summary>
/// Creates an isolated SQLite database plus a configured graph store for a single test.
/// </summary>
public sealed class SqliteGraphStoreFixture : IAsyncDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString;

    private SqliteGraphStoreFixture(
        string connectionString,
        SqliteConnection keepAlive,
        SqliteRunner runner,
        IEmbeddingGenerator embeddingGenerator,
        IGraphStore store)
    {
        this._connectionString = connectionString;
        this._keepAlive = keepAlive;
        this.Runner = runner;
        this.EmbeddingGenerator = embeddingGenerator;
        this.Store = store;
    }

    /// <summary>
    /// Gets the configured SQLite runner for direct verification queries.
    /// </summary>
    public SqliteRunner Runner { get; }

    /// <summary>
    /// Gets the deterministic embedding generator used by the store.
    /// </summary>
    public IEmbeddingGenerator EmbeddingGenerator { get; }

    /// <summary>
    /// Gets the graph store under test.
    /// </summary>
    public IGraphStore Store { get; }

    /// <summary>
    /// Creates a fresh fixture instance and applies the current SQLite schema migrations.
    /// </summary>
    /// <returns>The initialized fixture.</returns>
    public static async Task<SqliteGraphStoreFixture> CreateAsync()
    {
        string databaseName = $"graphrag_code_store_{Guid.NewGuid():N}";
        string connectionString = $"Data Source={databaseName};Mode=Memory;Cache=Shared";

        var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        ConfigureConnection(keepAlive);

        var migrations = new SqliteMigrationRunner(connectionString);
        await migrations.MigrateToLatestAsync(new MigrationContext { EmbeddingDimensions = FakeEmbeddingGenerator.Dimensions });

        var runner = new SqliteRunner(connectionString, onConnectionOpen: ConfigureConnection);
        IEmbeddingGenerator embeddingGenerator = new FakeEmbeddingGenerator();
        IGraphStore store = CreateStore(runner, embeddingGenerator);

        return new SqliteGraphStoreFixture(connectionString, keepAlive, runner, embeddingGenerator, store);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await this._keepAlive.CloseAsync();
        await this._keepAlive.DisposeAsync();
    }

    /// <summary>
    /// Executes a SQL statement against the fixture database.
    /// </summary>
    public async Task ExecuteAsync(string sql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var connection = await this.OpenConnectionAsync();
        await using var command = CreateCommand(connection, sql, parameters);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Returns the number of rows produced by a scalar count query.
    /// </summary>
    public async Task<long> CountAsync(string sql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var connection = await this.OpenConnectionAsync();
        await using var command = CreateCommand(connection, sql, parameters);
        object? result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Queries a single row and maps it with the supplied delegate.
    /// </summary>
    public async Task<T> QuerySingleAsync<T>(
        string sql,
        Func<SqliteDataReader, T> projector,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var connection = await this.OpenConnectionAsync();
        await using var command = CreateCommand(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync(), "Expected at least one result row.");
        T result = projector(reader);
        Assert.False(await reader.ReadAsync(), "Expected exactly one result row.");
        return result;
    }

    /// <summary>
    /// Parses a stored embedding value from either BLOB or JSON/TEXT format.
    /// </summary>
    public static float[] ReadStoredEmbedding(object dbValue)
    {
        return dbValue switch
        {
            byte[] blob when TryDecodeFloatBlob(blob, out float[]? vector) => vector!,
            byte[] blob => JsonSerializer.Deserialize<float[]>(Encoding.UTF8.GetString(blob)) ?? [],
            string text => JsonSerializer.Deserialize<float[]>(text) ?? [],
            _ => throw new InvalidOperationException($"Unsupported embedding value type '{dbValue.GetType().FullName}'."),
        };
    }

    private static IGraphStore CreateStore(SqliteRunner runner, IEmbeddingGenerator embeddingGenerator)
    {
        Assembly sqliteAssembly = typeof(SqliteMigrationRunner).Assembly;
        Type? storeType = sqliteAssembly.GetType("Agency.GraphRAG.Code.Sqlite.SqliteGraphStore", throwOnError: false);

        if (storeType is null)
        {
            throw new InvalidOperationException("SqliteGraphStore was not found. Rebase onto the production implementation lane before running these tests.");
        }

        Type loggerType = typeof(NullLogger<>).MakeGenericType(storeType);
        object logger =
            loggerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? loggerType.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? throw new InvalidOperationException($"Could not create a null logger for '{storeType.FullName}'.");

        object? instance = Activator.CreateInstance(storeType, runner, embeddingGenerator, FakeEmbeddingGenerator.Dimensions, logger);
        if (instance is not IGraphStore store)
        {
            throw new InvalidOperationException("SqliteGraphStore does not implement IGraphStore.");
        }

        return store;
    }

    private async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection(this._connectionString);
        await connection.OpenAsync();
        ConfigureConnection(connection);
        return connection;
    }

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        string sql,
        IReadOnlyDictionary<string, object?>? parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;

        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }
        }

        return command;
    }

    private static void ConfigureConnection(SqliteConnection connection)
    {
        SqliteMigrationRunner.ConfigureConnection(connection);
        EnsureDistanceFunction(connection);
    }

    private static void EnsureDistanceFunction(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT vec_distance_cosine('[1,0]', '[1,0]');";
            _ = command.ExecuteScalar();
        }
        catch (SqliteException)
        {
            connection.CreateFunction("vec_distance_cosine", (string v1, string v2) =>
            {
                float[] a = ParseVector(v1);
                float[] b = ParseVector(v2);
                double dot = a.Zip(b).Sum(pair => (double)pair.First * pair.Second);
                double normA = Math.Sqrt(a.Sum(x => (double)x * x));
                double normB = Math.Sqrt(b.Sum(x => (double)x * x));

                if (normA == 0 || normB == 0)
                {
                    return 1.0;
                }

                return 1.0 - (dot / (normA * normB));
            });
        }
    }

    private static float[] ParseVector(string raw)
        => raw.Trim('[', ']')
              .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
              .Select(value => float.Parse(value, CultureInfo.InvariantCulture))
              .ToArray();

    private static bool TryDecodeFloatBlob(byte[] blob, out float[]? vector)
    {
        if (blob.Length > 0 && blob.Length % sizeof(float) == 0)
        {
            vector = new float[blob.Length / sizeof(float)];
            for (int index = 0; index < vector.Length; index++)
            {
                vector[index] = BitConverter.ToSingle(blob, index * sizeof(float));
            }

            return true;
        }

        vector = null;
        return false;
    }
}
