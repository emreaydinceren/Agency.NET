# Agency.VectorStore.Sql.Postgre

#vectorstore #postgresql #pgvector #hnsw #observability

## What It Is

Agency.VectorStore.Sql.Postgre provides a PostgreSQL-backed implementation of `IVectorStore` from [[Agency.VectorStore.Common]]. It stores JSON values and vector embeddings in `semantic_kv_store`, supports cosine-distance search with pgvector, and supports metadata filtering with JSONB containment.

## Schema

Initialized by calling `InitializeSchemaAsync(dimensions)`:

```sql
CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE IF NOT EXISTS semantic_kv_store (
    user_id    TEXT NOT NULL,
    session_id TEXT NOT NULL,
    key        TEXT NOT NULL,
    value      JSONB NOT NULL,
    embedding  vector(1536) NOT NULL,
    metadata   JSONB,
    updated_on TIMESTAMPTZ DEFAULT NOW(),
    PRIMARY KEY (user_id, session_id, key)
);

CREATE INDEX IF NOT EXISTS idx_vector_search
ON semantic_kv_store USING hnsw (embedding vector_cosine_ops);

CREATE INDEX IF NOT EXISTS idx_metadata_filter
ON semantic_kv_store USING GIN (metadata);
```

`InitializeSchemaAsync` also drops an older `semantic_kv_store` shape when it exists without the `user_id` column, then recreates the current schema.

## How It Works

### Upsert

```csharp
await store.UpsertAsync(
    userId: "user-1",
    sessionId: "session-1",
    key: "article:42",
    value: new Article { Title = "pgvector basics", Content = "..." },
    metadata: new Dictionary<string, object> { ["tags"] = new[] { "database", "vector" } },
    cancellationToken: cancellationToken);
```

Internally:
1. Serializes `value` to JSON.
2. Generates an embedding from that serialized content via `IEmbeddingGenerator`.
3. Executes `INSERT ... ON CONFLICT (user_id, session_id, key) DO UPDATE`.
4. Stores `metadata` as JSONB.

### Search

```csharp
var query = new Query
{
    UserId = "user-1",
    SessionId = "session-1",
    Value = "how does pgvector work?",
    Limit = 5,
    MetadataFilter = new Dictionary<string, object> { ["tags"] = new[] { "vector" } }
};

IReadOnlyList<SearchHit<Article>> hits = await store.SearchAsync<Article>(query, cancellationToken);
```

The SQL uses:
- `<=>` for pgvector cosine distance.
- `@>` for JSONB metadata containment.
- `CASE WHEN @qVector::vector IS NULL THEN 0.0 ELSE (embedding <=> @qVector::vector) END` so key-only searches work without embedding generation.

## Observability

- Activity source and meter: `Agency.VectorStore.Sql.Postgre`.
- Activities: `vectorstore.initialize`, `vectorstore.search`, `vectorstore.upsert`, `vectorstore.delete`.
- Counter: `vectorstore.operations` with `operation` and `status` tags.
- Histogram: `vectorstore.duration` (milliseconds) with `operation` tag.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.VectorStore.Common]] | Implements `IVectorStore`, consumes `Query` and `SearchHit<TValue>`. |
| [[Agency.Embeddings.Common]] | Uses `IEmbeddingGenerator` to generate embeddings for upsert and query text. |
| [[Agency.Sql.Postgre]] | Uses `PostgreSqlRunner` to execute SQL and hydrate results. |
