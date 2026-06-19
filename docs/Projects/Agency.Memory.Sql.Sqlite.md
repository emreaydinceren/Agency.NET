# Agency.Memory.Sql.Sqlite
#memory #sql #sqlite #storage #embedded

## What It Is

`Agency.Memory.Sql.Sqlite` is the SQLite implementation of the [[Agency.Memory.Common]] `IMemoryStore` contract that durably stores and vector-searches `Record` items for the Agency long-term memory system. It is the zero-server, embedded, in-process sibling of [[Agency.Memory.Sql.Postgres]] — ideal for local agents, samples, and fast tests that need no external database. It manages a `records` table (per-user partitioning, embeddings stored as JSON-array TEXT), a `user_state` table (write-timestamp tracking), a `watermarks` table (distillation progress), a `dead_letter` table (failed-job audit trail), and a `schema_meta` table (embedding-dimension guard). All tables are provisioned at startup by `MemorySchemaInitializer` using idempotent DDL.

**Namespace:** `Agency.Memory.Sql.Sqlite`

## Prerequisites

- **`Microsoft.Data.Sqlite`** — the managed SQLite provider; declared as a package dependency. No native engine install and no external server are required; the database file is created automatically on first access.
- An **`IEmbeddingGenerator`** registration (e.g., from [[Agency.Embeddings.OpenAI]]) — required by `SqliteMemoryStore` to auto-embed records that arrive without a pre-computed vector.
- **`MemoryOptions`** bound via `IOptions<MemoryOptions>` (registered by `AddAgencyMemory` from [[Agency.Memory.Common]]).

The connection string can target a file (`Data Source=memory.db`) or an in-memory database (`Data Source=mem;Mode=Memory;Cache=Shared`). In-memory databases live only as long as at least one connection stays open, so tests hold a keep-alive connection for the database lifetime.

## API Surface

### `SqliteMemoryStore` — `IMemoryStore` implementation

```csharp
// File: src/Memory/Agency.Memory.Sql.Sqlite/SqliteMemoryStore.cs
using Agency.Embeddings.Common;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agency.Memory.Sql.Sqlite;

public sealed class SqliteMemoryStore : IMemoryStore
{
    public const string ActivitySourceName = "Agency.Memory.Sql.Sqlite";
    public const string MeterName          = "Agency.Memory.Sql.Sqlite";

    public SqliteMemoryStore(
        string connectionString,
        IEmbeddingGenerator embedder,
        IOptions<MemoryOptions> options,
        ILogger<SqliteMemoryStore> logger);

    // IMemoryStore
    public Task<Record>                   UpsertAsync(Record record, CancellationToken ct = default);
    public Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken ct = default);
    public Task<Record?>                  GetByKeyAsync(string userId, string? sessionId, string domain, string key, CancellationToken ct = default);
    public Task<bool>                     ForgetAsync(string userId, string domain, string key, CancellationToken ct = default);
    public Task<int>                      ForgetMeAsync(string userId, CancellationToken ct = default);
    public Task<DateTimeOffset?>          LastWrittenAtAsync(string userId, CancellationToken ct = default);
    public Task<IReadOnlyList<Record>>    GetAllForUserAsync(string userId, CancellationToken ct = default);
    public Task<int>                      DeleteWhereTtlExceededAsync(ContentType contentType, TimeSpan ttl, DateTimeOffset now, CancellationToken ct = default);
    public Task<int>                      DeleteWhereLowImportanceStaleAsync(double importanceThreshold, TimeSpan staleAge, DateTimeOffset now, CancellationToken ct = default);
    public Task<Record>                   MergeAsync(IReadOnlyList<string> idsToDelete, Record newRecord, CancellationToken ct = default);
    public Task<Record?>                  UpdateRecordAsync(string recordId, string userId, string? newValue, double? newImportance, CancellationToken ct = default);
    public Task<bool>                     DeleteByIdAsync(string recordId, string userId, CancellationToken ct = default);
}
```

### `MemorySchemaInitializer` — `IMemorySchemaInitializer` implementation

```csharp
// File: src/Memory/Agency.Memory.Sql.Sqlite/MemorySchemaInitializer.cs
using Agency.Memory.Common.Storage;

namespace Agency.Memory.Sql.Sqlite;

public sealed class MemorySchemaInitializer : IMemorySchemaInitializer
{
    public MemorySchemaInitializer(string connectionString);

    /// Provisions all required tables and indexes. Safe to call on every startup (all DDL uses IF NOT EXISTS).
    /// Persists the embedding dimension to schema_meta on first init and throws InvalidOperationException
    /// if a later call passes a dimension that differs from the stored one.
    public Task InitializeAsync(int embeddingDim, CancellationToken ct = default);
}
```

### `SqliteWatermarkRepository` — `IWatermarkStore` implementation

```csharp
// File: src/Memory/Agency.Memory.Sql.Sqlite/SqliteWatermarkRepository.cs
using Agency.Memory.Common.Storage;

namespace Agency.Memory.Sql.Sqlite;

public sealed class SqliteWatermarkRepository : IWatermarkStore
{
    public SqliteWatermarkRepository(string connectionString);

    /// Returns the last successfully distilled turn index for (userId, sessionId), or 0 if none.
    public Task<int> GetAsync(string userId, string sessionId, CancellationToken ct = default);

    /// Advances the watermark to candidate if greater than the stored value (MAX semantics).
    public Task<int> AdvanceAsync(string userId, string sessionId, int candidate, CancellationToken ct = default);

    /// Deletes the watermark row for the given session.
    public Task DeleteAsync(string userId, string sessionId, CancellationToken ct = default);
}
```

### `SqliteDeadLetterRepository` — `IDeadLetterStore` implementation

```csharp
// File: src/Memory/Agency.Memory.Sql.Sqlite/SqliteDeadLetterRepository.cs
using Agency.Memory.Common.Storage;

namespace Agency.Memory.Sql.Sqlite;

public sealed class SqliteDeadLetterRepository : IDeadLetterStore
{
    public SqliteDeadLetterRepository(string connectionString);

    /// Persists a failed distillation or consolidation job to the dead_letter table.
    public Task WriteAsync(string userId, string? sessionId, string jobKind, object payload, Exception error, CancellationToken ct = default);

    /// Returns all dead-letter entries created after cutoff, ordered by created_at ascending.
    public Task<IReadOnlyList<DeadLetterEntry>> ListSinceAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}

public sealed record DeadLetterEntry(
    string Id,
    string UserId,
    string? SessionId,
    string JobKind,
    string JobPayloadJson,
    string Error,
    DateTimeOffset CreatedAt);
```

## Registration

Call `AddAgencyMemorySqlite` alongside `AddAgencyMemory` and `AddAgencyEmbeddingsOpenAI` (or equivalent) in any order. The extension method registers `SqliteWatermarkRepository`, `SqliteDeadLetterRepository`, `MemorySchemaInitializer`, and `IMemoryStore → SqliteMemoryStore`, all as singletons. It also registers the provider-neutral abstractions `IWatermarkStore`, `IDeadLetterStore`, and `IMemorySchemaInitializer` (defined in [[Agency.Memory.Common]]), which is what lets the rest of the pipeline ([[Agency.Memory.Distiller]] and the host) resolve storage without referencing this concrete provider.

```csharp
// File: src/Memory/Agency.Memory.Sql.Sqlite/SqliteMemoryServiceCollectionExtensions.cs
using Agency.Memory.Sql.Sqlite;
using Microsoft.Extensions.DependencyInjection;

services
    .AddAgencyMemory(opts => { /* MemoryOptions */ })
    .AddAgencyEmbeddingsOpenAI(opts => { /* EmbeddingOptions */ })
    .AddAgencyMemorySqlite("Data Source=memory.db");

// Then run schema init at application startup (resolve the provider-neutral interface):
var initializer = app.Services.GetRequiredService<IMemorySchemaInitializer>();
await initializer.InitializeAsync(embeddingDim: 1536);
```

`IEmbeddingGenerator` and `IOptions<MemoryOptions>` must be registered before the store is first resolved, but the relative order of the `AddX` registration calls is immaterial. `AddAgencyMemorySqlite` registers `IMemoryStore` via a lazy factory lambda; the factory runs on first resolution of `IMemoryStore` (after `host.Build()`), not at the time the extension method is called.

### Selecting the provider via configuration

Because both providers register the same `IMemoryStore`/`IWatermarkStore`/`IDeadLetterStore`/`IMemorySchemaInitializer` surface, the host picks one at composition time. The console (`Agency.Harness.Console/Program.cs`) switches on a `Memory:Provider` setting (`"postgres"` — the default — or `"sqlite"`) and reads the matching connection string:

```jsonc
// appsettings.json
"Memory": { "Enabled": true, "Provider": "sqlite" },
"ConnectionStrings": {
  "PostgreSql": "Host=localhost;Port=5432;Database=dev_db;Username=dev_user;Password=dev_password",
  "Sqlite": "Data Source=agency-memory.db"
}
```

```csharp
switch (provider.ToLowerInvariant())
{
    case "postgres": services.AddAgencyMemoryPostgres(pgConnectionString); break;
    case "sqlite":   services.AddAgencyMemorySqlite(sqliteConnectionString); break;
}
```

Nothing downstream — [[Agency.Memory.Retrieval]], [[Agency.Memory.Distiller]], [[Agency.Memory.Consolidator]], [[Agency.Memory.Hygiene]] — changes between providers; they depend only on the [[Agency.Memory.Common]] contracts.

## How It Works

### Schema

`MemorySchemaInitializer` provisions five tables on startup:

| Table | Purpose |
|---|---|
| `records` | Primary store — one row per `Record`. PK is a `TEXT` UUID generated in C#. |
| `user_state` | Holds `last_written_at` per `user_id`; drives the retrieval gate. |
| `watermarks` | Tracks the last distilled turn index per `(user_id, session_id)`. |
| `dead_letter` | Audit log for distillation/consolidation jobs that exhausted retries. |
| `schema_meta` | Key/value table; stores the embedding dimension for the fail-fast guard. |

The `records` table schema:

```sql
CREATE TABLE IF NOT EXISTS records (
    id               TEXT PRIMARY KEY,                -- UUID string generated in C#
    user_id          TEXT NOT NULL,
    session_id       TEXT NULL,
    content_type     INTEGER NOT NULL,               -- ContentType enum: 0=Fact, 1=Memory
    domain           TEXT NOT NULL,
    key              TEXT NOT NULL,
    title            TEXT NOT NULL,
    value            TEXT NOT NULL,
    tags             TEXT NOT NULL DEFAULT '[]',      -- JSON array
    importance       REAL NOT NULL CHECK (importance >= 0 AND importance <= 1),
    embedding        TEXT NOT NULL,                   -- JSON array, e.g. "[0.12,-0.34,...]"
    created_at       TEXT NOT NULL,                   -- ISO-8601 round-trip ("O"), UTC
    updated_at       TEXT NOT NULL,
    last_accessed_at TEXT NULL
);
```

Indexes created:

- `records_upsert_key` — functional unique index on `(user_id, COALESCE(session_id, ''), domain, key)`.
- `records_user_content_type_idx` — index on `(user_id, content_type)`.
- `records_user_domain_idx` — index on `(user_id, domain)`.
- `records_updated_at_idx` — index on `updated_at`.
- `records_last_accessed_at_idx` — partial index on `last_accessed_at WHERE last_accessed_at IS NOT NULL`.

There is no ANN (HNSW) index; similarity search is a brute-force scan of the user's rows (see below).

### Upsert key

The conflict target for `UpsertAsync` and `MergeAsync` is `(user_id, COALESCE(session_id, ''), domain, key)`. This treats a `NULL` `session_id` as the empty string, collapsing all user-global records with the same domain and key into a single row. SQLite supports expression-based `ON CONFLICT` targets backed by a matching functional unique index, so the same `COALESCE` expression appears in both the index and the `ON CONFLICT` clause.

### Cosine similarity search (in-process UDF)

Embeddings are stored as JSON-array TEXT. On every connection, `SqliteMemoryStore` registers a `vec_distance_cosine(a, b)` scalar UDF (defined in `VectorFunctions`) that parses two JSON-array vectors and returns cosine *distance* (`1.0 - cosine_similarity`; `1.0` when either norm is zero). `SearchAsync` executes a single parameterised query:

```sql
SELECT ..., vec_distance_cosine(embedding, @query_vec) AS distance
FROM records
WHERE user_id = @user_id [AND content_type = @content_type] [AND domain = @domain]
ORDER BY distance ASC
LIMIT @top_k;
```

The distance value is converted to similarity with `similarity = 1.0 - distance`, matching the Postgres provider's `<=>` semantics so [[Agency.Memory.Retrieval]] behaves identically regardless of backend. The scan is `O(n·d)` over the user's rows — appropriate for the embedded/local scale this provider targets. After returning results, `SearchAsync` fires a background `Task` (fire-and-forget) to bump `last_accessed_at` for the hit rows so hygiene staleness checks reflect recent access without blocking the hot path.

### `LastWrittenAt` and the retrieval gate

Every write operation (`UpsertAsync`, `ForgetAsync`, `ForgetMeAsync`, `MergeAsync`, `UpdateRecordAsync`, `DeleteByIdAsync`) issues an upsert to `user_state` with `MAX(stored, new)` semantics, ensuring the timestamp is monotonically non-decreasing. Because timestamps are stored as ISO-8601 round-trip (`"O"`) strings, lexical ordering equals chronological ordering, so the string `MAX` is correct. The value is also written through to an in-memory `ConcurrentDictionary<string, DateTimeOffset>` keyed by `userId`, giving `LastWrittenAtAsync` an O(1) hot path with a single DB hydration on cold start.

### TTL and hygiene operations

`DeleteWhereTtlExceededAsync` and `DeleteWhereLowImportanceStaleAsync` both accept a caller-supplied `DateTimeOffset now`. Rather than computing date arithmetic in SQL, the store computes the cutoff (`now - ttl` / `now - staleAge`) in C# and compares `updated_at`/`last_accessed_at` against the formatted cutoff string. This keeps the staleness window deterministic under a virtual `TimeProvider` clock in tests (Spec TI-4), with no dependency on SQLite date functions.

### Atomic merge (`MergeAsync`)

`MergeAsync` opens a single connection and transaction, deletes the listed record IDs (via a dynamically built `id IN (@id0, @id1, ...)` list, scoped to the owning `userId`), inserts the replacement record, and bumps `user_state.last_written_at` — all within one transaction. If any step fails, the transaction is rolled back. This implements the Consolidator's `Memory_Merge` tool atomicity requirement (Spec §6.3 / §8.4) and is the reason the store manages `SqliteConnection` directly rather than going through the stateless [[Agency.Sql.Sqlite]] runner.

### Distillation watermarks

`SqliteWatermarkRepository` persists the last successfully distilled turn index per `(userId, sessionId)`. `AdvanceAsync` uses `MAX(stored, candidate)` semantics so concurrent or out-of-order completions cannot move the watermark backwards. An in-process `ConcurrentDictionary` cache provides O(1) hot-path reads; a DB read hydrates the cache on miss.

### Dead-letter audit

`SqliteDeadLetterRepository.WriteAsync` records failed distillation and consolidation jobs (job kind, JSON payload as TEXT, error message, stack trace) to the `dead_letter` table. The table is write-only for the live pipeline — no retry logic reads from it. `ListSinceAsync` is provided for operational tooling and tests.

## Observability

`SqliteMemoryStore` emits telemetry under a single name shared by both its `ActivitySource` and `Meter`:

- **`ActivitySourceName` / `MeterName`** = `"Agency.Memory.Sql.Sqlite"`

`UpsertAsync` starts a `memory.upsert` activity and `SearchAsync` starts a `memory.search` activity (both `ActivityKind.Client`). The meter publishes:

| Instrument | Type | Unit | Tags | Description |
|---|---|---|---|---|
| `memory.upsert.count` | `Counter<long>` | — | `status` (`success`/`error`) | Total upsert operations. |
| `memory.upsert.duration` | `Histogram<double>` | `ms` | — | Upsert duration in milliseconds. |
| `memory.search.count` | `Counter<long>` | — | `status` (`success`/`error`) | Total search operations. |
| `memory.search.duration` | `Histogram<double>` | `ms` | — | Search duration in milliseconds. |

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Memory.Common]] | Defines `IMemoryStore`, `Record`, `ContentType`, `SearchQuery`, `SearchHit`, and `MemoryOptions` — the entire contract this project implements |
| [[Agency.Memory.Sql.Postgres]] | Sibling provider implementing the same `IMemoryStore` contract on PostgreSQL + pgvector; this project is the embedded/zero-server alternative |
| [[Agency.Embeddings.Common]] | Provides `IEmbeddingGenerator`; `SqliteMemoryStore` calls it to produce embeddings for records that arrive without a pre-computed vector |
| [[Agency.Sql.Sqlite]] | Shared SQLite infrastructure; `VectorFunctions` reuses the same in-process cosine-UDF approach used by [[Agency.VectorStore.Sql.Sqlite]] |
| [[Agency.Memory.Retrieval]] | Calls `IMemoryStore.SearchAsync` and `LastWrittenAtAsync` on every agent iteration; depends on the `IMemoryStore` contract, not on a specific backend |
| [[Agency.Memory.Distiller]] | Calls `IMemoryStore.UpsertAsync` and the watermark repository to persist extracted episodes and advance the distillation watermark |
| [[Agency.Memory.Consolidator]] | Calls `IMemoryStore.GetAllForUserAsync`, `MergeAsync`, `UpdateRecordAsync`, and `DeleteByIdAsync` during cross-session memory reconciliation |
| [[Agency.Memory.Hygiene]] | Calls `DeleteWhereTtlExceededAsync` and `DeleteWhereLowImportanceStaleAsync` on a background schedule to prune stale records |

## Design Notes

- **Why embeddings are stored as JSON-array TEXT with a brute-force cosine UDF instead of a vector index.** SQLite has no native vector type or ANN index. Rather than take a native dependency (e.g. `sqlite-vec`), this provider reuses the proven in-process `vec_distance_cosine` UDF pattern already established by [[Agency.VectorStore.Sql.Sqlite]]. For the embedded/local scale this backend targets, a per-user linear scan is fast enough and keeps the provider dependency-free and trivially portable. Production-scale approximate-nearest-neighbour search remains the job of [[Agency.Memory.Sql.Postgres]].

- **Why timestamps are ISO-8601 round-trip strings.** Storing `created_at`/`updated_at`/`last_accessed_at`/`last_written_at` with the `"O"` format (and always UTC) makes lexical string ordering equal to chronological ordering. This lets the monotonic `MAX(...)` upserts on `user_state` and `watermarks` work directly on TEXT columns and lets range comparisons in the hygiene sweeps use simple string `<` predicates — no SQLite date functions required.

- **Why the store manages `SqliteConnection` directly instead of using the `Agency.Sql.Sqlite` runner.** `MergeAsync` requires an atomic multi-statement transaction (delete + insert + watermark bump), and the shared runner executes one statement per freshly-opened connection with no transaction surface. Managing the connection directly mirrors how [[Agency.Memory.Sql.Postgres]] uses `NpgsqlDataSource`, and lets the store register the cosine UDF on every connection it opens.

- **Why the embedding dimension is persisted to `schema_meta`.** Unlike Postgres, where `vector(N)` encodes the dimension in the column type and can be validated from the catalog, a SQLite TEXT column carries no dimension. To preserve the same §12.3 fail-fast behaviour, the initializer records the dimension on first init and throws `InvalidOperationException` if a later call passes a different one — catching an accidental embedder swap before it silently corrupts search results.
