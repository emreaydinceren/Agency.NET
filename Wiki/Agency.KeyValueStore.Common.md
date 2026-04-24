# Agency.KeyValueStore.Common

#keyvaluestore #abstractions #search #metadata

## What It Is

`Agency.KeyValueStore.Common` defines the shared contract and DTOs for user/session-scoped key-value storage. It provides the `IKVStore` interface, query/result models, metadata JSON helpers, and a dataset conversion extension.

## Key Types

### `IKVStore`

```csharp
public interface IKVStore
{
    Task UpsertAsync<TValue>(
        string userId,
        string? sessionId,
        string key,
        TValue value,
        IDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchHit<TValue>>> SearchAsync<TValue>(
        Query query,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        string userId,
        string? sessionId,
        string key,
        CancellationToken cancellationToken = default);
}
```

### `Query`

```csharp
public record class Query(
    string UserId,
    string? SessionId,
    string? Key,
    string? Value,
    IDictionary<string, object>? MetadataFilter = null,
    int? Limit = 10,
    bool? IncludeMetadataInResults = false);
```

### `SearchHit<TValue>`

```csharp
public record class SearchHit<TValue>(
    string UserId,
    string? SessionId,
    string Key,
    TValue Value,
    Dictionary<string, object>? Metadata,
    double Distance,
    DateTimeOffset UpdatedOn)
{
    public double SimilarityPercentage => Math.Max(0, (1.0 - Distance) * 100);

    private readonly TimeSpan _recency = DateTimeOffset.UtcNow - UpdatedOn;
    public double RecencyMinutes => _recency.TotalMinutes;
    public double RecencyHours => _recency.TotalHours;
}
```

### `SearchHitExtensions`

`ToDataset<TValue>(IReadOnlyList<SearchHit<TValue>>)` converts search hits to [[Agency.Common]] `Dataset` with columns: `Key`, `Value`, `Distance`, `SimilarityPercentage`, `UpdatedOn`.

### `JsonMetadataHelpers`

`JsonMetadataHelpers` provides:

- `DeserializeMetadata(string? metadataJson)`
- `ConvertJsonElementToObject(JsonElement element)`

These helpers deserialize metadata JSON and recursively convert `JsonElement` values to CLR values (`string`, numeric primitives, `bool`, `null`, dictionaries, and lists).

## Usage

```csharp
public static async Task DemoAsync(IKVStore store, CancellationToken cancellationToken)
{
    await store.UpsertAsync(
        userId: "user-42",
        sessionId: "session-a",
        key: "note:welcome",
        value: "Welcome back",
        metadata: new Dictionary<string, object> { ["topic"] = "greeting" },
        cancellationToken: cancellationToken);

    var hits = await store.SearchAsync<string>(
        new Query(
            UserId: "user-42",
            SessionId: "session-a",
            Key: null,
            Value: "welcome",
            MetadataFilter: new Dictionary<string, object> { ["topic"] = "greeting" },
            Limit: 5,
            IncludeMetadataInResults: true),
        cancellationToken);

    bool deleted = await store.DeleteAsync(
        userId: "user-42",
        sessionId: "session-a",
        key: "note:welcome",
        cancellationToken: cancellationToken);
}
```

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.KeyValueStore.Sql.Postgre]] | Implements `IKVStore` on PostgreSQL |
| [[Agency.KeyValueStore.Sql.Sqlite]] | Implements `IKVStore` on SQLite |
| [[Agency.Common]] | Supplies `Dataset` and `IColumnMetadata` used by `SearchHitExtensions.ToDataset` |
| [[Agency.Mcp.Memory]] | Uses `IKVStore` as the persistence abstraction for memory operations |

## Observability

`Agency.KeyValueStore.Common` does not emit telemetry. Observability is implemented by concrete store projects.