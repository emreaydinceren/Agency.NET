using Agency.Embeddings.Common;
using Microsoft.Data.Sqlite;
using Moq;

namespace Agency.Memory.Sql.Sqlite.Test;

/// <summary>
/// Shared helpers for SQLite memory test classes.
/// </summary>
internal static class TestHelpers
{
    /// <summary>
    /// Builds a unique in-memory SQLite connection string using shared cache so the DB survives
    /// across multiple <see cref="SqliteConnection"/> instances.
    /// </summary>
    internal static string BuildConnectionString()
    {
        string dbName = $"mem_{Guid.NewGuid():N}";
        return $"Data Source={dbName};Mode=Memory;Cache=Shared";
    }

    /// <summary>
    /// Opens and returns a keep-alive connection for the given connection string.
    /// The caller must hold this connection open for as long as the in-memory DB must survive.
    /// </summary>
    internal static SqliteConnection OpenKeepAlive(string connectionString)
    {
        var conn = new SqliteConnection(connectionString);
        conn.Open();
        return conn;
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