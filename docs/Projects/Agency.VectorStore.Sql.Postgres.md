# Agency.VectorStore.Sql.Postgres

#vectorstore #postgresql #pgvector #hnsw #observability

## What It Is

`Agency.VectorStore.Sql.Postgres` is the PostgreSQL-backed vector store implementation that persists JSON values and their embeddings in a `semantic_kv_store` table and retrieves them via cosine-distance search with optional exact key matching and JSONB metadata filtering. Entries are scoped along three axes — user, session, and project — so a single query can fold together user-global, session-specific, and loaded-project results.

**Namespace:** `Agency.VectorStore.Sql.Postgres`

## Prerequisites

- PostgreSQL 18 with the **pgvector** extension available (the extension is created automatically by `InitializeSchemaAsync`).
- A running PostgreSQL instance; see `src/docker-compose.yml` for the local dev setup.

## API Surface

`PostgresKVStore` implements `IVectorStore` (defined in [[Agency.VectorStore.Common]]) and adds one schema-setup method. The store is constructed directly via its public constructor; this project ships no DI registration.

```csharp
// File: src/VectorStore/Agency.VectorStore.Sql.Postgres/PostgresKVStore.cs
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
        string? projectId = null,
        CancellationToken cancellationToken = default);

    public Task<IReadOnlyList<SearchHit<TValue>>> SearchAsync<TValue>(
        Query query,
        CancellationToken cancellationToken = default);

    public Task<bool> DeleteAsync(
        string userId,
        string? sessionId,
        string key,
        string? projectId = null,
        CancellationToken cancellationToken = default);

    public Task<IReadOnlyList<string>> ListProjectsAsync(
        string userId,
        CancellationToken cancellationToken = default);

    public Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(
        string userId,
        string? sessionId,
        IReadOnlyList<string>? projectIds,
        CancellationToken cancellationToken = default);
}
```

`ListDocumentsAsync` returns `DocumentInfo` records (defined in [[Agency.VectorStore.Common]]):

```csharp
// File: src/VectorStore/Agency.VectorStore.Common/DocumentInfo.cs
public record DocumentInfo(string SourceFile, string SessionId, string ProjectId);
```

## How It Works

### Schema initialization

`InitializeSchemaAsync(dimensions)` runs a single SQL batch that:

1. Creates the `vector` extension if it does not exist.
2. Drops any existing `semantic_kv_store` table (`DROP TABLE IF EXISTS … CASCADE`) so the schema is rebuilt cleanly.
3. Creates `semantic_kv_store` with columns `user_id`, `session_id`, `project_id` (`NOT NULL DEFAULT '*'`), `key`, `value` (JSONB), `embedding` (vector), `metadata` (JSONB), and `updated_on`, with a compound primary key `(user_id, session_id, project_id, key)`.
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
    projectId: "docs-site",
    cancellationToken: cancellationToken);
```

Steps performed internally:

1. Serializes `value` to JSON via `System.Text.Json`.
2. Generates an embedding for that JSON string via `IEmbeddingGenerator`.
3. Formats the embedding as a `[f1,f2,…]` string literal and casts it with `::vector` in the SQL.
4. Executes `INSERT … ON CONFLICT (user_id, session_id, project_id, key) DO UPDATE SET value, embedding, metadata` to atomically upsert.
5. A `null` `sessionId` resolves to the sentinel `"*"` and a `null` `projectId` resolves to the sentinel `"*"`, so both columns participate in the primary key without nullable columns.

### Search — three-scope union

```csharp
using Agency.VectorStore.Common;

var query = new Query(
    UserId:         "user-1",
    SessionId:      "session-1",
    Key:            null,
    Value:          "how does pgvector work?",
    MetadataFilter: new Dictionary<string, object> { ["tags"] = new[] { "vector" } },
    Limit:          5,
    ProjectIds:     new[] { "docs-site", "design-notes" });

IReadOnlyList<SearchHit<Article>> hits = await store.SearchAsync<Article>(query, cancellationToken);
```

A single query shape unions three scopes for the user via a disjunction in the `WHERE` clause:

- **Global** — when `@allSessions` is true (`query.SessionId == null`), every row for the user is in scope (global-only effectively, since no session/project filter is applied).
- **Session** — rows where `session_id = @sid AND project_id = '*'`.
- **Loaded projects** — when `@hasProjects` is true, rows where `session_id = '*' AND project_id = ANY(@pids)`.

The project clause matches with `project_id = ANY(@pids)`, where `@pids` is a typed `NpgsqlDbType.Array | NpgsqlDbType.Text` parameter carrying `query.ProjectIds` (empty array when none). Other behavior:

- Uses `<=>` (cosine distance) when `query.Value` is non-empty; passes `NULL` (cast `@qVector::vector`) otherwise so all rows return distance `0.0` and key-only or metadata-only lookups still work.
- Uses `@>` for JSONB metadata containment when `query.MetadataFilter` is set.
- Uses boolean flag parameters (`@allSessions`, `@hasProjects`, `@hasKey`, `@hasFilter`) to activate each clause within one compiled query plan.
- A row whose stored `session_id` equals the `"*"` sentinel is hydrated back to a `null` `SessionId` on the returned `SearchHit<TValue>`. A `null` `SessionId` on the incoming query resolves to global-only.

### Delete

Returns `true` when a row was deleted, `false` when no matching row existed. Resolves `null` `sessionId` and `null` `projectId` to the `"*"` sentinel before executing the `DELETE`, so the delete targets the exact primary-key tuple.

### Listing projects and documents

- `ListProjectsAsync(userId)` returns the distinct, ordered `project_id` values for the user, excluding the `"*"` global sentinel.
- `ListDocumentsAsync(userId, sessionId, projectIds)` returns distinct `DocumentInfo` rows (`source_file`, `session_id`, `project_id`) sourced from the `metadata->>'source_file'` field, scoped with the same three-way union (global / session / loaded projects via `project_id = ANY(@pids)`) and skipping rows without a `source_file`.

## Observability

- **Activity source / meter name:** `Agency.VectorStore.Sql.Postgres`
- **Activities:** `vectorstore.initialize`, `vectorstore.upsert`, `vectorstore.search`, `vectorstore.delete`

| Instrument | Name | Unit | Tags |
|---|---|---|---|
| Counter | `vectorstore.operations` | `{operation}` | `operation`, `status` (`success` / `error`) |
| Histogram | `vectorstore.duration` | `ms` | `operation` |

Every activity records an `exception` event with `exception.type`, `exception.message`, and `exception.stacktrace` tags on failure. `ListProjectsAsync` and `ListDocumentsAsync` do not emit telemetry.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.VectorStore.Common]] | Implements `IVectorStore`; consumes `Query`, `SearchHit<TValue>`, `DocumentInfo`, and `JsonMetadataHelpers`. |
| [[Agency.Embeddings.Common]] | Injects `IEmbeddingGenerator` to produce embeddings for upsert and semantic search. |
| [[Agency.Sql.Postgres]] | Injects `PostgreSqlRunner` for parameterized SQL execution and result hydration. |

## Design Notes

- **Project scope folded into the primary key.** Adding `project_id` to the compound key `(user_id, session_id, project_id, key)` lets the same store hold user-global, session-scoped, and project-scoped entries side by side without separate tables, and lets the search/list queries union all three scopes in one statement.
- **`= ANY(@array)` instead of an `IN (…)` list.** The loaded-project clause binds a single typed text-array parameter (`NpgsqlDbType.Array | NpgsqlDbType.Text`) and matches with `project_id = ANY(@pids)`. This keeps one stable, compiled query plan regardless of how many projects are loaded, avoids building a variable-length `IN` list with N placeholders, and degrades safely to an empty array (matching nothing) when no projects are loaded.
- **`"*"` sentinel instead of NULL.** A `null` `sessionId` or `projectId` resolves to the `"*"` sentinel so both columns stay `NOT NULL` and participate cleanly in the primary key — PostgreSQL treats `NULL` as distinct in uniqueness/equality, which would break `ON CONFLICT` upserts and equality-based scope matching. The sentinel is translated back to `null` when hydrating `SearchHit<TValue>`.
- **Boolean flag parameters over dynamic SQL.** Optional filters (sessions, projects, key, metadata) are toggled by boolean parameters rather than string-concatenated SQL, preserving a single compiled query plan while still supporting key-only, metadata-only, and cross-session lookups.
- **Vector literal on write, NULL-safe cast on read.** Upsert builds a plain `[f1,f2,…]` literal cast with `::vector`; search casts `@qVector::vector` so a missing query embedding becomes `NULL` and yields a `0.0` placeholder distance instead of failing, letting non-semantic (key/metadata) queries reuse the same SQL.
