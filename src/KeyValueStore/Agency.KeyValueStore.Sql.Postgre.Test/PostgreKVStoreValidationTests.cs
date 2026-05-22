namespace Agency.KeyValueStore.Sql.Postgre.Test;

using Agency.Sql.Postgres;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// Validation tests for <see cref="PostgreKVStore"/> constructor and parameter validation.
/// These tests verify input validation without requiring a database.
/// Run with: dotnet test --filter "Category!=Functional"
/// </summary>
public sealed class PostgreKVStoreValidationTests
{
    // ── Constructor validation ──────────────────────────────────────────────

    /// <summary>
    /// Verifies that constructor rejects null SQL runner.
    /// </summary>
    [Fact]
    public void Constructor_NullPostgreSqlRunner_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new PostgreKVStore(null!, new Mock<ILogger<PostgreKVStore>>().Object));
    }
}
