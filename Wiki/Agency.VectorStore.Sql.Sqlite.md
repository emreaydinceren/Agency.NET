# Agency.VectorStore.Sql.Sqlite

#vectorstore #sqlite #cosine #udf #observability

## What It Is

Agency.VectorStore.Sql.Sqlite provides a SQLite-backed implementation of `IVectorStore` from [[Agency.VectorStore.Common]]. It stores JSON-serialized values and embeddings in a `semantic_kv_store` table and computes cosine distance with a SQLite scalar UDF (`vec_distance_cosine`) registered on each opened connection.

## Schema

Initialized by calling `InitializeSchemaAsync(dimensions)`:

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

`dimensions` is accepted for API compatibility and telemetry tagging.

## The `vec_distance_cosine` UDF

`SqliteKVStore.RegisterVectorFunctions` registers the scalar function through `Microsoft.Data.Sqlite`:

```csharp
connection.CreateFunction("vec_distance_cosine", (string v1, string v2) =>
{
    float[] a = ParseVector(v1);
    float[] b = ParseVector(v2);
    double dot = a.Zip(b).Sum(p => (double)p.First * p.Second);
    double normA = Math.Sqrt(a.Sum(x => (double)x * x));
    double normB = Math.Sqrt(b.Sum(x => (double)x * x));

    if (normA == 0 || normB == 0)
    {
        return 1.0;
    }

    return 1.0 - (dot / (normA * normB));
});
```

## How It Works

```csharp
var runner = new SqliteRunner(
    "Data Source=agency.db",
    onConnectionOpen: SqliteKVStore.RegisterVectorFunctions);

var store = new SqliteKVStore(embeddingGenerator, runner);
await store.InitializeSchemaAsync();

await store.UpsertAsync(
    userId: "user-1",
    sessionId: "session-1",
    key: "note:1",
    value: new { Text = "Hello world" },
    metadata: new Dictionary<string, object> { ["tags"] = new[] { "welcome", "vector" } });

var hits = await store.SearchAsync<Dictionary<string, object>>(new Query(
    UserId: "user-1",
    SessionId: "session-1",
    Key: null,
    Value: "greeting",
    MetadataFilter: new Dictionary<string, object> { ["tags"] = new[] { "vector" } },
    Limit: 5,
    IncludeMetadataInResults: true));
```

### Metadata Filtering

SQLite has no native JSON containment operator, so metadata filtering runs in process after SQL retrieval:

1. SQL filters by `user_id`, optional `session_id`, and optional `key`, then orders by computed distance.
2. When `MetadataFilter` is provided, SQL uses `LIMIT -1` and C# applies `MatchesMetadataFilter`.
3. `MatchesMetadataFilter` supports scalar equality and array subset containment.

When `Query.Value` is empty, no query embedding is generated and distance is `0.0` for all returned rows.

## Observability

- Activity source name: `Agency.VectorStore.Sql.Sqlite`
- Meter name: `Agency.VectorStore.Sql.Sqlite`
- Activities: `vectorstore.initialize`, `vectorstore.search`, `vectorstore.upsert`, `vectorstore.delete`
- Counter: `vectorstore.operations` (tags include `operation`, `status`)
- Histogram: `vectorstore.duration` in milliseconds

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.VectorStore.Common]] | Implements `IVectorStore`, and uses `Query` / `SearchHit<TValue>`. |
| [[Agency.Embeddings.Common]] | Uses `IEmbeddingGenerator` to generate embeddings for upsert and semantic search. |
| [[Agency.Sql.Sqlite]] | Uses `SqliteRunner` for SQL execution and connection-level UDF registration. |
