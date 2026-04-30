using Agency.GraphRAG.Code.Sqlite;
using Agency.GraphRAG.Code.Storage;
using Agency.GraphRAG.Code.Test.Storage;
using Agency.Sql.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agency.GraphRAG.Code.Sqlite.Test;

/// <summary>
/// Runs the shared graph-store contract against the SQLite implementation.
/// </summary>
public sealed class SqliteGraphStoreContractTests : IGraphStoreContractTests
{
    /// <inheritdoc />
    protected override IGraphStore CreateGraphStore()
        => new SqliteGraphStore(
            new SqliteRunner("Data Source=:memory:", onConnectionOpen: static _ => { }),
            new FakeEmbeddingGenerator(),
            NullLogger<SqliteGraphStore>.Instance);
}
