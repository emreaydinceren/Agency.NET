# Agency.GraphRAG.Code.Postgres
#graphrag #postgresql #pgvector #codegraph #observability 

## What It Is

`Agency.GraphRAG.Code.Postgres` is the PostgreSQL-backed persistence implementation that fulfils the `IGraphStore` contract for the [[Agency.GraphRAG.Code]] pipeline. It persists repositories, projects, files, modules, symbols, edges, clusters, and unresolved call sites in PostgreSQL with pgvector HNSW indexes for symbol and cluster similarity search, and ships a FluentMigrator-based schema manager that creates and upgrades the full schema automatically.

**Namespace:** `Agency.GraphRAG.Code.Postgres`

## Prerequisites

- **PostgreSQL** with the `vector` (pgvector) extension available — the `vector` and `pg_trgm` extensions are created automatically on first migration, but the server must support them.
- A running PostgreSQL instance. The local dev setup uses `docker-compose up -d` from `src/` (credentials: `dev_user` / `dev_password`, database: `dev_db`, port `5432`).
- A valid connection string passed to the `PostgresGraphStore` constructor or `PostgresMigrationRunner`.

## API Surface

### `PostgresGraphStore`

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code.Postgres/PostgresGraphStore.cs
using Agency.Embeddings.Common;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Storage;
using Agency.Sql.Postgre;
using Microsoft.Extensions.Logging;

public sealed class PostgresGraphStore : IGraphStore
{
    public const string ActivitySourceName = "Agency.GraphRAG.Code.Postgres";
    public const string MeterName         = "Agency.GraphRAG.Code.Postgres";

    // Primary constructor — explicit connection string
    public PostgresGraphStore(
        PostgreSqlRunner postgreSqlRunner,
        IEmbeddingGenerator embeddingGenerator,
        string connectionString,
        ILogger<PostgresGraphStore>? logger = null);

    // Convenience constructor — derives connectionString from postgreSqlRunner
    public PostgresGraphStore(
        PostgreSqlRunner postgreSqlRunner,
        IEmbeddingGenerator embeddingGenerator,
        ILogger<PostgresGraphStore>? logger = null);

    Task InitializeSchemaAsync(CancellationToken cancellationToken = default);

    Task UpsertRepoAsync(Repo repo, CancellationToken cancellationToken = default);
    Task UpsertProjectAsync(Project project, CancellationToken cancellationToken = default);
    Task UpsertExternalPackageBatchAsync(IReadOnlyList<ExternalPackage> packages, CancellationToken cancellationToken = default);
    Task UpsertFileAsync(SourceFile file, CancellationToken cancellationToken = default);
    Task UpsertModuleAsync(Module module, CancellationToken cancellationToken = default);
    Task UpsertSymbolAsync(Symbol symbol, CancellationToken cancellationToken = default);
    Task UpsertSymbolBatchAsync(IReadOnlyList<Symbol> symbols, CancellationToken cancellationToken = default);
    Task UpsertEdgeBatchAsync(IReadOnlyList<Edge> edges, CancellationToken cancellationToken = default);

    Task DeleteSymbolsByFileAsync(Guid fileId, CancellationToken cancellationToken = default);
    Task DeleteFileAsync(Guid fileId, CancellationToken cancellationToken = default);
    Task RenameFileAsync(Guid fileId, string newPath, CancellationToken cancellationToken = default);

    Task<string?> LoadIndexedCommitAsync(Guid repoId, CancellationToken cancellationToken = default);
    Task SetIndexedCommitAsync(Guid repoId, string indexedCommit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VectorSearchResult>> VectorSearchSymbolsAsync(string queryText, int topK, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VectorSearchResult>> VectorSearchClustersAsync(string queryText, int topK, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TraversalHop>> TraverseFromAsync(TraversalRequest request, CancellationToken cancellationToken = default);
    Task<Symbol?> GetSymbolByIdAsync(Guid symbolId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Symbol>> FindSymbolsByNameAsync(string name, CancellationToken cancellationToken = default);

    Task<SourceFile?> GetFileByPathAsync(string path, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, IReadOnlyList<Symbol>>> GetSymbolsByPathsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    Task StageUnresolvedCallSiteBatchAsync(IReadOnlyList<UnresolvedCallSite> callSites, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UnresolvedCallSite>> DrainUnresolvedCallSitesAsync(Guid? sourceFileId = null, CancellationToken cancellationToken = default);

    Task ApplyClusterAssignmentsAsync(IReadOnlyDictionary<Guid, ValueTuple<Guid, string>> assignments, CancellationToken cancellationToken = default);
    Task ReplaceClusterSummariesAtomicallyAsync(IReadOnlyList<Cluster> clusters, CancellationToken cancellationToken = default);
}
```

### `PostgresMigrationRunner`

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code.Postgres/Migrations/PostgresMigrationRunner.cs
using FluentMigrator.Runner;

public sealed class PostgresMigrationRunner
{
    public PostgresMigrationRunner(string connectionString);

    Task MigrateAsync(CancellationToken cancellationToken = default);
}
```

## How It Works

1. **Schema initialization** — `InitializeSchemaAsync()` (or a standalone `PostgresMigrationRunner`) applies pending FluentMigrator migrations. `M0001_InitialSchema` enables the `vector` extension and creates the `repos`, `projects`, `external_packages`, `files`, `modules`, `symbols`, `clusters`, `edges`, and `unresolved_call_sites` tables. `M0002_IndexesAndExtensions` enables `pg_trgm` and adds HNSW indexes on `symbols.embedding` and `clusters.embedding`, a GIN trigram index on `symbols.name`, and B-tree indexes for common graph traversal paths.

2. **Ingestion** — single-row upserts (`UpsertRepoAsync`, `UpsertProjectAsync`, `UpsertFileAsync`, `UpsertModuleAsync`, `UpsertSymbolAsync`) use `INSERT … ON CONFLICT DO UPDATE`. Batch writes (`UpsertExternalPackageBatchAsync`, `UpsertEdgeBatchAsync`, `StageUnresolvedCallSiteBatchAsync`) use Npgsql typed array parameters with `unnest(…)` to write many rows in a single round-trip. `UpsertSymbolBatchAsync` generates multi-row `VALUES` SQL.

3. **Graph traversal** — `TraverseFromAsync` accepts a `TraversalRequest` (seed IDs, hop limit 1–6, direction). It builds a recursive CTE over the `edges` table and returns `TraversalHop` records.

4. **Vector search** — `VectorSearchSymbolsAsync` and `VectorSearchClustersAsync` embed the query text via `IEmbeddingGenerator`, then run an `ORDER BY embedding <=> @queryEmbedding LIMIT @topK` query over the HNSW index. Scores are `1 − cosine_distance`.

5. **Name search** — `FindSymbolsByNameAsync` runs a UNION of exact-name match (rank 0) and `pg_trgm` similarity match (rank 1), deduplicates by ID, and orders by rank then score.

6. **File lookup** — `GetFileByPathAsync` fetches a single file by path using `SELECT * FROM files WHERE path = @path LIMIT 1`. `GetSymbolsByPathsAsync` returns symbols grouped by file path using `WHERE f.path = ANY(@paths)` — this Postgres-idiomatic array operator is more efficient than a parameterized `IN` clause and avoids SQL string concatenation.

7. **Cluster management** — `ReplaceClusterSummariesAtomicallyAsync` opens its own `NpgsqlConnection` and transaction to atomically delete all `MemberOf` edges and all `clusters`, then insert the new set with freshly generated embeddings. `ApplyClusterAssignmentsAsync` converts a symbol→cluster assignment dictionary into `MemberOf` edges and delegates to `UpsertEdgeBatchAsync`.

8. **Deferred call-site resolution** — `StageUnresolvedCallSiteBatchAsync` stages call sites that could not be resolved at index time. `DrainUnresolvedCallSitesAsync` atomically deletes and returns them (optionally scoped to a file) for the resolver pass.

```csharp
using Agency.Embeddings.Common;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Postgres;
using Agency.GraphRAG.Code.Postgres.Migrations;
using Agency.GraphRAG.Code.Storage;
using Agency.Sql.Postgre;
using Microsoft.Extensions.Logging.Abstractions;

string connectionString = "Host=localhost;Port=5432;Database=dev_db;Username=dev_user;Password=dev_password";

// 1. Run migrations
var migrationRunner = new PostgresMigrationRunner(connectionString);
await migrationRunner.MigrateAsync();

// 2. Construct the store
IEmbeddingGenerator embeddingGenerator = /* resolved from DI */;
PostgreSqlRunner sqlRunner = new(connectionString, NullLogger<PostgreSqlRunner>.Instance);
var store = new PostgresGraphStore(sqlRunner, embeddingGenerator, connectionString);

// 3. Ingest
await store.UpsertRepoAsync(new Repo { Id = Guid.NewGuid(), LocalPath = @"E:\Repos\Agency", IsShallow = false });

// 4. Query
IReadOnlyList<VectorSearchResult> hits  = await store.VectorSearchSymbolsAsync("postgres graph store", 5);
IReadOnlyList<Symbol>             named = await store.FindSymbolsByNameAsync("PostgresGraphStore");
```

## Observability

`PostgresGraphStore` wraps every public operation in an OpenTelemetry activity and records duration and operation count metrics.

- **ActivitySource name:** `Agency.GraphRAG.Code.Postgres`
- **Meter name:** `Agency.GraphRAG.Code.Postgres`
- **Counter:** `graphrag.postgres.operations` — tagged with `operation` and `status` (`ok` or `error`)
- **Histogram:** `graphrag.postgres.duration` (ms) — tagged with `operation`

Each activity is named `graphrag.postgres.{operation}`. Search and traversal methods attach additional tags such as `graphrag.search.top_k`, `graphrag.search.result_count`, `graphrag.traversal.seed_count`, `graphrag.traversal.max_hops`, and `graphrag.traversal.direction`. Failures are logged, the counter increments with `status=error`, and an `exception` event is added to the current activity.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.GraphRAG.Code]] | Defines `IGraphStore`, all domain models (`Repo`, `Project`, `Symbol`, `Edge`, `Cluster`, etc.), and query types (`TraversalRequest`, `VectorSearchResult`, `TraversalHop`). |
| [[Agency.Sql.Postgre]] | Provides `PostgreSqlRunner`, which executes all SQL statements and queries used by `PostgresGraphStore`. |
| [[Agency.Embeddings.Common]] | Provides `IEmbeddingGenerator`, which `PostgresGraphStore` calls to embed query text for vector search and to generate cluster summary embeddings. |
| [[Agency.GraphRAG.Code.Sqlite]] | Sibling `IGraphStore` implementation targeting SQLite instead of PostgreSQL. |

## Design Notes

- **Array-parameter batching** — batch write methods (`UpsertExternalPackageBatchAsync`, `UpsertEdgeBatchAsync`, `StageUnresolvedCallSiteBatchAsync`) pass typed Npgsql array parameters into `unnest(…)` queries rather than looping or constructing value lists, keeping per-row overhead near zero and eliminating N+1 round-trips.
- **Atomic cluster replacement** — `ReplaceClusterSummariesAtomicallyAsync` opens its own `NpgsqlConnection` and `NpgsqlTransaction` rather than routing through `PostgreSqlRunner` because it must interleave per-cluster embedding generation with SQL writes inside a single strict transaction scope; it rolls back the entire operation on any error to prevent partial cluster state.
- **Schema-aware migrations** — `PostgresMigrationRunner` resolves the primary schema from the connection string's `SearchPath` (or by querying `current_schema()` at connect time) and scopes the FluentMigrator version table to that schema, so migrations work correctly in multi-schema PostgreSQL deployments.
- **Dual-mode symbol lookup** — `FindSymbolsByNameAsync` combines an exact `name =` match (rank 0) with a `pg_trgm` similarity match (rank 1) in a single UNION query, giving callers both deterministic lookup and fuzzy name retrieval without a second round-trip.


