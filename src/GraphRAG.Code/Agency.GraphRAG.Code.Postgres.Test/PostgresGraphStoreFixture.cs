using Agency.Common;
using Agency.Embeddings.Common;
using Agency.GraphRAG.Code.Postgres.Migrations;
using Agency.GraphRAG.Code.Storage;
using Agency.Sql.Postgre;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Agency.GraphRAG.Code.Postgres.Test;

/// <summary>
/// Creates an isolated PostgreSQL schema plus a configured graph store for a single functional test.
/// </summary>
public sealed class PostgresGraphStoreFixture : IAsyncDisposable
{
    private readonly PostgreSqlRunner _rootRunner;

    private PostgresGraphStoreFixture(
        string schema,
        PostgreSqlRunner rootRunner,
        PostgreSqlRunner runner,
        IEmbeddingGenerator embeddingGenerator,
        IGraphStore store)
    {
        this.Schema = schema;
        this._rootRunner = rootRunner;
        this.Runner = runner;
        this.EmbeddingGenerator = embeddingGenerator;
        this.Store = store;
    }

    /// <summary>
    /// Gets the schema dedicated to the current test instance.
    /// </summary>
    public string Schema { get; }

    /// <summary>
    /// Gets the configured PostgreSQL runner for direct verification queries.
    /// </summary>
    public PostgreSqlRunner Runner { get; }

    /// <summary>
    /// Gets the deterministic embedding generator used by the store.
    /// </summary>
    public IEmbeddingGenerator EmbeddingGenerator { get; }

    /// <summary>
    /// Gets the graph store under test.
    /// </summary>
    public IGraphStore Store { get; }

    /// <summary>
    /// Creates a fresh fixture instance, provisions a unique schema, and applies the latest migrations.
    /// </summary>
    /// <returns>The initialized fixture.</returns>
    public static async Task<PostgresGraphStoreFixture> CreateAsync()
    {
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddUserSecrets<PostgresGraphStoreFixture>()
            .AddEnvironmentVariables()
            .Build();

        string? baseConnectionString = config.GetConnectionString("PostgreSql");
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            throw new InvalidOperationException(
                "Connection string is not configured. Please set it in user secrets or environment variables with the key 'ConnectionStrings:PostgreSql'.");
        }

        string schema = $"graphrag_code_{Guid.NewGuid():N}";
        var rootRunner = new PostgreSqlRunner(baseConnectionString);
        await rootRunner.ExecuteAsync($"""CREATE SCHEMA "{schema}";""");

        string schemaConnectionString = CreateSchemaScopedConnectionString(baseConnectionString, schema);
        var migrationRunner = new PostgresMigrationRunner(schemaConnectionString);
        await migrationRunner.MigrateAsync();

        var runner = new PostgreSqlRunner(schemaConnectionString);
        await EnsureSchemaObjectsAsync(rootRunner, runner, schema);
        IEmbeddingGenerator embeddingGenerator = new FakeEmbeddingGenerator();
        IGraphStore store = CreateStore(runner, schemaConnectionString, embeddingGenerator);

        return new PostgresGraphStoreFixture(schema, rootRunner, runner, embeddingGenerator, store);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await this.Runner.DisposeAsync();
        await this._rootRunner.ExecuteAsync($"""DROP SCHEMA IF EXISTS "{this.Schema}" CASCADE;""");
        await this._rootRunner.DisposeAsync();
    }

    /// <summary>
    /// Executes a SQL statement against the isolated test schema.
    /// </summary>
    /// <param name="sql">The SQL to execute.</param>
    /// <param name="parameters">Optional SQL parameters.</param>
    public Task ExecuteAsync(string sql, Dictionary<string, object?>? parameters = null)
        => this.Runner.ExecuteAsync(sql, parameters, TestContext.Current.CancellationToken);

    /// <summary>
    /// Returns the value produced by a scalar count query.
    /// </summary>
    /// <param name="sql">The SQL to execute.</param>
    /// <param name="parameters">Optional SQL parameters.</param>
    /// <returns>The scalar count converted to <see cref="long"/>.</returns>
    public async Task<long> CountAsync(string sql, Dictionary<string, object?>? parameters = null)
    {
        Dataset dataSet = await this.Runner.QueryAsync(sql, parameters, TestContext.Current.CancellationToken);
        Assert.Single(dataSet.Rows);
        return Convert.ToInt64(dataSet[0, 0], CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Queries a single row and maps it with the supplied projector.
    /// </summary>
    /// <typeparam name="T">The projected result type.</typeparam>
    /// <param name="sql">The SQL to execute.</param>
    /// <param name="projector">The row projection function.</param>
    /// <param name="parameters">Optional SQL parameters.</param>
    /// <returns>The projected row value.</returns>
    public async Task<T> QuerySingleAsync<T>(
        string sql,
        Func<Dataset, int, T> projector,
        Dictionary<string, object?>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(projector);

        Dataset dataSet = await this.Runner.QueryAsync(sql, parameters, TestContext.Current.CancellationToken);
        Assert.Single(dataSet.Rows);
        return projector(dataSet, 0);
    }

    /// <summary>
    /// Formats an embedding for PostgreSQL <c>vector</c> casts.
    /// </summary>
    /// <param name="embedding">The embedding values.</param>
    /// <returns>The pgvector text literal.</returns>
    public static string ToVectorLiteral(IReadOnlyList<float> embedding)
        => $"[{string.Join(",", embedding.Select(value => value.ToString("G9", CultureInfo.InvariantCulture)))}]";

    /// <summary>
    /// Parses a pgvector text literal into its float components.
    /// </summary>
    /// <param name="raw">The raw pgvector text literal.</param>
    /// <returns>The parsed vector.</returns>
    public static float[] ParseVectorLiteral(string raw)
        => raw.Trim('[', ']')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => float.Parse(value, CultureInfo.InvariantCulture))
            .ToArray();

    private static string CreateSchemaScopedConnectionString(string baseConnectionString, string schema)
    {
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            SearchPath = $"{schema},public",
            Options = $"-c search_path={schema},public",
        };

        return builder.ConnectionString;
    }

    private static IGraphStore CreateStore(
        PostgreSqlRunner runner,
        string connectionString,
        IEmbeddingGenerator embeddingGenerator)
    {
        Assembly postgresAssembly = typeof(PostgresMigrationRunner).Assembly;
        Type? storeType = postgresAssembly.GetType("Agency.GraphRAG.Code.Postgres.PostgresGraphStore", throwOnError: false);

        if (storeType is null)
        {
            throw new InvalidOperationException(
                "PostgresGraphStore was not found. Rebase onto the production implementation lane before running these tests.");
        }

        Type loggerType = typeof(NullLogger<>).MakeGenericType(storeType);
        object logger =
            loggerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? loggerType.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? throw new InvalidOperationException($"Could not create a null logger for '{storeType.FullName}'.");

        ConstructorInfo? constructor = storeType
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .OrderByDescending(candidate => candidate.GetParameters().Length)
            .FirstOrDefault(candidate =>
            {
                ParameterInfo[] parameters = candidate.GetParameters();
                return TryCreateArguments(parameters, runner, connectionString, embeddingGenerator, logger, out _);
            });

        if (constructor is null)
        {
            throw new InvalidOperationException(
                $"Could not find a supported PostgresGraphStore constructor on '{storeType.FullName}'.");
        }

        _ = TryCreateArguments(constructor.GetParameters(), runner, connectionString, embeddingGenerator, logger, out object?[]? arguments);
        object? instance = constructor.Invoke(arguments);
        if (instance is not IGraphStore store)
        {
            throw new InvalidOperationException("PostgresGraphStore does not implement IGraphStore.");
        }

        return store;
    }

    private static bool TryCreateArguments(
        IReadOnlyList<ParameterInfo> parameters,
        PostgreSqlRunner runner,
        string connectionString,
        IEmbeddingGenerator embeddingGenerator,
        object logger,
        out object?[]? arguments)
    {
        arguments = new object?[parameters.Count];

        for (int index = 0; index < parameters.Count; index++)
        {
            Type parameterType = parameters[index].ParameterType;

            if (parameterType.IsAssignableFrom(typeof(PostgreSqlRunner)))
            {
                arguments[index] = runner;
            }
            else if (parameterType == typeof(string))
            {
                arguments[index] = connectionString;
            }
            else if (parameterType.IsAssignableFrom(typeof(IEmbeddingGenerator)))
            {
                arguments[index] = embeddingGenerator;
            }
            else if (parameterType.IsInstanceOfType(logger))
            {
                arguments[index] = logger;
            }
            else if (parameters[index].HasDefaultValue)
            {
                arguments[index] = parameters[index].DefaultValue;
            }
            else
            {
                arguments = null;
                return false;
            }
        }

        return true;
    }

    private static async Task EnsureSchemaObjectsAsync(PostgreSqlRunner rootRunner, PostgreSqlRunner runner, string schema)
    {
        Dataset dataSet = await rootRunner.QueryAsync(
            """
            SELECT COUNT(*)
            FROM information_schema.tables
            WHERE table_schema = @schema
              AND table_name = 'repos';
            """,
            new Dictionary<string, object?> { ["schema"] = schema },
            TestContext.Current.CancellationToken);

        if (Convert.ToInt64(dataSet[0, 0], CultureInfo.InvariantCulture) > 0)
        {
            return;
        }

        await rootRunner.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS vector;", cancellationToken: TestContext.Current.CancellationToken);
        await rootRunner.ExecuteAsync("CREATE EXTENSION IF NOT EXISTS pg_trgm;", cancellationToken: TestContext.Current.CancellationToken);

        await runner.ExecuteAsync(
            $"""
            CREATE TABLE IF NOT EXISTS repos (
                id UUID PRIMARY KEY,
                remote_url TEXT NULL,
                root_path TEXT NOT NULL,
                indexed_commit TEXT NULL,
                indexed_at TIMESTAMPTZ NULL,
                is_shallow BOOLEAN NOT NULL
            );

            CREATE TABLE IF NOT EXISTS projects (
                id UUID PRIMARY KEY,
                repo_id UUID NOT NULL,
                name TEXT NOT NULL,
                manifest_path TEXT NULL,
                path TEXT NOT NULL,
                language TEXT NOT NULL,
                ecosystem TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS external_packages (
                id UUID PRIMARY KEY,
                project_id UUID NOT NULL,
                name TEXT NOT NULL,
                version TEXT NULL,
                version_resolved TEXT NULL,
                ecosystem TEXT NOT NULL,
                scope TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS files (
                id UUID PRIMARY KEY,
                repo_id UUID NOT NULL,
                project_id UUID NOT NULL,
                path TEXT NOT NULL,
                language TEXT NOT NULL,
                content_hash TEXT NULL,
                last_indexed_at TIMESTAMPTZ NULL
            );

            CREATE TABLE IF NOT EXISTS modules (
                id UUID PRIMARY KEY,
                project_id UUID NOT NULL,
                file_id UUID NULL,
                name TEXT NOT NULL,
                path TEXT NULL,
                kind TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS symbols (
                id UUID PRIMARY KEY,
                file_id UUID NOT NULL,
                module_id UUID NULL,
                name TEXT NOT NULL,
                fully_qualified_name TEXT NULL,
                kind TEXT NOT NULL,
                signature TEXT NULL,
                summary TEXT NULL,
                one_line_summary TEXT NULL,
                embedding vector({M0001_InitialSchema.DefaultEmbeddingDimensions}) NULL,
                content_hash TEXT NULL,
                is_utility BOOLEAN NOT NULL,
                source_range_start INTEGER NOT NULL,
                source_range_end INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS clusters (
                id UUID PRIMARY KEY,
                label TEXT NOT NULL,
                summary TEXT NULL,
                embedding vector({M0001_InitialSchema.DefaultEmbeddingDimensions}) NULL,
                coherence REAL NOT NULL,
                type TEXT NOT NULL,
                level INTEGER NULL
            );

            CREATE TABLE IF NOT EXISTS edges (
                id UUID PRIMARY KEY,
                source_id UUID NOT NULL,
                source_kind TEXT NOT NULL,
                target_id UUID NOT NULL,
                target_kind TEXT NOT NULL,
                edge_kind TEXT NOT NULL,
                confidence REAL NOT NULL,
                signals JSONB NOT NULL DEFAULT '[]'::jsonb,
                properties JSONB NOT NULL DEFAULT jsonb_build_object()
            );

            CREATE TABLE IF NOT EXISTS unresolved_call_sites (
                id UUID PRIMARY KEY,
                source_symbol_id UUID NOT NULL,
                source_file_id UUID NOT NULL,
                identifier TEXT NOT NULL,
                scope TEXT NULL,
                llm_extracted_target TEXT NULL
            );
            """,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Deterministic embedding generator used by PostgreSQL graph-store tests.
    /// </summary>
    private sealed class FakeEmbeddingGenerator : IEmbeddingGenerator
    {
        /// <inheritdoc />
        public Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string input, CancellationToken cancellationToken = default)
            => Task.FromResult(new ReadOnlyMemory<float>(CreateVector(input)));

        /// <inheritdoc />
        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(
            IEnumerable<string> inputs,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ReadOnlyMemory<float>> vectors = inputs
                .Select(input => new ReadOnlyMemory<float>(CreateVector(input)))
                .ToArray();

            return Task.FromResult(vectors);
        }

        /// <summary>
        /// Creates the deterministic vector that corresponds to the supplied text.
        /// </summary>
        /// <param name="input">The text to encode.</param>
        /// <returns>The generated vector.</returns>
        public static float[] CreateVector(string input)
        {
            ArgumentNullException.ThrowIfNull(input);

            byte[] bytes = Encoding.UTF8.GetBytes(input);
            if (bytes.Length == 0)
            {
                return new float[M0001_InitialSchema.DefaultEmbeddingDimensions];
            }

            float[] vector = new float[M0001_InitialSchema.DefaultEmbeddingDimensions];
            for (int index = 0; index < vector.Length; index++)
            {
                vector[index] = bytes[index % bytes.Length] / 255f;
            }

            return vector;
        }
    }
}
