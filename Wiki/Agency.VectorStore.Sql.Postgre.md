# Agency.VectorStore.Sql.Postgre

#vectorstore #postgresql #pgvector #hnsw #observability

## What It Is

`Agency.VectorStore.Sql.Postgre` implements [[Agency.VectorStore.Common]]'s `IKVStore` interface backed by PostgreSQL with the `pgvector` extension. It provides production-grade vector similarity search using an HNSW index and JSON metadata filtering via the PostgreSQL JSONB `@>` containment operator.

## Schema

Initialized by calling `InitializeSchemaAsync(dimensions)`:

```sql
CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE IF NOT EXISTS semantic_kv_store (
    key        TEXT PRIMARY KEY,
    value      JSONB NOT NULL,
    embedding  vector(1536) NOT NULL,
    metadata   JSONB,
    updated_on TIMESTAMPTZ DEFAULT NOW()
);

-- High-speed ANN search
CREATE INDEX IF NOT EXISTS idx_vector_search
ON semantic_kv_store USING hnsw (embedding vector_cosine_ops);

-- High-speed metadata filtering
CREATE INDEX IF NOT EXISTS idx_metadata_filter
ON semantic_kv_store USING GIN (metadata);
```

## How It Works

### Upsert

```csharp
await store.UpsertAsync("article:42",
    new Article { Title = "pgvector basics", Content = "..." },
    metadata: new Dictionary<string, object> { ["tags"] = new[] { "database", "vector" } });
```

Internally:
1. JSON-serializes `value` and generates an embedding vector from the serialized text.
2. Executes `INSERT … ON CONFLICT (key) DO UPDATE` — idempotent upsert.
3. Stores `metadata` as `JSONB` for later filtering.

### Search

```csharp
IReadOnlyList<SearchHit<Article>> hits = await store.SearchAsync<Article>(
    new Query(Value: "how does pgvector work?", Limit: 5,
              metadataFilter: new Dictionary<string, object> { ["tags"] = new[] { "vector" } }));
```

The SQL uses:
- `<=>` (cosine distance operator from pgvector)
- `@>` (JSONB containment) for metadata filtering
- `CASE WHEN @qVector::vector IS NULL THEN 0.0 ELSE (embedding <=> @qVector::vector) END` to gracefully handle key-only queries without embedding

## Observability

- **Activity** `vectorstore.initialize` / `vectorstore.search` / `vectorstore.upsert`
- **Counter** `vectorstore.operations` (tags: `operation`, `status`)
- **Histogram** `vectorstore.duration` (ms)

ActivitySource name: `Agency.VectorStore.Sql.Postgre`

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.VectorStore.Common]] | Implements `IKVStore` |
| [[Agency.Embeddings.Common]] | Injects `IEmbeddingGenerator` to produce vectors |
| [[Agency.Sql.Postgre]] | Delegates all SQL execution to `PostgreSqlRunner` |
| [[Agency.Ingestion]] | `DefaultIngestionPipeline` writes chunks here |
