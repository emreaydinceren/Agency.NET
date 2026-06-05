# Agency.VectorStore.Sql.Postgres

#vectorstore #postgresql #pgvector #hnsw #observability

## What It Is

Agency.VectorStore.Sql.Postgres is the PostgreSQL-backed vector store implementation that persists JSON values and their embeddings in a `semantic_kv_store` table and retrieves them via cosine-distance search with optional exact key matching and JSONB metadata filtering.

**Namespace:** `Agency.VectorStore.Sql.Postgres`

## Prerequisites

- PostgreSQL 18 with the **pgvector** extension available (the extension is created automatically by `InitializeSchemaAsync`).
- A running PostgreSQL instance; see `src/docker-compose.yml` for the local dev setup.

## API Surface

`PostgresKVStore` implements `IVectorStore` (defined in [[Agency.VectorStore.Common]]) and adds one schema-setup method.

```csharp
// File: src/VectorStore/Agency.VectorStore.Sql.Postgres/PostgresKVStore.cs
using Agency.VectorStore.Common;
using Agency.Embeddings.Common;
using Agency.Sql.Postgres;
using Microsoft.Extensions.Logging;

public class PostgresKVStore : IVectorStore
{
    // Telemetry names
    public const string ActivitySourceName = "Agency.VectorStore.Sql.Postgres";
    public const string MeterName          = "Agency.VectorStore.Sql.Postgres";

    // Constructor
    public PostgresKVStore(
        IEmbeddingGenerator embeddingGenerator,
        PostgreSqlRunner postgreSqlRunner,
        ILogger<PostgresKVStore> logger);

    // One-time schema setup — call before first use
    public Task InitializeSchemaAsync(int dimensions = 1536, CancellationToken cancellationToken = default);

    // IVectorStore members
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
}
```

## How It Works

### Schema initialization

`InitializeSchemaAsync(dimensions)` runs a single SQL batch that:

1. Creates the `vector` extension if it does not exist.
2. Drops the legacy `semantic_kv_store` table when it exists without a `user_id` column (migration guard).
3. Creates `semantic_kv_store` with columns `user_id`, `session_id`, `key`, `value` (JSONB), `embedding` (vector), `metadata` (JSONB), and `updated_on`.
4. Creates an HNSW index on `embedding` using cosine ops (`vector_cosine_ops`).
5. Creates a GIN index on `metadata` for JSONB containment queries.

### Upsert

```csharp
using Agency.VectorStore.Common;

await store.UpsertAsync(
    userId:    "user-1",
    sessionId: "session-1",
    key:       "article:42",
    value:     new Article { Title = "pgvector basics", Content = "..." },
    metadata:  new Dictionary<string, object> { ["tags"] = new[] { "database", "vector" } },
    cancellationToken: cancellationToken);
```

Steps performed internally:

1. Serializes `value` to JSON via `System.Text.Json`.
2. Generates an embedding for that JSON string via `IEmbeddingGenerator`.
3. Formats the embedding as a `[f1,f2,…]` string literal and casts it with `::vector` in the SQL.
4. Executes `INSERT … ON CONFLICT (user_id, session_id, key) DO UPDATE SET value, embedding, metadata` to atomically upsert.
5. A `null` `sessionId` is stored as the sentinel value `"*"` so it participates in the primary key without nullable columns.

### Search

```csharp
using Agency.VectorStore.Common;

var query = new Query(
    UserId:         "user-1",
    SessionId:      "session-1",
    Key:            null,
    Value:          "how does pgvector work?",
    MetadataFilter: new Dictionary<string, object> { ["tags"] = new[] { "vector" } },
    Limit:          5);

IReadOnlyList<SearchHit<Article>> hits = await store.SearchAsync<Article>(query, cancellationToken);
```

The SQL uses boolean flag parameters (`@hasSessionId`, `@hasKey`, `@hasFilter`) to conditionally activate each filter clause within a single query shape:

- Uses `<=>` (cosine distance) when `query.Value` is non-empty; passes `NULL` otherwise so all rows return distance `0.0` and key-only or metadata-only lookups still work.
- Uses `@>` for JSONB metadata containment when `query.MetadataFilter` is set.
- Filters by `session_id` only when `query.SessionId` is non-null, allowing cross-session searches.
- Filters by `key` only when `query.Key` is non-null, allowing exact key lookups.

### Delete

Returns `true` when a row was deleted, `false` when no matching row existed. Uses the `"*"` sentinel to resolve a `null` `sessionId` before executing the `DELETE`.

## Observability

- **Activity source / meter name:** `Agency.VectorStore.Sql.Postgres`
- **Activities:** `vectorstore.initialize`, `vectorstore.upsert`, `vectorstore.search`, `vectorstore.delete`
- **Counter:** `vectorstore.operations` — tagged with `operation` and `status` (`success` / `error`)
- **Histogram:** `vectorstore.duration` (milliseconds) — tagged with `operation`

Every activity records an `exception` event with `exception.type`, `exception.message`, and `exception.stacktrace` tags on failure.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.VectorStore.Common]] | Implements `IVectorStore`; consumes `Query`, `SearchHit<TValue>`, and `JsonMetadataHelpers`. |
| [[Agency.Embeddings.Common]] | Injects `IEmbeddingGenerator` to produce embeddings for upsert and semantic search. |
| [[Agency.Sql.Postgres]] | Injects `PostgreSqlRunner` for parameterized SQL execution and result hydration. |

## Design Notes

- The `sessionId = null` → `"*"` sentinel keeps `session_id` NOT NULL in the primary key, avoiding nullable primary-key columns in PostgreSQL while still supporting user-global entries that span all sessions.
- The upsert path builds a plain `[f1,f2,…]` vector literal and casts it with `::vector` rather than passing a `Pgvector.Vector` parameter, because Npgsql's pgvector type mapping requires the extension to be loaded in the connection session — the literal cast is more portable across connection pool configurations. The search path passes a `Pgvector.Vector` object directly because the read path opens a fresh connection where the extension is already registered.
- Schema migration is intentionally destructive for the legacy table shape (no `user_id` column): the table is dropped and recreated rather than altered, since the legacy shape had no production data at the time this migration was introduced.
- The search SQL uses boolean flag parameters (`@hasSessionId`, `@hasKey`, `@hasFilter`) instead of dynamic SQL string construction, keeping a single compiled query plan while allowing optional filter activation at the parameter level.
