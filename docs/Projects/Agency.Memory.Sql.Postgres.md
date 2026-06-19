# Agency.Memory.Sql.Postgres
#memory #sql #postgres #storage

## What It Is

`Agency.Memory.Sql.Postgres` is the PostgreSQL + pgvector implementation of the [[Agency.Memory.Common]] `IMemoryStore` contract that durably stores and vector-searches `Record` items for the Agency long-term memory system. It manages a `records` table (HNSW-indexed embeddings, per-user partitioning), a `user_state` table (write-timestamp tracking), a `watermarks` table (distillation progress), and a `dead_letter` table (failed-job audit trail). All four tables are provisioned at startup by `MemorySchemaInitializer` using idempotent DDL.

**Namespace:** `Agency.Memory.Sql.Postgres`

## Prerequisites

- **PostgreSQL 18** (or any version that supports `gen_random_uuid()` and `TIMESTAMPTZ`).
- **pgvector extension** — must be installable in the target database. `MemorySchemaInitializer` runs `CREATE EXTENSION IF NOT EXISTS vector SCHEMA public;` on startup.
- **Npgsql 9+** and the **pgvector .NET client** (`Pgvector` NuGet package) — both are declared as package dependencies in the project.
- An **`IEmbeddingGenerator`** registration (e.g., from [[Agency.Embeddings.OpenAI]]) — required by `PostgresMemoryStore` to auto-embed records that arrive without a pre-computed vector.
- **`MemoryOptions`** bound via `IOptions<MemoryOptions>` (registered by `AddAgencyMemory` from [[Agency.Memory.Common]]).

For local development, Docker Compose in `src/docker-compose.yml` provides a pre-configured PostgreSQL 18 instance at `localhost:5432` with credentials `dev_user` / `dev_password`, database `dev_db`.

## API Surface

The three repositories implement provider-neutral storage contracts defined in [[Agency.Memory.Common]] (`Agency.Memory.Common.Storage`): `PostgresMemoryStore` implements `IMemoryStore`, `MemorySchemaInitializer` implements `IMemorySchemaInitializer`, `WatermarkRepository` implements `IWatermarkStore`, and `DeadLetterRepository` implements `IDeadLetterStore`.

### `PostgresMemoryStore` — `IMemoryStore` implementation

```csharp
// File: src/Memory/Agency.Memory.Sql.Postgres/PostgresMemoryStore.cs
using Agency.Embeddings.Common;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Agency.Memory.Sql.Postgres;

public sealed class PostgresMemoryStore : IMemoryStore
{
    public const string ActivitySourceName = "Agency.Memory.Sql.Postgres";
    public const string MeterName          = "Agency.Memory.Sql.Postgres";

    public PostgresMemoryStore(
        NpgsqlDataSource dataSource,
        IEmbeddingGenerator embedder,
        IOptions<MemoryOptions> options,
        ILogger<PostgresMemoryStore> logger);

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

### `MemorySchemaInitializer` — startup schema provisioner (`IMemorySchemaInitializer`)

```csharp
// File: src/Memory/Agency.Memory.Sql.Postgres/MemorySchemaInitializer.cs
using Agency.Memory.Common.Storage;
using Npgsql;

namespace Agency.Memory.Sql.Postgres;

public sealed class MemorySchemaInitializer : IMemorySchemaInitializer
{
    public MemorySchemaInitializer(NpgsqlDataSource dataSource);

    /// Provisions all required tables and indexes. Safe to call on every startup (all DDL uses IF NOT EXISTS).
    /// Throws InvalidOperationException if the existing embedding column dimension differs from embeddingDim.
    public Task InitializeAsync(int embeddingDim, CancellationToken ct = default);
}
```

### `WatermarkRepository` — distillation progress tracking (`IWatermarkStore`)

```csharp
// File: src/Memory/Agency.Memory.Sql.Postgres/WatermarkRepository.cs
using Agency.Memory.Common.Storage;
using Npgsql;

namespace Agency.Memory.Sql.Postgres;

public sealed class WatermarkRepository : IWatermarkStore
{
    public WatermarkRepository(NpgsqlDataSource dataSource);

    /// Returns the last successfully distilled turn index for (userId, sessionId), or 0 if none.
    public Task<int> GetAsync(string userId, string sessionId, CancellationToken ct = default);

    /// Advances the watermark to candidate if greater than the stored value (GREATEST semantics).
    public Task<int> AdvanceAsync(string userId, string sessionId, int candidate, CancellationToken ct = default);

    /// Deletes the watermark row for the given session.
    public Task DeleteAsync(string userId, string sessionId, CancellationToken ct = default);
}
```

### `DeadLetterRepository` — failed-job persistence (`IDeadLetterStore`)

```csharp
// File: src/Memory/Agency.Memory.Sql.Postgres/DeadLetterRepository.cs
using Agency.Memory.Common.Storage;
using Npgsql;

namespace Agency.Memory.Sql.Postgres;

public sealed class DeadLetterRepository : IDeadLetterStore
{
    public DeadLetterRepository(NpgsqlDataSource dataSource);

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

Call `AddAgencyMemoryPostgres` alongside `AddAgencyMemory` and `AddAgencyEmbeddingsOpenAI` (or equivalent) in any order. The extension method registers everything as singletons. Each concrete repository is registered once, and the provider-neutral storage interfaces from [[Agency.Memory.Common]] are bound back to those same singletons so the Distiller and host can resolve storage without referencing this concrete provider (enabling config-driven provider selection):

- `NpgsqlDataSource` (with pgvector enabled via `UseVector()`)
- `WatermarkRepository`, and `IWatermarkStore` → `WatermarkRepository`
- `DeadLetterRepository`, and `IDeadLetterStore` → `DeadLetterRepository`
- `MemorySchemaInitializer`, and `IMemorySchemaInitializer` → `MemorySchemaInitializer`
- `IMemoryStore` → `PostgresMemoryStore` (lazy factory)

```csharp
// File: src/Memory/Agency.Memory.Sql.Postgres/PostgresMemoryServiceCollectionExtensions.cs
using Agency.Memory.Common.Storage;
using Agency.Memory.Sql.Postgres;
using Microsoft.Extensions.DependencyInjection;

services
    .AddAgencyMemory(opts => { /* MemoryOptions */ })
    .AddAgencyEmbeddingsOpenAI(opts => { /* EmbeddingOptions */ })
    .AddAgencyMemoryPostgres("Host=localhost;Port=5432;Database=dev_db;Username=dev_user;Password=dev_password;");

// Then call MemorySchemaInitializer.InitializeAsync at application startup:
var initializer = app.Services.GetRequiredService<MemorySchemaInitializer>();
await initializer.InitializeAsync(embeddingDim: 1536);
```

`IEmbeddingGenerator` and `IOptions<MemoryOptions>` must be registered before the store is first resolved, but the relative order of the `AddX` registration calls is immaterial. `AddAgencyMemoryPostgres` registers `IMemoryStore` via a lazy factory lambda; the factory runs on first resolution of `IMemoryStore` (after `host.Build()`), not at the time the extension method is called. The code sample above is illustrative — in `Agency.Harness.Console/Program.cs`, `AddAgencyMemoryPostgres` is actually called *before* `AddAgencyMemory`.

## How It Works

### Schema

`MemorySchemaInitializer` provisions four tables and five indexes on startup:

| Table | Purpose |
|---|---|
| `records` | Primary store — one row per `Record`. PK is a `UUID`. |
| `user_state` | Holds `last_written_at` per `user_id`; drives the retrieval gate. |
| `watermarks` | Tracks the last distilled turn index per `(user_id, session_id)`. |
| `dead_letter` | Audit log for distillation/consolidation jobs that exhausted retries. |

The `records` table schema:

```sql
CREATE TABLE IF NOT EXISTS records (
    id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id          TEXT NOT NULL,
    session_id       TEXT NULL,
    content_type     SMALLINT NOT NULL,               -- ContentType enum: 0=Fact, 1=Memory
    domain           TEXT NOT NULL,
    key              TEXT NOT NULL,
    title            TEXT NOT NULL,
    value            TEXT NOT NULL,
    tags             TEXT[] NOT NULL DEFAULT '{}',
    importance       DOUBLE PRECISION NOT NULL CHECK (importance >= 0 AND importance <= 1),
    embedding        vector(<dim>) NOT NULL,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_accessed_at TIMESTAMPTZ NULL
);
```

Indexes created:

- `records_upsert_key` — functional unique index on `(user_id, COALESCE(session_id, ''), domain, key)`.
- `records_embedding_hnsw` — HNSW index on `embedding` with `vector_cosine_ops` (`m=16`, `ef_construction=64`).
- `records_user_content_type_idx` — B-tree on `(user_id, content_type)`.
- `records_user_domain_idx` — B-tree on `(user_id, domain)`.
- `records_updated_at_idx` — B-tree on `updated_at`.
- `records_last_accessed_at_idx` — partial B-tree on `last_accessed_at WHERE last_accessed_at IS NOT NULL`.

### Upsert key

The conflict target for `UpsertAsync` and `MergeAsync` is `(user_id, COALESCE(session_id, ''), domain, key)`. This treats a `NULL` `session_id` as the empty string, collapsing all user-global records with the same domain and key into a single row. Without `COALESCE`, PostgreSQL's default NULL≠NULL semantics in unique constraints would allow duplicate global records per user.

### pgvector similarity search

`SearchAsync` executes a single parameterised query using the pgvector cosine distance operator `<=>`:

```sql
SELECT ..., embedding <=> @query_vec AS distance
FROM records
WHERE user_id = @user_id [AND content_type = @content_type] [AND domain = @domain]
ORDER BY distance ASC
LIMIT @top_k;
```

The distance value is converted to similarity with `similarity = 1.0 - distance`. [[Agency.Memory.Retrieval]] is responsible for over-fetching and composite re-ranking on top of this raw similarity-ordered result.

After returning results, `SearchAsync` fires a background `Task` (fire-and-forget, using `CancellationToken.None`) to `UPDATE records SET last_accessed_at = now() WHERE id = ANY(@ids)` so that hygiene staleness checks reflect recent access without blocking the hot path.

### `LastWrittenAt` and the retrieval gate

Every write operation (`UpsertAsync`, `ForgetAsync`, `ForgetMeAsync`, `MergeAsync`, `UpdateRecordAsync`, `DeleteByIdAsync`) issues an upsert to `user_state` with `GREATEST(stored, new)` semantics, ensuring the timestamp is monotonically non-decreasing. The value is also written through to an in-memory `ConcurrentDictionary<string, DateTimeOffset>` keyed by `userId`. `LastWrittenAtAsync` returns the cached value in O(1) for the hot path and falls back to a DB read only on cold start (first call after process restart).

### TTL and hygiene operations

`DeleteWhereTtlExceededAsync` and `DeleteWhereLowImportanceStaleAsync` both accept a caller-supplied `DateTimeOffset now` parameter rather than using the database `now()` function. This makes the staleness window deterministic under a virtual `TimeProvider` clock in tests (Spec TI-4), without the predicate being affected by any skew between the application clock and the database server clock.

### Atomic merge (`MergeAsync`)

`MergeAsync` opens a single database transaction, deletes the listed record IDs (scoped to the owning `userId`), inserts the replacement record, and bumps `user_state.last_written_at` — all within one `BEGIN`/`COMMIT`. If any step fails, the transaction is rolled back. This implements the Consolidator's `Memory_Merge` tool atomicity requirement (Spec §6.3 / §8.4).

### Distillation watermarks

`WatermarkRepository` persists the last successfully distilled turn index per `(userId, sessionId)`. `AdvanceAsync` uses `GREATEST(stored, candidate)` semantics so concurrent or out-of-order completions cannot move the watermark backwards. An in-process `ConcurrentDictionary` cache provides O(1) hot-path reads; a DB read hydrates the cache on miss.

### Dead-letter audit

`DeadLetterRepository.WriteAsync` records failed distillation and consolidation jobs (job kind, JSONB payload, error message, stack trace) to the `dead_letter` table. The table is write-only for the live pipeline — no retry logic reads from it. `ListSinceAsync` is provided for operational tooling and tests.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Memory.Common]] | Defines the storage contracts this project implements — `Storage.IMemoryStore`, `Storage.IMemorySchemaInitializer`, `Storage.IWatermarkStore`, `Storage.IDeadLetterStore` — plus the `Record`, `ContentType`, `SearchQuery`, `SearchHit`, and `MemoryOptions` types they exchange |
| [[Agency.Embeddings.Common]] | Provides `IEmbeddingGenerator`; `PostgresMemoryStore` calls it to produce embeddings for records that arrive without a pre-computed vector |
| [[Agency.Memory.Retrieval]] | Calls `IMemoryStore.SearchAsync` and `LastWrittenAtAsync` on every agent iteration; depends on this project at runtime |
| [[Agency.Memory.Distiller]] | Calls `IMemoryStore.UpsertAsync` and `WatermarkRepository.AdvanceAsync` to persist extracted episodes and advance the distillation watermark |
| [[Agency.Memory.Consolidator]] | Calls `IMemoryStore.GetAllForUserAsync`, `MergeAsync`, `UpdateRecordAsync`, and `DeleteByIdAsync` during cross-session memory reconciliation |
| [[Agency.Memory.Hygiene]] | Calls `DeleteWhereTtlExceededAsync` and `DeleteWhereLowImportanceStaleAsync` on a background schedule to prune stale records |
| [[Agency.Sql.Postgres]] | Shared Postgres infrastructure (connection string helpers, test fixtures) referenced by this project |

## Design Notes

- **Why the repositories implement interfaces that live in [[Agency.Memory.Common]] rather than defining their own.** The storage contracts (`IMemoryStore`, `IMemorySchemaInitializer`, `IWatermarkStore`, `IDeadLetterStore`) were relocated into `Agency.Memory.Common.Storage`. Consumers such as the Distiller depend only on those abstractions, so a different backing store could be substituted purely through DI (`AddAgencyMemoryPostgres` binds each interface to its concrete repository). Keeping the contracts in Common — not in this provider — is what makes provider selection a configuration concern rather than a code-reference concern.

- **Why a functional unique index (`COALESCE(session_id, '')`) rather than a table-level `UNIQUE` constraint.** PostgreSQL treats `NULL != NULL` in unique constraint evaluation, so a plain `UNIQUE (user_id, session_id, domain, key)` would allow any number of rows with a `NULL` session, making the upsert conflict detection fail for user-global records. Using `COALESCE(session_id, '')` in a functional unique index maps all global records to the empty-string partition and restores the expected one-row-per-key semantics without requiring a sentinel value in the column itself. The `ON CONFLICT` clause in `UpsertAsync` mirrors the same expression exactly.

- **Why `last_written_at` is cached in-process and written through on every mutation, rather than queried from the database.** The retrieval gate (`RetrievalGate` in [[Agency.Memory.Retrieval]]) checks `LastWrittenAtAsync` on every agent iteration that might need retrieval. If this check required a database round-trip, it would add latency on the hot path — the exact problem Spec P1 ("hot path is sacred") prohibits. Writing through to `ConcurrentDictionary` on every mutation means the gate check is O(1) across all turns after the first write. The one-turn hydration penalty on cold start (process restart before any user write) is acceptable because an empty cache means no prior writes exist, and the gate returns `true` (run retrieval) only once before the cache is warm.

- **Why the hygiene methods accept a caller-supplied `now` instead of using the database `now()`.** The hygiene sweeper injects a `TimeProvider` so that unit tests can control the clock deterministically (Spec §8.5, TI-4). If the SQL predicate used `now()`, the staleness window would be evaluated against the database server clock, which tests cannot control. Passing `@now` from the application's `TimeProvider.GetUtcNow()` ensures the same virtual clock governs both the test assertion and the SQL predicate, eliminating a whole class of timing-sensitive test failures.
