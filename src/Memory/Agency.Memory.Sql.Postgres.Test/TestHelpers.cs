using Agency.Embeddings.Common;
using Microsoft.Extensions.Configuration;
using Moq;
using Npgsql;

namespace Agency.Memory.Sql.Postgres.Test;

/// <summary>
/// Shared helpers for functional test classes in this project.
/// </summary>
internal static class TestHelpers
{
    /// <summary>
    /// Per-project Postgres schema. Isolating the <c>records</c>/<c>watermarks</c>/<c>dead_letter</c>/
    /// <c>user_state</c> tables here keeps this test assembly's schema resets from racing with
    /// <c>Agency.Memory.Functional.Test</c> (which targets the same database but a different schema)
    /// when both assemblies run concurrently under <c>dotnet test</c>.
    /// </summary>
    private const string TestSchema = "mem_sql_test";

    /// <summary>
    /// Builds a <see cref="NpgsqlDataSource"/> from user secrets / environment variables.
    /// Uses the key <c>ConnectionStrings:PostgreSql</c>.
    /// </summary>
    /// <remarks>
    /// Every physical connection opened by the returned data source is initialised to point at
    /// <see cref="TestSchema"/> (with <c>public</c> as a fallback so the <c>vector</c> extension
    /// type remains reachable). This guarantees that all DDL/DML for this assembly's tests
    /// targets a private schema, regardless of how connections are pooled or reused.
    /// </remarks>
    internal static NpgsqlDataSource BuildDataSource<TSecretContext>() where TSecretContext : class
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<TSecretContext>()
            .AddEnvironmentVariables()
            .Build();

        var cs = config.GetConnectionString("PostgreSql")
            ?? throw new InvalidOperationException("Connection string 'PostgreSql' not found.");

        // Preserve the SET search_path issued by the physical-connection initializer across
        // pool re-acquires. Without this Npgsql resets the session (DISCARD ALL) on close,
        // which would drop us back to the default search_path and reintroduce the cross-project
        // race on the public `records` table.
        var csb = new NpgsqlConnectionStringBuilder(cs) { NoResetOnClose = true };
        var builder = new NpgsqlDataSourceBuilder(csb.ConnectionString);
        builder.UseVector();
        ConfigureSchemaIsolation(builder, TestSchema);
        return builder.Build();
    }

    /// <summary>
    /// Wires a physical-connection initialiser that pins every new connection to
    /// <paramref name="schemaName"/>. The schema is created on first use; the search_path is set
    /// so that unqualified DDL/DML lands in the project schema while still resolving the shared
    /// <c>vector</c> type from <c>public</c>.
    /// </summary>
    private static void ConfigureSchemaIsolation(NpgsqlDataSourceBuilder builder, string schemaName)
    {
        string initSql = $"CREATE SCHEMA IF NOT EXISTS {schemaName}; SET search_path TO {schemaName}, public;";

        builder.UsePhysicalConnectionInitializer(
            conn =>
            {
                using var cmd = new NpgsqlCommand(initSql, conn);
                cmd.ExecuteNonQuery();
            },
            async conn =>
            {
                await using var cmd = new NpgsqlCommand(initSql, conn);
                await cmd.ExecuteNonQueryAsync();
            });
    }

    /// <summary>
    /// Drops the <c>records</c> table (CASCADE) and re-initialises the schema with the given dimension.
    /// Call this in <c>InitializeAsync</c> to ensure the right vector dimension regardless of what a
    /// previous test class left behind.
    /// </summary>
    internal static async Task ResetSchemaAsync(NpgsqlDataSource dataSource, int dim, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var dropCmd = new NpgsqlCommand("DROP TABLE IF EXISTS records CASCADE;", (NpgsqlConnection)conn);
        await dropCmd.ExecuteNonQueryAsync(ct);

        await new MemorySchemaInitializer(dataSource).InitializeAsync(dim, ct);

        await using var truncCmd = new NpgsqlCommand(
            "TRUNCATE TABLE watermarks, dead_letter, user_state;",
            (NpgsqlConnection)conn);
        await truncCmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Creates a deterministic <see cref="IEmbeddingGenerator"/> that produces vectors
    /// of the specified dimension using a hash of the input text.
    /// </summary>
    internal static IEmbeddingGenerator DeterministicEmbedder(int dim)
    {
        var mock = new Mock<IEmbeddingGenerator>();
        mock.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((input, _) =>
            {
                var rng = new Random(input.GetHashCode());
                var arr = new float[dim];
                for (int i = 0; i < dim; i++)
                {
                    arr[i] = (float)rng.NextDouble();
                }

                return Task.FromResult((ReadOnlyMemory<float>)arr.AsMemory());
            });
        return mock.Object;
    }
}
