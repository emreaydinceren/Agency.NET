# Agency.KeyValueStore.Sql.Postgre

#keyvaluestore #postgresql #jsonb #observability

## What It Is

`Agency.KeyValueStore.Sql.Postgre` provides a PostgreSQL implementation of [[Agency.KeyValueStore.Common]] `IKVStore`.

The project currently exposes one concrete type:

- `PostgreKVStore`

It stores values and metadata as `JSONB`, scopes records by `user_id` and `session_id`, and orders search results by `updated_on DESC`. Null session IDs are resolved to the sentinel `"*"` for user-global entries.

It depends on [[Agency.Sql.Postgre]] for SQL execution and `Npgsql` parameter types used for JSONB values and filters.

## Key Types

### `PostgreKVStore`

```csharp
public class PostgreKVStore : IKVStore
{
    public const string ActivitySourceName = "Agency.KeyValueStore.Sql.Postgre";
    public const string MeterName = "Agency.KeyValueStore.Sql.Postgre";

    public PostgreKVStore(PostgreSqlRunner postgreSqlRunner, ILogger<PostgreKVStore> logger);

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

`InitializeSchemaAsync` creates the `kv_store` table when needed, adds a GIN index on `metadata`, and drops an old incompatible `kv_store` shape that does not contain `user_id`.

## Usage

```csharp
var store = new PostgreKVStore(postgreSqlRunner, logger);

await store.InitializeSchemaAsync(cancellationToken);

await store.UpsertAsync(
    userId: "user-42",
    sessionId: "session-a",
    key: "profile",
    value: new { Name = "Emre", Role = "Admin" },
    metadata: new Dictionary<string, object> { ["source"] = "seed" },
    cancellationToken: cancellationToken);

var hits = await store.SearchAsync<object>(
    new Query(
        UserId: "user-42",
        SessionId: "session-a",
        Key: null,
        Value: "Emre",
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

This project does not define an options class. `PostgreKVStore` receives a configured `PostgreSqlRunner`, so connection settings come from the runner's connection string setup.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.KeyValueStore.Common]] | Provides `IKVStore`, `Query`, and `SearchHit<TValue>` contracts implemented and returned by `PostgreKVStore` |
| [[Agency.Sql.Postgre]] | Supplies `PostgreSqlRunner`, which executes all SQL used by `PostgreKVStore` |

## Observability

`PostgreKVStore` emits OpenTelemetry traces and metrics:

- `ActivitySource` name: `Agency.KeyValueStore.Sql.Postgre`
- `Meter` name: `Agency.KeyValueStore.Sql.Postgre`
- Counter: `kvstore.operations` (operation/status tags)
- Histogram: `kvstore.duration` in milliseconds

Operations instrumented:

- `kvstore.initialize`
- `kvstore.search`
- `kvstore.upsert`
- `kvstore.delete`
- `kvstore.getMetadata`
