# Agency.VectorStore.Sql.Postgre

#vectorstore #postgresql #pgvector #hnsw #observability

## What It Is

Agency.VectorStore.Sql.Postgre is the PostgreSQL-backed vector store implementation that persists JSON values and their embeddings in a `semantic_kv_store` table and retrieves them via cosine-distance search with optional JSONB metadata filtering.

**Namespace:** `Agency.VectorStore.Sql.Postgre`

## Prerequisites

- PostgreSQL 18 with the **pgvector** extension available (the extension is created automatically by `InitializeSchemaAsync`).
- A running PostgreSQL instance; see `src/docker-compose.yml` for the local dev setup.

## API Surface

`PostgreKVStore` implements `IVectorStore` (defined in [[Agency.VectorStore.Common]]) and adds one schema-setup method.

```csharp
// File: src/VectorStore/Agency.VectorStore.Sql.Postgre/PostgreKVStore.cs
using Agency.VectorStore.Common;
using Agency.Embeddings.Common;
using Agency.Sql.Postgre;

public class PostgreKVStore : IVectorStore
{
    // Telemetry names
    public const string ActivitySourceName = "Agency.VectorStore.Sql.Postgre";
    public const string MeterName          = "Agency.VectorStore.Sql.Postgre";

    // Constructor
    public PostgreKVStore(
        IEmbeddingGenerator embeddingGenerator,
        PostgreSqlRunner postgreSqlRunner,
        ILogger<PostgreKVStore> logger);

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
3. Executes `INSERT … ON CONFLICT (user_id, session_id, key) DO UPDATE` to atomically upsert.
4. A `null` `sessionId` is stored as the sentinel value `"*"` so it participates in the primary key without nullable columns.

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

The SQL:
- Uses `<=>` (cosine distance) when `query.Value` is non-empty; otherwise distances default to `0.0` so key-only lookups still work.
- Uses `@>` for JSONB metadata containment when `query.MetadataFilter` is set.
- Filters by `session_id` only when `query.SessionId` is non-null, allowing cross-session searches.

### Delete

Returns `true` when a row was deleted, `false` when no matching row existed.

## Observability

- **Activity source / meter name:** `Agency.VectorStore.Sql.Postgre`
- **Activities:** `vectorstore.initialize`, `vectorstore.upsert`, `vectorstore.search`, `vectorstore.delete`
- **Counter:** `vectorstore.operations` — tagged with `operation` and `status` (`success` / `error`)
- **Histogram:** `vectorstore.duration` (milliseconds) — tagged with `operation`

Every activity records an `exception` event with `exception.type`, `exception.message`, and `exception.stacktrace` tags on failure.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.VectorStore.Common]] | Implements `IVectorStore`; consumes `Query`, `SearchHit<TValue>`, and `JsonMetadataHelpers`. |
| [[Agency.Embeddings.Common]] | Injects `IEmbeddingGenerator` to produce embeddings for upsert and semantic search. |
| [[Agency.Sql.Postgre]] | Injects `PostgreSqlRunner` for parameterized SQL execution and result hydration. |

## Design Notes

- The `sessionId = null` → `"*"` sentinel keeps `session_id` NOT NULL in the primary key, avoiding nullable primary-key columns in PostgreSQL while still supporting user-global entries that span all sessions.
- Vector parameters are passed as `NpgsqlTypes.Pgvector.Vector` objects rather than raw strings where possible for type safety, but the upsert path builds a plain `[f1,f2,…]` literal and casts it with `::vector` because Npgsql's pgvector type mapping requires the extension to be loaded in the connection session — the literal cast is more portable across connection pool configurations.
- Schema migration is intentionally destructive for the legacy table shape (no `user_id` column): the table is dropped and recreated rather than altered, since the legacy shape had no production data at the time this migration was introduced.
