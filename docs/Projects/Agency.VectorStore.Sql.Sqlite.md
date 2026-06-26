# Agency.VectorStore.Sql.Sqlite
#vectorstore #sqlite #semantic-search #cosine #udf #observability

## What It Is

`Agency.VectorStore.Sql.Sqlite` is the SQLite-backed `IVectorStore` implementation that persists JSON-serialized values and their embeddings in a single `semantic_kv_store` table and computes cosine similarity through a pure-managed scalar UDF (`vec_distance_cosine`) registered on each opened connection. Each row is scoped by `(user_id, session_id, project_id, key)`, letting a single store hold user-global rows, session-scoped rows, and project-scoped (ingested document) rows side by side. `SearchAsync` and `ListDocumentsAsync` union those three scopes in one query, while metadata filtering is applied in-process after the SQL query because SQLite has no native JSONB containment operator.

**Namespace:** `Agency.VectorStore.Sql.Sqlite`

## Prerequisites

- `Microsoft.Data.Sqlite` — the embeddings are stored as JSON-array `TEXT`, so no native vector extension is required; the `vec_distance_cosine` UDF is implemented in managed C#.
- A `SqliteRunner` (from [[Agency.Sql.Sqlite]]) whose `onConnectionOpen` callback invokes `SqliteKVStore.RegisterVectorFunctions`, otherwise the UDF is unavailable on the connection.

## API Surface

### SqliteKVStore

```csharp
// File: src/VectorStore/Agency.VectorStore.Sql.Sqlite/SqliteKVStore.cs
public sealed class SqliteKVStore : IVectorStore
{
    public const string ActivitySourceName = "Agency.VectorStore.Sql.Sqlite";
    public const string MeterName = "Agency.VectorStore.Sql.Sqlite";

    public SqliteKVStore(
        IEmbeddingGenerator embeddingGenerator,
        SqliteRunner sqliteRunner,
        ILogger<SqliteKVStore>? logger = null);

    public static void RegisterVectorFunctions(SqliteConnection connection);

    public Task InitializeSchemaAsync(
        int dimensions = 1536,
        CancellationToken cancellationToken = default);

    public Task<IReadOnlyList<SearchHit<TValue>>> SearchAsync<TValue>(
        Query query,
        CancellationToken cancellationToken = default);

    public Task UpsertAsync<TValue>(
        string userId,
        string? sessionId,
        string key,
        TValue value,
        IDictionary<string, object>? metadata = null,
        string? projectId = null,
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

The class implements `IVectorStore` from [[Agency.VectorStore.Common]]. The query/result contracts it consumes live in that project:

```csharp
// File: src/VectorStore/Agency.VectorStore.Common/Query.cs
public record class Query(
    string UserId,
    string? SessionId,
    string? Key,
    string? Value,
    IDictionary<string, object>? MetadataFilter = null,
    int? Limit = 10,
    bool? IncludeMetadataInResults = false,
    IReadOnlyList<string>? ProjectIds = null);
```

```csharp
// File: src/VectorStore/Agency.VectorStore.Common/DocumentInfo.cs
public record DocumentInfo(string SourceFile, string SessionId, string ProjectId);
```

## How It Works

### Schema

`InitializeSchemaAsync` **drops and recreates** `semantic_kv_store`. The table carries a `project_id TEXT NOT NULL DEFAULT '*'` column and a four-part primary key `(user_id, session_id, project_id, key)`. Embeddings are stored in the `embedding TEXT` column as a JSON-style array literal (`[0.12,-0.34,...]`); metadata is stored as a JSON `TEXT` blob.

### Scope sentinels

Both `session_id` and `project_id` use `"*"` as a sentinel for "global". `UpsertAsync` and `DeleteAsync` resolve a `null` `sessionId`/`projectId` to `"*"` before touching the database, and `SearchAsync`/`ListDocumentsAsync` resolve a `null` query `SessionId` to `"*"`. On the way out, `HydrateSearchHit` maps a stored `"*"` session back to `null` so callers never see the sentinel.

### Three-scope union search

`SearchAsync` builds a `WHERE user_id = @uid` query whose scope predicate unions up to three buckets:

1. **Global** — `session_id = '*' AND project_id = '*'`
2. **Session** — `session_id = @sid AND project_id = '*'`
3. **Loaded projects** — when `Query.ProjectIds` is non-empty, a clause `OR (session_id = '*' AND project_id IN (@pid0, @pid1, ...))` is appended dynamically, one parameter per requested project.

Similarity is computed by the `vec_distance_cosine(embedding, @qVector)` UDF (cosine distance = `1 - cosine_similarity`); results are ordered `distance ASC`. When the query has no `Value`, distance collapses to `0.0` and the search degenerates to a scoped key/metadata lookup. When a `MetadataFilter` is present the SQL `LIMIT` is suppressed (`-1`) so containment filtering and the final `Take(limit)` happen in C#.

> **Breaking change:** a `null` `SessionId` no longer fans out across every session. It now resolves to the global sentinel `"*"`, so a null-session search returns only global (and any requested project) rows, not all of the user's sessions.

### Document and project listing

`ListProjectsAsync` returns the distinct non-`"*"` `project_id` values for a user. `ListDocumentsAsync` reuses the same three-scope union and returns one `DocumentInfo` per distinct `SELECT DISTINCT json_extract(metadata, '$.source_file')` (rows without a `source_file` metadata key are excluded), carrying the originating session and project.

### Usage

```csharp
using Agency.Embeddings.Common;
using Agency.Sql.Sqlite;
using Agency.VectorStore.Common;
using Agency.VectorStore.Sql.Sqlite;

// Register the cosine UDF on every connection the runner opens.
var runner = new SqliteRunner(connectionString, onConnectionOpen: SqliteKVStore.RegisterVectorFunctions);
var store = new SqliteKVStore(embeddingGenerator, runner);
await store.InitializeSchemaAsync(dimensions: 1536);

// Ingest a chunk into a project scope with a source_file tag.
await store.UpsertAsync(
    userId: "alice",
    sessionId: null,                      // stored as "*"
    key: "doc-1#0",
    value: "The quarterly report shows...",
    metadata: new Dictionary<string, object> { ["source_file"] = "report.md" },
    projectId: "finance");

// Search global + session + the "finance" project in one shot.
var hits = await store.SearchAsync<string>(new Query(
    UserId: "alice",
    SessionId: "session-42",
    Key: null,
    Value: "what were the quarterly numbers?",
    ProjectIds: new[] { "finance" }));

IReadOnlyList<DocumentInfo> docs = await store.ListDocumentsAsync(
    "alice", "session-42", new[] { "finance" });
```

## Observability

The store defines an `ActivitySource` and `Meter`, both named `"Agency.VectorStore.Sql.Sqlite"` (exposed as `ActivitySourceName` / `MeterName`). Each operation opens a client activity (`vectorstore.initialize`, `vectorstore.search`, `vectorstore.upsert`, `vectorstore.delete`) tagged with the operation, key parameters, and outcome, recording an `exception` event on failure.

| Metric | Kind | Tags |
|---|---|---|
| `vectorstore.operations` | Counter `<long>` (`{operation}`) | `operation` (`initialize`/`search`/`upsert`/`delete`), `status` (`success`/`error`) |
| `vectorstore.duration` | Histogram `<double>` (`ms`) | `operation` (`initialize`/`search`/`upsert`/`delete`) |

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.VectorStore.Common]] | Provides the `IVectorStore` contract this class implements, plus `Query`, `SearchHit<TValue>`, `DocumentInfo`, and `JsonMetadataHelpers`. |
| [[Agency.Embeddings.Common]] | Supplies the `IEmbeddingGenerator` used to vectorize stored values and query text. |
| [[Agency.Sql.Sqlite]] | Provides the `SqliteRunner` used for all SQL execution and the connection-open hook for `RegisterVectorFunctions`. |
| [[Agency.VectorStore.Sql.Postgres]] | Sibling `IVectorStore` implementation backed by PostgreSQL + pgvector; same contract, native vector indexing instead of a managed UDF. |

## Design Notes

- **`"*"` sentinel instead of `NULL` for scope columns.** `session_id` and `project_id` are part of the primary key, and SQLite (like SQL in general) treats `NULL` as not-equal-to-`NULL`, which would break uniqueness and equality joins for global rows. A concrete `"*"` sentinel keeps the key total and lets the union predicates compare with plain `=`/`IN`. The boundary converts `null` to `"*"` on write and back to `null` on read so callers keep a clean nullable API.
- **Embeddings stored as JSON-array `TEXT`.** SQLite has no native vector type, so vectors are serialized with `FormatVector` (invariant-culture floats) and parsed back by the managed `vec_distance_cosine` UDF. This keeps the store dependency-free (no compiled native extension) at the cost of full-scan cosine evaluation — acceptable for the per-user/session/project row counts this store targets.
- **`InitializeSchemaAsync` drops the table.** It runs `DROP TABLE IF EXISTS` before `CREATE`, so calling it against an existing store is destructive — it is a fresh-initialization / test-setup primitive, not a migration. The `project_id` column was added this way rather than via `ALTER TABLE`.
- **Three-scope union over separate queries.** Folding global, session, and loaded-project rows into one `WHERE` clause means a single ranked `ORDER BY distance` pass across every relevant scope, avoiding client-side merge/re-rank of multiple result sets.
- **Metadata filtering in C#, not SQL.** SQLite lacks a JSONB containment operator, so array-subset and scalar metadata matching run in-process; the SQL `LIMIT` is therefore disabled when a filter is present and re-applied after filtering.
