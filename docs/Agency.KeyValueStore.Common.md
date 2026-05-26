# Agency.KeyValueStore.Common
#keyvaluestore #abstractions #search #metadata

## What It Is

`Agency.KeyValueStore.Common` is the shared contract layer for the key-value store subsystem. It defines the `IKVStore` interface, the `Query` input model, the `SearchHit` / `SearchHit<TValue>` result records, JSON metadata round-trip helpers, and a `Dataset` conversion extension. All concrete store implementations depend on this project; no infrastructure dependencies are introduced here.

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

`sessionId` of `null` is treated as the user-global scope (stored as `"*"` by implementations). `GetMetadataAsync` returns lightweight, non-generic `SearchHit` records containing only keys and metadata — no deserialized value payload. `DeleteAsync` returns `true` when an entry was removed, `false` when no matching entry existed.

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

`SessionId = null` returns results across all sessions for the user. `Key` and `Value` are optional filters — `Value` is a substring match (case-insensitive). `MetadataFilter` is an exact-match dictionary applied on top of the key/value filter. `Limit` defaults to `10`; passing `null` removes the cap. `IncludeMetadataInResults = false` is a hint to implementations to skip deserializing the metadata column when the caller does not need it.

### `SearchHit` and `SearchHit<TValue>`

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
    public double RecencyHours { get; }
}

public record class SearchHit<TValue>(
    string? SessionId,
    string Key,
    TValue Value,
    Dictionary<string, object>? Metadata,
    DateTimeOffset UpdatedOn) : SearchHit(SessionId, Key, Metadata, UpdatedOn);
```

`SearchHit` is the non-generic base returned by `GetMetadataAsync`. `SearchHit<TValue>` extends it with the deserialized `Value` and is returned by `SearchAsync<TValue>`. Both expose `RecencyMinutes` and `RecencyHours`, computed as elapsed time since `UpdatedOn` at the moment the record is constructed.

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

Converts a list of `SearchHit<TValue>` to an [[Agency.Common]] `Dataset` with three columns: `Key`, `Value`, and `UpdatedOn`. This bridges key-value search results into the RAG formatting pipeline.

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

`DeserializeMetadata` converts a raw JSON string into a `Dictionary<string, object>` by recursively replacing `JsonElement` nodes with CLR primitives. `ConvertJsonElementToObject` handles `string`, `long`, `double`, `decimal`, `bool`, `null`, nested `Dictionary<string, object>`, and `List<object>`. Used by SQL implementations to hydrate the metadata column after a query.

## How It Works

A caller acquires an `IKVStore` implementation (e.g. from [[Agency.KeyValueStore.Sql.Postgres]] or [[Agency.KeyValueStore.Sql.Sqlite]]) and interacts through the four operations:

```csharp
using Agency.KeyValueStore.Common;

// Write — upsert a typed value with optional metadata
await store.UpsertAsync(
    userId: "user-1",
    sessionId: "session-42",
    key: "preferences",
    value: new UserPreferences { Theme = "dark" },
    metadata: new Dictionary<string, object> { ["source"] = "onboarding" });

// Read — search with filters and a result cap
var hits = await store.SearchAsync<UserPreferences>(new Query(
    UserId: "user-1",
    SessionId: "session-42",
    Key: "preferences",
    Value: null,
    Limit: 5,
    IncludeMetadataInResults: true));

// Browse — list available keys without deserializing values
var keys = await store.GetMetadataAsync("user-1", sessionId: null);

// Delete — remove a specific entry
bool removed = await store.DeleteAsync("user-1", "session-42", "preferences");

// Convert search results to a Dataset for RAG formatting
using Agency.Common;
Dataset dataset = hits.ToDataset();
```

`sessionId = null` in `UpsertAsync` / `DeleteAsync` / `GetMetadataAsync` targets the user-global scope. Implementations store this as `"*"` internally; callers never need to use that sentinel directly.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Common]] | `SearchHitExtensions.ToDataset` produces a `Dataset` defined in `Agency.Common`, bridging key-value results into the RAG formatting pipeline |
| [[Agency.KeyValueStore.Sql.Postgres]] | PostgreSQL implementation of `IKVStore`; uses `JsonMetadataHelpers` to hydrate the metadata column |
| [[Agency.KeyValueStore.Sql.Sqlite]] | SQLite implementation of `IKVStore`; follows the same pattern |
| [[Agency.Agentic]] | Agent tool layers depend on `IKVStore` for persistent session memory |

## Design Notes

- `sessionId = null` is a first-class concept for user-global entries rather than requiring callers to pass a sentinel string. The sentinel `"*"` is an implementation detail hidden behind the interface, preventing leakage of storage conventions into the calling layer.
- The non-generic `SearchHit` base was introduced so `GetMetadataAsync` can return lightweight key-listing results without forcing callers to specify a value type they do not need — avoiding unnecessary deserialization on metadata-only reads.
- `Query.IncludeMetadataInResults` is an opt-in hint: metadata deserialization is skipped by default (`false`), keeping read paths efficient when callers only need keys and values.
- No infrastructure packages are referenced here — the project depends only on `Agency.Common`, making the abstraction portable across all store backends and testable with simple in-memory fakes.
