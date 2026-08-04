# Agency.Sql.Sqlite

## What It Is

`Agency.Sql.Sqlite` is the SQLite database adapter for the Agency toolkit. It provides `SqliteRunner`, an async SQL runner that wraps `SqlRunnerBase` with SQLite-specific connection and command building, and `SQLQueryEmbedder`, which rewrites `vectorize('<text>')` placeholders in SQL strings into quoted vector literals using `IEmbeddingGenerator`.

Namespace: `Agency.Sql.Sqlite`

## API Surface

```csharp
using Agency.Sql.Sqlite;
```

| Type | Member | Signature |
|---|---|---|
| `SqliteRunner` | Constant | `public const string ActivitySourceName = "Agency.Sql.Sqlite"` |
| `SqliteRunner` | Constant | `public const string MeterName = "Agency.Sql.Sqlite"` |
| `SqliteRunner` | Constructor | `public SqliteRunner(string connectionString, Action<SqliteConnection>? onConnectionOpen = null, ILogger<SqliteRunner>? logger = null)` |
| `SqliteRunner` | Method | `public Task<int> ExecuteAsync(string sql, Dictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)` |
| `SqliteRunner` | Method | `public Task<Dataset> QueryAsync(string sql, Dictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)` |
| `SqliteRunner` | Method | `public Task<List<TResult>> QueryAsync<TResult>(string sql, Func<DbDataReader, Task<TResult>> predicate, Dictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)` |
| `SQLQueryEmbedder` | Constructor | `public SQLQueryEmbedder(IEmbeddingGenerator embeddingGenerator)` |
| `SQLQueryEmbedder` | Method | `public Task<string> EmbedVectorsInQueryAsync(string sqlQuery, CancellationToken cancellationToken = default)` |

## Dependencies

- `Agency.Common`
- `Agency.Embeddings.Common`
- `Agency.Sql.Common`
- `Microsoft.Data.Sqlite`

## Related

- [Agency.Sql.Common](Agency.Sql.Common.md)
- [Agency.Embeddings.Common](Agency.Embeddings.Common.md)
- [Agency.Common](Agency.Common.md)
- [Agency.VectorStore.Sql.Sqlite](Agency.VectorStore.Sql.Sqlite.md)
- [Agency.KeyValueStore.Sql.Sqlite](Agency.KeyValueStore.Sql.Sqlite.md)
