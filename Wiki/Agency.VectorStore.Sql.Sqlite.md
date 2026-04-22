# Agency.VectorStore.Sql.Sqlite

#vectorstore #sqlite #cosine #udf #observability

## What It Is

`Agency.VectorStore.Sql.Sqlite` implements [[Agency.VectorStore.Common]]'s `IKVStore` backed by SQLite. Because SQLite has no native vector type or similarity operator, it stores embeddings as JSON-array TEXT columns and registers a C# UDF (`vec_distance_cosine`) on each connection for cosine distance calculation.

## Schema

```sql
CREATE TABLE IF NOT EXISTS semantic_kv_store (
    key        TEXT PRIMARY KEY,
    value      TEXT NOT NULL,       -- JSON-serialized value
    embedding  TEXT NOT NULL,       -- "[f1,f2,...]" float array
    metadata   TEXT,                -- JSON-serialized metadata
    updated_on TEXT DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS idx_kv_key ON semantic_kv_store (key);
```

## The `vec_distance_cosine` UDF

SQLite does not support user-defined aggregate or window functions natively, but `Microsoft.Data.Sqlite` allows registering scalar functions via `CreateFunction`. `SqliteKVStore.RegisterVectorFunctions` must be passed as the `onConnectionOpen` callback to `SqliteRunner`:

```csharp
connection.CreateFunction("vec_distance_cosine", (string v1, string v2) =>
{
    float[] a = ParseVector(v1);   // "[0.1,0.2,...]" → float[]
    float[] b = ParseVector(v2);
    double dot  = a.Zip(b).Sum(p => (double)p.First * p.Second);
    double magA = Math.Sqrt(a.Sum(x => (double)x * x));
    double magB = Math.Sqrt(b.Sum(x => (double)x * x));
    return 1.0 - (dot / (magA * magB));   // cosine distance
});
```

## How It Works

```csharp
// Wire up the runner with the UDF callback
var runner = new SqliteRunner(
    "Data Source=agency.db",
    onConnectionOpen: SqliteKVStore.RegisterVectorFunctions);

var store = new SqliteKVStore(embeddingGenerator, runner);
await store.InitializeSchemaAsync();

// Upsert
await store.UpsertAsync("note:1", new { Text = "Hello world" });

// Search
var hits = await store.SearchAsync<MyDoc>(new Query(Value: "greeting", Limit: 3));
```

### Metadata Filtering

Unlike PostgreSQL's JSONB `@>` operator, SQLite has no native JSON containment. The implementation applies metadata filtering **in-process after the SQL query**:

1. SQL fetches all rows ordered by cosine distance (no SQL `LIMIT` when a metadata filter is present).
2. C# applies `MatchesMetadataFilter()` which supports both scalar equality and array-subset containment.
3. `Take(query.Limit ?? 10)` is applied after filtering.

## Observability

Same metrics as [[Agency.VectorStore.Sql.Postgre]]:
- **Activity** `vectorstore.initialize` / `vectorstore.search` / `vectorstore.upsert` / `vectorstore.delete`
- **Counter** `vectorstore.operations` / **Histogram** `vectorstore.duration`

ActivitySource name: `Agency.VectorStore.Sql.Sqlite`

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.VectorStore.Common]] | Implements `IKVStore` |
| [[Agency.Embeddings.Common]] | Injects `IEmbeddingGenerator` for embedding generation |
| [[Agency.Sql.Sqlite]] | Delegates all SQL execution to `SqliteRunner` |
| [[Agency.Ingestion]] | `DefaultIngestionPipeline` writes chunks here |
