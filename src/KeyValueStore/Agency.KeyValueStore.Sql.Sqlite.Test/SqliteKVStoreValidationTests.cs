namespace Agency.KeyValueStore.Sql.Sqlite.Test;

using Agency.Sql.Sqlite;

/// <summary>
/// Unit tests that validate constructor argument checks for <see cref="SqliteKVStore"/>.
/// </summary>
public sealed class SqliteKVStoreValidationTests
{
    /// <summary>
    /// Verifies that the constructor throws <see cref="ArgumentNullException"/> when
    /// <paramref name="sqliteRunner"/> is <see langword="null"/>.
    /// </summary>
    [Fact]
    public void Constructor_NullSqliteRunner_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new SqliteKVStore(null!));
    }
}
