# Agency.VectorStore.Sql.Sqlite
#vectorstore #sqlite #cosine #udf #observability

## What It Is

Agency.VectorStore.Sql.Sqlite is the SQLite-backed `IVectorStore` implementation that stores JSON-serialized values and their embeddings in a `semantic_kv_store` table and computes cosine similarity via a pure-managed scalar UDF (`vec_distance_cosine`) registered on each opened connection. Metadata filtering is applied in-process after the SQL query because SQLite has no native JSONB containment operator.

Namespace: `Agency.VectorStore.Sql.Sqlite`

## API Surface

```csharp
using Agency.Embeddings.Common;
using Agency.Sql.Sqlite;
using Agency.VectorStore.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Agency.VectorStore.Sql.Sqlite;
```

| Type | Member | Signature |
|---|---|---|
| `SqliteKVStore` | Class | `public sealed class SqliteKVStore : IVectorStore` |
| `SqliteKVStore` | Constant | `public const string ActivitySourceName = "Agency.VectorStore.Sql.Sqlite"` |
| `SqliteKVStore` | Constant | `public const string MeterName = "Agency.VectorStore.Sql.Sqlite"` |
| `SqliteKVStore` | Constructor | `public SqliteKVStore(IEmbeddingGenerator embeddingGenerator, SqliteRunner sqliteRunner, ILogger<SqliteKVStore>? logger = null)` |
| `SqliteKVStore` | Static method | `public static void RegisterVectorFunctions(SqliteConnection connection)` |
| `SqliteKVStore` | Method | `public Task InitializeSchemaAsync(int dimensions = 1536, CancellationToken cancellationToken = default)` |
| `SqliteKVStore` | Method | `public Task<IReadOnlyList<SearchHit<TValue>>> SearchAsync<TValue>(Query query, CancellationToken cancellationToken = default)` |
| `SqliteKVStore` | Method | `public Task UpsertAsync<TValue>(string userId, string? sessionId, string key, TValue value, IDictionary<string, object>? metadata = null, CancellationToken cancellationToken = default)` |
| `SqliteKVStore` | Method | `public Task<bool> DeleteAsync(string userId, string? sessionId, string key, CancellationToken cancellationToken = default)` |

## Dependencies

- [[Agency.Embeddings.Common]] — `IEmbeddingGenerator`
- [[Agency.Sql.Sqlite]] — `SqliteRunner`
- [[Agency.VectorStore.Common]] — `IVectorStore`, `Query`, `SearchHit<TValue>`, `JsonMetadataHelpers`

## Related

- [[Agency.VectorStore.Common]]
- [[Agency.Embeddings.Common]]
- [[Agency.Sql.Sqlite]]
- [[Agency.VectorStore.Sql.Postgres]]
