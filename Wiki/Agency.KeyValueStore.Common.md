# Agency.KeyValueStore.Common

#keyvaluestore #abstractions #search #metadata

## What It Is

Agency.KeyValueStore.Common is the shared contract layer that defines the `IKVStore` interface, query/result models, metadata JSON helpers, and dataset conversion extensions used by all key-value store implementations.

**Namespace:** `Agency.KeyValueStore.Common`

## API Surface

### `IKVStore`

```csharp
// File: src/KeyValueStore/Agency.KeyValueStore.Common/IKVStore.cs
namespace Agency.KeyValueStore.Common;

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

    Task<IReadOnlyList<SearchHit>> GetMetadataAsync(
        string userId,
        string? sessionId,
        CancellationToken cancellationToken = default);
}
```

`sessionId` of `null` is treated as the user-global scope (stored as `"*"` by implementations). `GetMetadataAsync` returns lightweight, non-generic `SearchHit` records containing only keys and metadata — no deserialized value payload.

### `Query`

```csharp
// File: src/KeyValueStore/Agency.KeyValueStore.Common/Query.cs
namespace Agency.KeyValueStore.Common;

public record class Query(
    string UserId,
    string? SessionId,
    string? Key,
    string? Value,
    IDictionary<string, object>? MetadataFilter = null,
    int? Limit = 10,
    bool? IncludeMetadataInResults = false);
```

`SessionId = null` returns results across all sessions for the user. `Value` is a substring filter (case-insensitive). `MetadataFilter` is an exact-match dictionary applied on top of the key/value filter.

### `SearchHit<TValue>` and `SearchHit`

```csharp
// File: src/KeyValueStore/Agency.KeyValueStore.Common/SearchHit.cs
namespace Agency.KeyValueStore.Common;

public record class SearchHit(
    string? SessionId,
    string Key,
    Dictionary<string, object>? Metadata,
    DateTimeOffset UpdatedOn)
{
    public double RecencyMinutes { get; }
    public double RecencyHours  { get; }
}

public record class SearchHit<TValue>(
    string? SessionId,
    string Key,
    TValue Value,
    Dictionary<string, object>? Metadata,
    DateTimeOffset UpdatedOn) : SearchHit(SessionId, Key, Metadata, UpdatedOn);
```

`SearchHit` is the non-generic base returned by `GetMetadataAsync`. `SearchHit<TValue>` extends it with the deserialized `Value` and is returned by `SearchAsync<TValue>`. Both expose `RecencyMinutes` and `RecencyHours` computed from `UpdatedOn`.

### `SearchHitExtensions`

```csharp
// File: src/KeyValueStore/Agency.KeyValueStore.Common/SearchHitExtensions.cs
using Agency.Common;
using Agency.KeyValueStore.Common;

public static class SearchHitExtensions
{
    public static Dataset ToDataset<TValue>(this IReadOnlyList<SearchHit<TValue>> hits);
}
```

Converts a list of `SearchHit<TValue>` to a [[Agency.Common]] `Dataset` with columns `Key`, `Value`, and `UpdatedOn`.

### `JsonMetadataHelpers`

```csharp
// File: src/KeyValueStore/Agency.KeyValueStore.Common/JsonMetadataHelpers.cs
using System.Text.Json;
using Agency.KeyValueStore.Common;

public static class JsonMetadataHelpers
{
    public static Dictionary<string, object>? DeserializeMetadata(string? metadataJson);
    public static object ConvertJsonElementToObject(JsonElement element);
}
```

`DeserializeMetadata` converts a raw JSON string into a `Dictionary<string, object>` by recursively replacing `JsonElement` nodes with CLR primitives (`string`, `long`, `double`, `decimal`, `bool`, `null`, `Dictionary<string, object>`, `List<object>`). Used by SQL implementations to hydrate metadata columns.

## How It Works

1. A caller acquires an `IKVStore` implementation (e.g. from [[Agency.KeyValueStore.Sql.Postgre]] or [[Agency.KeyValueStore.Sql.Sqlite]]).
2. **Write** — `UpsertAsync` stores a typed value plus optional metadata under `(userId, sessionId, key)`.
3. **Read** — `SearchAsync` accepts a `Query` and returns matching `SearchHit<TValue>` records. The implementation applies key, value, and metadata filters and respects the `Limit`.
4. **Browse** — `GetMetadataAsync` returns lightweight `SearchHit` records (no value payload) for all entries belonging to a user/session, useful for listing available keys before fetching values.
5. **Delete** — `DeleteAsync` removes a single entry and returns `true` if an entry existed.
6. Implementations use `JsonMetadataHelpers.DeserializeMetadata` to round-trip the JSON metadata column back to a dictionary after querying.

## How It Relates to Other Projects

- **[[Agency.Common]]** — `SearchHitExtensions.ToDataset` produces a `Dataset` defined in `Agency.Common`, bridging key-value results into the RAG formatting pipeline.
- **[[Agency.KeyValueStore.Sql.Postgre]]** — PostgreSQL implementation of `IKVStore`; uses `JsonMetadataHelpers` for metadata serialization.
- **[[Agency.KeyValueStore.Sql.Sqlite]]** — SQLite implementation of `IKVStore`; same pattern.
- **[[Agency.Agentic]]** — Agent tool layers depend on `IKVStore` for persistent session memory.

## Design Notes

- `sessionId = null` is a first-class concept for user-global entries rather than requiring callers to pass a sentinel string; the sentinel `"*"` is an implementation detail hidden behind the interface.
- The non-generic `SearchHit` base was introduced so `GetMetadataAsync` can return lightweight key-listing results without forcing callers to specify a value type they do not need.
- `Query.IncludeMetadataInResults` is a hint to implementations to avoid deserializing the metadata column when the caller does not need it, keeping read paths efficient.
- No infrastructure dependencies are declared here — the project references only `Agency.Common`, keeping the abstraction layer portable across all store backends.