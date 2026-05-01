# Agency.KeyValueStore.Sql.Sqlite

#keyvaluestore #sqlite #observability

## What It Is

Agency.KeyValueStore.Sql.Sqlite is the SQLite-backed implementation of the `IKVStore` contract that stores, searches, and deletes typed key/value entries with optional JSON metadata in a local SQLite database.

**Namespace:** `Agency.KeyValueStore.Sql.Sqlite`

## API Surface

```csharp
// File: src/KeyValueStore/Agency.KeyValueStore.Sql.Sqlite/SqliteKVStore.cs
using Agency.KeyValueStore.Common;
using Agency.Sql.Sqlite;
using Microsoft.Extensions.Logging;

public sealed class SqliteKVStore : IKVStore
{
    public const string ActivitySourceName = "Agency.KeyValueStore.Sql.Sqlite";
    public const string MeterName          = "Agency.KeyValueStore.Sql.Sqlite";

    public SqliteKVStore(
        SqliteRunner sqliteRunner,
        ILogger<SqliteKVStore>? logger = null);

    /// <summary>Creates the kv_store table and key index if they do not already exist.</summary>
    public Task InitializeSchemaAsync(CancellationToken cancellationToken = default);

    public Task UpsertAsync<TValue>(
        string userId,
        string? sessionId,
        string key,
        TValue value,
        IDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default);

    public Task<IReadOnlyList<SearchHit<TValue>>> SearchAsync<TValue>(
        Query query,
        CancellationToken cancellationToken = default);

    public Task<bool> DeleteAsync(
        string userId,
        string? sessionId,
        string key,
        CancellationToken cancellationToken = default);

    public Task<IReadOnlyList<SearchHit>> GetMetadataAsync(
        string userId,
        string? sessionId,
        CancellationToken cancellationToken = default);
}
```

## How It Works

1. **Schema bootstrap** — `InitializeSchemaAsync` issues a `CREATE TABLE IF NOT EXISTS kv_store` DDL via `SqliteRunner`. The table has columns `user_id`, `session_id`, `key`, `value` (JSON text), `metadata` (JSON text, nullable), and `updated_on` (text, default `datetime('now')`). The primary key is `(user_id, session_id, key)`.
2. **Session resolution** — a `null` `sessionId` is stored as the sentinel value `"*"`, representing a user-global entry that is not tied to any specific session.
3. **Upsert** — serializes `value` to JSON and `metadata` to JSON, then issues an `INSERT … ON CONFLICT … DO UPDATE` that also refreshes `updated_on`.
4. **Search** — builds a parameterised SQL query with optional `session_id`, `key`, and `instr(value, @v) > 0` clauses, ordered by `updated_on DESC`. When a `MetadataFilter` is present, the SQL `LIMIT` is removed and post-query C# filtering applies array-containment or scalar-equality checks before the final limit is enforced.
5. **Delete** — issues a targeted `DELETE` by `(user_id, session_id, key)` and returns `true` if a row was removed.
6. **Metadata-only read** — `GetMetadataAsync` selects `session_id`, `key`, `metadata`, and `updated_on` without fetching the value column, for lightweight enumeration.

## Observability

`SqliteKVStore` instruments every operation with OpenTelemetry traces and metrics.

- **ActivitySource name:** `Agency.KeyValueStore.Sql.Sqlite`
- **Meter name:** `Agency.KeyValueStore.Sql.Sqlite`
- **Counter:** `kvstore.operations` — tags `operation` and `status` (`success` / `error`)
- **Histogram:** `kvstore.duration` (milliseconds) — tag `operation`

Operations covered: `kvstore.initialize`, `kvstore.upsert`, `kvstore.search`, `kvstore.delete`, `kvstore.getMetadata`.

Each activity span sets `kvstore.operation`, captures result counts and key metadata as span tags, and records a structured exception event on failure.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.KeyValueStore.Common]] | Defines `IKVStore`, `Query`, `SearchHit<TValue>`, and `SearchHit` — the contracts that `SqliteKVStore` implements and returns |
| [[Agency.Sql.Sqlite]] | Provides `SqliteRunner`, which `SqliteKVStore` delegates all SQL execution to |

## Design Notes

- Value filtering uses SQLite's `instr()` function for in-database substring matching, keeping result sets small before they reach C#; metadata filtering is intentionally deferred to C# because SQLite has no native JSONB containment operator.
- The `"*"` sentinel for `null` session IDs allows a single schema column to represent both session-scoped and user-global entries without a nullable primary key, which SQLite does not support.
