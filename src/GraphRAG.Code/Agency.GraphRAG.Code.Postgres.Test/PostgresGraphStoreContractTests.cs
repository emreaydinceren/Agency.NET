using Agency.Embeddings.Common;
using Agency.GraphRAG.Code.Postgres;
using Agency.GraphRAG.Code.Postgres.Migrations;
using Agency.GraphRAG.Code.Storage;
using Agency.GraphRAG.Code.Test.Storage;
using Agency.Sql.Postgre;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;

namespace Agency.GraphRAG.Code.Postgres.Test;

/// <summary>
/// Runs the shared graph-store contract against the PostgreSQL implementation.
/// </summary>
[Trait("Category", "Functional")]
public sealed class PostgresGraphStoreContractTests : IGraphStoreContractTests
{
    /// <inheritdoc />
    protected override IGraphStore CreateGraphStore()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddUserSecrets<PostgresGraphStoreFixture>()
            .AddEnvironmentVariables()
            .Build();

        string connectionString = configuration.GetConnectionString("PostgreSql")
            ?? throw new InvalidOperationException("ConnectionStrings:PostgreSql must be configured for Postgres graph-store contract tests.");

        return new PostgresGraphStore(
            new PostgreSqlRunner(connectionString),
            new ContractEmbeddingGenerator(),
            connectionString,
            NullLogger<PostgresGraphStore>.Instance);
    }

    /// <summary>
    /// Provides deterministic embeddings for contract test construction.
    /// </summary>
    private sealed class ContractEmbeddingGenerator : IEmbeddingGenerator
    {
        /// <inheritdoc />
        public Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string input, CancellationToken cancellationToken = default)
            => Task.FromResult(new ReadOnlyMemory<float>(CreateVector(input)));

        /// <inheritdoc />
        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IEnumerable<string> inputs, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(inputs.Select(input => new ReadOnlyMemory<float>(CreateVector(input))).ToArray());

        private static float[] CreateVector(string input)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(input);
            float[] vector = new float[M0001_InitialSchema.DefaultEmbeddingDimensions];
            for (int index = 0; index < vector.Length; index++)
            {
                vector[index] = bytes.Length == 0 ? 0f : bytes[index % bytes.Length] / 255f;
            }

            return vector;
        }
    }
}
