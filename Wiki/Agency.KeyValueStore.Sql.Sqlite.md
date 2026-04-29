# Agency.KeyValueStore.Sql.Sqlite

#keyvaluestore #sqlite #observability

## What It Is

`Agency.KeyValueStore.Sql.Sqlite` provides a SQLite implementation of [[Agency.KeyValueStore.Common]] `IKVStore`.

The project currently exposes one concrete type:

- `SqliteKVStore`

`SqliteKVStore` stores `value` as JSON text (`TEXT`) and optional `metadata` as JSON text (`TEXT`) in a `kv_store` table keyed by `(user_id, session_id, key)`. It applies value filtering in SQL via `instr(value, @v) > 0`, applies metadata filtering in-process (because SQLite has no native JSONB containment), and returns results ordered by `updated_on DESC`.

It depends on [[Agency.Sql.Sqlite]] for SQL execution, [[Agency.KeyValueStore.Common]] for `IKVStore`, `Query`, and `SearchHit<TValue>`, plus `Microsoft.Data.Sqlite` and `Microsoft.Extensions.Logging.Abstractions`.

## Key Types

### `SqliteKVStore`

```csharp
public sealed class SqliteKVStore : IKVStore
{
    public const string ActivitySourceName = "Agency.KeyValueStore.Sql.Sqlite";
    public const string MeterName = "Agency.KeyValueStore.Sql.Sqlite";

    public SqliteKVStore(
        SqliteRunner sqliteRunner,
        ILogger<SqliteKVStore>? logger = null);

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

`InitializeSchemaAsync` creates `kv_store` if needed:

- `user_id TEXT NOT NULL`
- `session_id TEXT NOT NULL`
- `key TEXT NOT NULL`
- `value TEXT NOT NULL`
- `metadata TEXT`
- `updated_on TEXT DEFAULT (datetime('now'))`
- `PRIMARY KEY (user_id, session_id, key)`

## Usage

```csharp
var runner = new SqliteRunner("Data Source=kvstore.db");
var store = new SqliteKVStore(runner, logger);

await store.InitializeSchemaAsync(cancellationToken);

await store.UpsertAsync(
    userId: "user-42",
    sessionId: "session-a",
    key: "profile",
    value: "Emre",
    metadata: new Dictionary<string, object> { ["source"] = "seed" },
    cancellationToken: cancellationToken);

var hits = await store.SearchAsync<string>(
    new Query(
        UserId: "user-42",
        SessionId: "session-a",
        Key: null,
        Value: "Em",
        MetadataFilter: new Dictionary<string, object> { ["source"] = "seed" },
        Limit: 10,
        IncludeMetadataInResults: true),
    cancellationToken);

bool deleted = await store.DeleteAsync(
    userId: "user-42",
    sessionId: "session-a",
    key: "profile",
    cancellationToken: cancellationToken);

var metadata = await store.GetMetadataAsync(
    userId: "user-42",
    sessionId: "session-a",
    cancellationToken: cancellationToken);
```

## Configuration

This project does not define an options class. `SqliteKVStore` receives a configured `SqliteRunner`, so connection settings come from the runner's SQLite connection string.

`sessionId` values of `null` are stored as `"*"` for user-global entries.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.KeyValueStore.Common]] | Provides `IKVStore`, `Query`, and `SearchHit<TValue>` contracts implemented and returned by `SqliteKVStore` |
| [[Agency.Sql.Sqlite]] | Supplies `SqliteRunner`, which executes all SQL used by `SqliteKVStore` |

## Observability

`SqliteKVStore` emits OpenTelemetry traces and metrics:

- `ActivitySource` name: `Agency.KeyValueStore.Sql.Sqlite`
- `Meter` name: `Agency.KeyValueStore.Sql.Sqlite`
- Counter: `kvstore.operations` (operation/status tags)
- Histogram: `kvstore.duration` in milliseconds

Operations instrumented:

- `kvstore.initialize`
- `kvstore.search`
- `kvstore.upsert`
- `kvstore.delete`
- `kvstore.getMetadata`
