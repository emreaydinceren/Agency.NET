using Agency.Embeddings.Common;
using Agency.Sql.Sqlite;
using Moq;

namespace Agency.VectorStore.Sql.Sqlite.Test;

/// <summary>
/// Unit tests for <see cref="SqliteKVStore"/> constructor argument validation.
/// </summary>
public sealed class SqliteKVStoreValidationTests
{
    /// <summary>
    /// Verifies that the constructor throws when the embedding generator is null.
    /// </summary>
    [Fact]
    public void Constructor_NullEmbeddingGenerator_ThrowsArgumentNullException()
    {
        var runner = new SqliteRunner("Data Source=:memory:");
        Assert.Throws<ArgumentNullException>(() => new SqliteKVStore(null!, runner));
    }

    /// <summary>
    /// Verifies that the constructor throws when the SQLite runner is null.
    /// </summary>
    [Fact]
    public void Constructor_NullSqliteRunner_ThrowsArgumentNullException()
    {
        var generator = new Mock<IEmbeddingGenerator>().Object;
        Assert.Throws<ArgumentNullException>(() => new SqliteKVStore(generator, null!));
    }
}
