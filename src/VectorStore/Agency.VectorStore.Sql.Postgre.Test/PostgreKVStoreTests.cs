using Agency.Embeddings.Common;
using Agency.Sql.Postgre;
using Microsoft.Extensions.Logging;
using Moq;

namespace Agency.VectorStore.Sql.Postgre.Test;

/// <summary>
/// Validation tests for <see cref="PostgreKVStore"/> constructor and parameter validation.
/// These tests verify input validation without requiring a database.
/// Run with: dotnet test --filter "Category!=Functional"
/// </summary>
public sealed class PostgreKVStoreValidationTests
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
        var mockLogger = new Mock<ILogger<PostgreKVStore>>();

        Assert.Throws<ArgumentNullException>(() =>
            new PostgreKVStore(null!, runner, mockLogger.Object));
    }

    /// <summary>
    /// Verifies that constructor rejects null SQL runner.
    /// </summary>
    [Fact]
    public void Constructor_NullPostgreSqlRunner_ThrowsArgumentNullException()
    {
        var mockGenerator = new Mock<IEmbeddingGenerator>();

        Assert.Throws<ArgumentNullException>(() =>
            new PostgreKVStore(mockGenerator.Object, null!, new Mock<ILogger<PostgreKVStore>>().Object));
    }
}
