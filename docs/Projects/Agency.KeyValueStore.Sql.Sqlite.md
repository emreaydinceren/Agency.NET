# Agency.KeyValueStore.Sql.Sqlite
#keyvaluestore #sqlite #observability

## What It Is

`Agency.KeyValueStore.Sql.Sqlite` is the SQLite-backed implementation of the `IKVStore` contract. It stores, searches, and deletes typed key/value entries — serialised as JSON text — in a local SQLite database, with optional JSON metadata and substring-based value filtering via the SQLite `instr` function. Metadata filtering is applied in-process after the SQL query because SQLite has no native JSONB containment operator. Results are ordered by recency (newest first).

Namespace: `Agency.KeyValueStore.Sql.Sqlite`

## API Surface

```csharp
using Agency.KeyValueStore.Common;
using Agency.Sql.Sqlite;
using Microsoft.Extensions.Logging;

namespace Agency.KeyValueStore.Sql.Sqlite;
```

| Type | Member | Signature |
|---|---|---|
| `SqliteKVStore` | Constant | `public const string ActivitySourceName = "Agency.KeyValueStore.Sql.Sqlite"` |
| `SqliteKVStore` | Constant | `public const string MeterName = "Agency.KeyValueStore.Sql.Sqlite"` |
| `SqliteKVStore` | Constructor | `public SqliteKVStore(SqliteRunner sqliteRunner, ILogger<SqliteKVStore>? logger = null)` |
| `SqliteKVStore` | Method | `public Task InitializeSchemaAsync(CancellationToken cancellationToken = default)` |
| `SqliteKVStore` | Method | `public Task UpsertAsync<TValue>(string userId, string? sessionId, string key, TValue value, IDictionary<string, object>? metadata = null, CancellationToken cancellationToken = default)` |
| `SqliteKVStore` | Method | `public Task<IReadOnlyList<SearchHit<TValue>>> SearchAsync<TValue>(Query query, CancellationToken cancellationToken = default)` |
| `SqliteKVStore` | Method | `public Task<bool> DeleteAsync(string userId, string? sessionId, string key, CancellationToken cancellationToken = default)` |
| `SqliteKVStore` | Method | `public Task<IReadOnlyList<SearchHit>> GetMetadataAsync(string userId, string? sessionId, CancellationToken cancellationToken = default)` |

## Dependencies

- `Agency.KeyValueStore.Common`
- `Agency.Sql.Sqlite`

## Related

- [[Agency.KeyValueStore.Common]]
- [[Agency.Sql.Sqlite]]
