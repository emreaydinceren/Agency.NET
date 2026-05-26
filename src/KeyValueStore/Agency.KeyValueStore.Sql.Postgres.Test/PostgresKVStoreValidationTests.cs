
using Agency.Sql.Postgres;
using Microsoft.Extensions.Logging;
using Moq;

namespace Agency.KeyValueStore.Sql.Postgres.Test;
/// <summary>
/// Validation tests for <see cref="PostgresKVStore"/> constructor and parameter validation.
/// These tests verify input validation without requiring a database.
/// Run with: dotnet test --filter "Category!=Functional"
/// </summary>
public sealed class PostgresKVStoreValidationTests
{
    // ── Constructor validation ──────────────────────────────────────────────

    /// <summary>
    /// Verifies that constructor rejects null SQL runner.
    /// </summary>
    [Fact]
    public void Constructor_NullPostgreSqlRunner_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new PostgresKVStore(null!, new Mock<ILogger<PostgresKVStore>>().Object));
    }
}
