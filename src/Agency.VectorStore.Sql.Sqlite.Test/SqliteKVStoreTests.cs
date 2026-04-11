using Agency.Embeddings.Common;
using Agency.Sql.Sqlite;
using Agency.VectorStore.Sql.Sqlite;
using Moq;

namespace Agency.VectorStore.Sql.Sqlite.Test;

public sealed class SqliteKVStoreValidationTests
{
    [Fact]
    public void Constructor_NullEmbeddingGenerator_ThrowsArgumentNullException()
    {
        var runner = new SqliteRunner("Data Source=:memory:");
        Assert.Throws<ArgumentNullException>(() => new SqliteKVStore(null!, runner));
    }

    [Fact]
    public void Constructor_NullSqliteRunner_ThrowsArgumentNullException()
    {
        var generator = new Mock<IEmbeddingGenerator>().Object;
        Assert.Throws<ArgumentNullException>(() => new SqliteKVStore(generator, null!));
    }
}
