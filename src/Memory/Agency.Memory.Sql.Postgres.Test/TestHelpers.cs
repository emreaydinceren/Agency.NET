using Agency.Embeddings.Common;
using Agency.Memory.Sql.Postgres;
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
    /// Builds a <see cref="NpgsqlDataSource"/> from user secrets / environment variables.
    /// Uses the key <c>ConnectionStrings:PostgreSql</c>.
    /// </summary>
    internal static NpgsqlDataSource BuildDataSource<TSecretContext>() where TSecretContext : class
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<TSecretContext>()
            .AddEnvironmentVariables()
            .Build();

        var cs = config.GetConnectionString("PostgreSql")
            ?? throw new InvalidOperationException("Connection string 'PostgreSql' not found.");

        var builder = new NpgsqlDataSourceBuilder(cs);
        builder.UseVector();
        return builder.Build();
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
