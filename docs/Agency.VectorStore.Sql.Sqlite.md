# Agency.VectorStore.Sql.Sqlite

#vectorstore #sqlite #cosine #udf #observability

**Namespace:** `Agency.VectorStore.Sql.Sqlite`

## What It Is

Agency.VectorStore.Sql.Sqlite is the SQLite-backed `IVectorStore` implementation that stores JSON-serialized values and their embeddings in a `semantic_kv_store` table and computes cosine similarity via a pure-managed scalar UDF (`vec_distance_cosine`) registered on each opened connection.

## Prerequisites

- `Microsoft.Data.Sqlite` NuGet package (no native sqlite-vec extension required — cosine distance is implemented entirely in managed C# and registered as a connection-level scalar UDF).
- A `SqliteRunner` instance from [[Agency.Sql.Sqlite]] constructed with `SqliteKVStore.RegisterVectorFunctions` as the `onConnectionOpen` callback.
- An `IEmbeddingGenerator` from [[Agency.Embeddings.Common]].

## API Surface

```csharp
// File: src/VectorStore/Agency.VectorStore.Sql.Sqlite/SqliteKVStore.cs
using Agency.Embeddings.Common;
using Agency.Sql.Sqlite;
using Agency.VectorStore.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Agency.VectorStore.Sql.Sqlite;

public sealed class SqliteKVStore : IVectorStore
{
    // Telemetry source names
    public const string ActivitySourceName = "Agency.VectorStore.Sql.Sqlite";
    public const string MeterName          = "Agency.VectorStore.Sql.Sqlite";

    // Constructor
    public SqliteKVStore(
        IEmbeddingGenerator embeddingGenerator,
        SqliteRunner sqliteRunner,
        ILogger<SqliteKVStore>? logger = null);

    // Register the vec_distance_cosine UDF on a connection.
    // Pass as onConnectionOpen when constructing SqliteRunner.
    public static void RegisterVectorFunctions(SqliteConnection connection);

    // Creates the semantic_kv_store table if it does not exist.
    public Task InitializeSchemaAsync(int dimensions = 1536, CancellationToken cancellationToken = default);

    // IVectorStore
    public Task UpsertAsync<TValue>(
        string userId, string? sessionId, string key, TValue value,
        IDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default);

    public Task<IReadOnlyList<SearchHit<TValue>>> SearchAsync<TValue>(
        Query query, CancellationToken cancellationToken = default);

    public Task<bool> DeleteAsync(
        string userId, string? sessionId, string key,
        CancellationToken cancellationToken = default);
}
```

## How It Works

Wire up the runner so every connection receives the UDF, then call `InitializeSchemaAsync` once before using the store:

```csharp
using Agency.Embeddings.Common;
using Agency.Sql.Sqlite;
using Agency.VectorStore.Common;
using Agency.VectorStore.Sql.Sqlite;

var runner = new SqliteRunner(
    "Data Source=agency.db",
    onConnectionOpen: SqliteKVStore.RegisterVectorFunctions);

var store = new SqliteKVStore(embeddingGenerator, runner);
await store.InitializeSchemaAsync();

// Store an entry
await store.UpsertAsync(
    userId: "user-1",
    sessionId: "session-1",
    key: "note:1",
    value: new { Text = "Hello world" },
    metadata: new Dictionary<string, object> { ["tags"] = new[] { "welcome", "vector" } });

// Semantic search with metadata filter
var hits = await store.SearchAsync<Dictionary<string, object>>(new Query(
    UserId: "user-1",
    SessionId: "session-1",
    Key: null,
    Value: "greeting",
    MetadataFilter: new Dictionary<string, object> { ["tags"] = new[] { "vector" } },
    Limit: 5));
```

### Schema

`InitializeSchemaAsync` creates the following table:

```sql
CREATE TABLE IF NOT EXISTS semantic_kv_store (
    user_id    TEXT NOT NULL,
    session_id TEXT NOT NULL,
    key        TEXT NOT NULL,
    value      TEXT NOT NULL,
    embedding  TEXT NOT NULL,
    metadata   TEXT,
    updated_on TEXT DEFAULT (datetime('now')),
    PRIMARY KEY (user_id, session_id, key)
)
```

A `null` `sessionId` is stored as the sentinel value `"*"` so user-global entries can coexist with session-scoped entries in the same table. The `dimensions` parameter is accepted for API compatibility with other `IVectorStore` implementations but is not used structurally because embeddings are stored as variable-length JSON text.

### The `vec_distance_cosine` UDF

`RegisterVectorFunctions` wires up a managed scalar function so SQLite can call it during `ORDER BY distance ASC`:

```csharp
using Microsoft.Data.Sqlite;

connection.CreateFunction("vec_distance_cosine", (string v1, string v2) =>
{
    float[] a = ParseVector(v1);
    float[] b = ParseVector(v2);
    double dot = a.Zip(b).Sum(p => (double)p.First * p.Second);
    double normA = Math.Sqrt(a.Sum(x => (double)x * x));
    double normB = Math.Sqrt(b.Sum(x => (double)x * x));
    if (normA == 0 || normB == 0) return 1.0;
    return 1.0 - (dot / (normA * normB));
});
```

Vectors are persisted in bracket notation (`[f1,f2,...]`) matching pgvector's literal format.

### Metadata Filtering

SQLite has no native JSON containment operator, so metadata filtering runs in-process after SQL retrieval:

1. SQL filters by `user_id`, optional `session_id`, and optional `key`, then orders by computed distance.
2. When `MetadataFilter` is provided, SQL uses `LIMIT -1` (no limit) and C# applies `MatchesMetadataFilter` after the row scan.
3. `MatchesMetadataFilter` supports scalar string equality and array subset containment (every element in the filter array must appear in the metadata array).

When `Query.Value` is empty or whitespace, no query embedding is generated and `distance` is `0.0` for all returned rows.

## Observability

- **Activity source:** `Agency.VectorStore.Sql.Sqlite`
- **Meter:** `Agency.VectorStore.Sql.Sqlite`
- **Activities:** `vectorstore.initialize`, `vectorstore.search`, `vectorstore.upsert`, `vectorstore.delete`
- **Counter:** `vectorstore.operations` — tags: `operation`, `status` (`success` / `error`)
- **Histogram:** `vectorstore.duration` (milliseconds) — tag: `operation`
- Failed operations set `ActivityStatusCode.Error` and attach an `exception` event with type, message, and stack trace.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.VectorStore.Common]] | Implements `IVectorStore`; uses `Query`, `SearchHit<TValue>`, and `JsonMetadataHelpers`. |
| [[Agency.Embeddings.Common]] | Depends on `IEmbeddingGenerator` to embed values at upsert time and queries at search time. |
| [[Agency.Sql.Sqlite]] | Uses `SqliteRunner` for all connection management and query execution. |

## Design Notes

- The cosine-distance function is implemented entirely in managed C# — no native sqlite-vec extension is loaded. This simplifies deployment at the cost of in-process CPU for similarity computation.
- Metadata filtering is deliberately deferred to C# rather than expressed in SQL because SQLite's JSON functions lack a set-containment operator; this means SQL always returns all candidate rows when a `MetadataFilter` is present, and the `Limit` is applied after the in-memory filter pass.
| [[Agency.Sql.Sqlite]] | Uses `SqliteRunner` for SQL execution and connection-level UDF registration. |
