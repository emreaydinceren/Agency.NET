# Agency.KeyValueStore.Sql.Postgre

#keyvaluestore #postgresql #jsonb #observability

## What It Is

Agency.KeyValueStore.Sql.Postgre is the PostgreSQL-backed implementation of [[Agency.KeyValueStore.Common]]'s `IKVStore` that stores, retrieves, and deletes typed key-value entries with optional JSONB metadata in a `kv_store` table scoped by `user_id` and `session_id`.

**Namespace:** `Agency.KeyValueStore.Sql.Postgre`

## Prerequisites

- A running PostgreSQL instance accessible via a configured [[Agency.Sql.Postgre]] `PostgreSqlRunner`
- `InitializeSchemaAsync` must be called once before first use to create the `kv_store` table and its GIN index

## API Surface

```csharp
// File: src/KeyValueStore/Agency.KeyValueStore.Sql.Postgre/PostgreKVStore.cs
using Agency.KeyValueStore.Common;
using Agency.Sql.Postgre;
using Microsoft.Extensions.Logging;

public class PostgreKVStore : IKVStore
{
    public const string ActivitySourceName = "Agency.KeyValueStore.Sql.Postgre";
    public const string MeterName = "Agency.KeyValueStore.Sql.Postgre";

    public PostgreKVStore(PostgreSqlRunner postgreSqlRunner, ILogger<PostgreKVStore> logger);

    // Creates the kv_store table and GIN index on metadata if not already present.
    // Drops legacy table shapes that lack the user_id column.
    public Task InitializeSchemaAsync(CancellationToken cancellationToken = default);

    // Inserts or updates an entry identified by (userId, sessionId, key).
    // value and metadata are serialized as JSONB. Null sessionId is stored as "*".
    public Task UpsertAsync<TValue>(
        string userId,
        string? sessionId,
        string key,
        TValue value,
        IDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default);

    // Queries entries for userId with optional session, key, value substring, and metadata containment filters.
    // Results are ordered by updated_on DESC and capped by Query.Limit (default 10).
    public Task<IReadOnlyList<SearchHit<TValue>>> SearchAsync<TValue>(
        Query query,
        CancellationToken cancellationToken = default);

    // Deletes the entry at (userId, sessionId, key). Returns true if a row was removed.
    public Task<bool> DeleteAsync(
        string userId,
        string? sessionId,
        string key,
        CancellationToken cancellationToken = default);

    // Returns metadata-only records (no value payload) for all keys belonging to userId,
    // optionally scoped to a specific sessionId.
    public Task<IReadOnlyList<SearchHit>> GetMetadataAsync(
        string userId,
        string? sessionId,
        CancellationToken cancellationToken = default);
}
```

## How It Works

1. **Schema initialization** — `InitializeSchemaAsync` runs a `DO $$ … END $$` guard that drops any existing `kv_store` table that lacks the `user_id` column (legacy schema), then issues `CREATE TABLE IF NOT EXISTS` with a composite primary key `(user_id, session_id, key)` and a GIN index on `metadata` for fast containment queries.
2. **Null session sentinel** — `null` session IDs are mapped to the literal string `"*"` before every write and mapped back to `null` on read. This allows `session_id` to participate in the `NOT NULL` primary key without requiring a surrogate key or a nullable column.
3. **Upsert** — values and metadata are serialized via `System.Text.Json` and passed as `NpgsqlDbType.Jsonb` parameters. The SQL uses `INSERT … ON CONFLICT DO UPDATE` to refresh both `value` and `metadata` in place while advancing `updated_on` to `NOW()`.
4. **Search** — boolean flag parameters (`@hasSessionId`, `@hasKey`, `@hasValue`, `@hasFilter`) enable each filter independently within a single parameterized query. Value search uses `ILIKE '%…%'` for case-insensitive substring matching; metadata search uses the JSONB containment operator `@>`.
5. **Delete** — issues a single `DELETE` by composite key and returns whether `rowsAffected > 0`.
6. **GetMetadata** — returns `SearchHit` (non-generic, no value payload) ordered by `updated_on DESC`. Session scoping is optional; passing `null` returns entries across all sessions for the user.

```csharp
using Agency.KeyValueStore.Common;
using Agency.KeyValueStore.Sql.Postgre;
using Agency.Sql.Postgre;
using Microsoft.Extensions.Logging.Abstractions;

// Construction
var runner = new PostgreSqlRunner(connectionString);
var store = new PostgreKVStore(runner, NullLogger<PostgreKVStore>.Instance);

// One-time schema setup
await store.InitializeSchemaAsync();

// Store a typed value with optional metadata
await store.UpsertAsync(
    userId: "user-42",
    sessionId: "session-1",
    key: "user.preferences",
    value: new { theme = "dark", language = "en" },
    metadata: new Dictionary<string, object> { ["source"] = "onboarding" });

// Search — session-scoped, substring value match
IReadOnlyList<SearchHit<UserPreferences>> hits = await store.SearchAsync<UserPreferences>(
    new Query(
        UserId: "user-42",
        SessionId: "session-1",
        Key: null,
        Value: "dark",
        MetadataFilter: new Dictionary<string, object> { ["source"] = "onboarding" },
        Limit: 5));

// Delete a specific entry
bool removed = await store.DeleteAsync("user-42", "session-1", "user.preferences");

// Get metadata for all sessions of a user (no value payload)
IReadOnlyList<SearchHit> metaHits = await store.GetMetadataAsync("user-42", sessionId: null);
```

## Observability

`PostgreKVStore` emits OpenTelemetry traces and metrics for every operation:

- **ActivitySource:** `Agency.KeyValueStore.Sql.Postgre`
- **Meter:** `Agency.KeyValueStore.Sql.Postgre`
- **Counter:** `kvstore.operations` — tags: `operation`, `status` (`success` / `error`)
- **Histogram:** `kvstore.duration` (ms) — tag: `operation`

Operations instrumented: `kvstore.initialize`, `kvstore.upsert`, `kvstore.search`, `kvstore.delete`, `kvstore.getMetadata`.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.KeyValueStore.Common]] | Defines `IKVStore`, `Query`, `SearchHit`, and `SearchHit<TValue>` — the contracts this project implements |
| [[Agency.Sql.Postgre]] | Supplies `PostgreSqlRunner`, which executes all DDL and DML issued by `PostgreKVStore` |
| [[Agency.KeyValueStore.Sql.Sqlite]] | Sibling SQLite implementation of the same `IKVStore` contract |

## Design Notes

- **Sentinel session ID** — PostgreSQL does not permit `NULL` in primary key columns. Mapping `null` to `"*"` preserves a simple composite PK while still modelling the concept of user-global (session-less) entries cleanly, without a nullable column or a surrogate key.
- **Schema migration guard** — the `DO $$ … END $$` block in `InitializeSchemaAsync` detects and drops only old table shapes that are structurally incompatible (missing `user_id`). This avoids requiring a separate migration script for deployments upgrading from early schema versions.
- **ILIKE substring search over vector similarity** — `SearchAsync` deliberately uses plain text substring matching rather than embedding-based similarity. This keeps the KV store dependency-free from the embeddings pipeline and suitable for exact or partial lookup patterns (e.g. retrieving stored user profile fields by value fragment).
- **Single parameterized query for all filter combinations** — rather than building SQL dynamically per filter combination, all optional predicates are gated by boolean flag parameters (`@hasSessionId`, `@hasKey`, etc.). This lets the query planner see a stable query shape and avoids SQL injection surface area from dynamic string concatenation.
