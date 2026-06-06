using Agency.Embeddings.Common;
using Agency.Sql.Postgres;
using Microsoft.Extensions.Logging;
using Moq;

namespace Agency.VectorStore.Sql.Postgres.Test;

/// <summary>
/// Validation tests for <see cref="PostgresKVStore"/> constructor and parameter validation.
/// These tests verify input validation without requiring a database.
/// Run with: dotnet test --filter "Category!=Functional"
/// </summary>
public sealed class PostgresKVStoreValidationTests
{
    // ── Constructor validation ──────────────────────────────────────────────

    /// <summary>
    /// Verifies that constructor rejects null embedding generator.
    /// </summary>
    [Fact]
    public void Constructor_NullEmbeddingGenerator_ThrowsArgumentNullException()
    {
        var connectionString = "Host=localhost;Port=5432;Username=test;Password=test;Database=test";
        var runner = new PostgreSqlRunner(connectionString);
        var mockLogger = new Mock<ILogger<PostgresKVStore>>();

        Assert.Throws<ArgumentNullException>(() =>
            new PostgresKVStore(null!, runner, mockLogger.Object));
    }

    /// <summary>
    /// Verifies that constructor rejects null SQL runner.
    /// </summary>
    [Fact]
    public void Constructor_NullPostgreSqlRunner_ThrowsArgumentNullException()
    {
        var mockGenerator = new Mock<IEmbeddingGenerator>();

        Assert.Throws<ArgumentNullException>(() =>
            new PostgresKVStore(mockGenerator.Object, null!, new Mock<ILogger<PostgresKVStore>>().Object));
    }
}
