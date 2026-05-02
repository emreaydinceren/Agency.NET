# Agency.GraphRAG.Code.Sqlite

#graphrag #code #sqlite #fts #vectors #observability

## What It Is

`Agency.GraphRAG.Code.Sqlite` is the SQLite-backed storage provider that implements the `IGraphStore` contract for the GraphRAG code-indexing pipeline, persisting repositories, projects, files, modules, symbols, edges, clusters, and unresolved call sites in a local SQLite database with optional FTS5 full-text search and `sqlite-vec` vector search.

**Namespace:** `Agency.GraphRAG.Code.Sqlite`

## Prerequisites

- **sqlite-vec native extension** — `vec0` (or an equivalent platform-specific shared library such as `vec0.dll`, `sqlite_vec.dll`) must be present in the application's base directory or on the system library path. The extension enables the `vec0(...)` virtual tables used by `symbols_vec` and `clusters_vec`. Without it the migration still succeeds but vector tables are unavailable; the store falls back to scanning raw `BLOB` embeddings.
- **SQLite with extension loading enabled** — `Microsoft.Data.Sqlite` must be able to call `LoadExtension`. On some runtimes extension loading is compiled out; in that case vector search degrades to the fallback path.

## API Surface

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code.Sqlite/SqliteGraphStore.cs
using Agency.Embeddings.Common;
using Agency.GraphRAG.Code.Domain;
using Agency.GraphRAG.Code.Storage;
using Agency.GraphRAG.Code.Sqlite;
using Agency.GraphRAG.Code.Sqlite.Migrations;
using Agency.Sql.Sqlite;
using Microsoft.Extensions.Logging;

namespace Agency.GraphRAG.Code.Sqlite;

public sealed class SqliteGraphStore : IGraphStore
{
    public const string ActivitySourceName = "Agency.GraphRAG.Code.Sqlite";
    public const string MeterName          = "Agency.GraphRAG.Code.Sqlite";

    public SqliteGraphStore(
        SqliteRunner sqliteRunner,
        IEmbeddingGenerator embeddingGenerator,
        ILogger<SqliteGraphStore>? logger = null);

    // IGraphStore — schema lifecycle
    public Task InitializeSchemaAsync(CancellationToken cancellationToken = default);

    // IGraphStore — write operations
    public Task UpsertRepoAsync(Repo repo, CancellationToken cancellationToken = default);
    public Task UpsertProjectAsync(Project project, CancellationToken cancellationToken = default);
    public Task UpsertExternalPackageBatchAsync(IReadOnlyList<ExternalPackage> packages, CancellationToken cancellationToken = default);
    public Task UpsertFileAsync(SourceFile file, CancellationToken cancellationToken = default);
    public Task UpsertModuleAsync(Module module, CancellationToken cancellationToken = default);
    public Task UpsertSymbolAsync(Symbol symbol, CancellationToken cancellationToken = default);
    public Task UpsertSymbolBatchAsync(IReadOnlyList<Symbol> symbols, CancellationToken cancellationToken = default);
    public Task UpsertEdgeBatchAsync(IReadOnlyList<Edge> edges, CancellationToken cancellationToken = default);

    // IGraphStore — delete / rename
    public Task DeleteSymbolsByFileAsync(Guid fileId, CancellationToken cancellationToken = default);
    public Task DeleteFileAsync(Guid fileId, CancellationToken cancellationToken = default);
    public Task RenameFileAsync(Guid fileId, string newPath, CancellationToken cancellationToken = default);

    // IGraphStore — commit tracking
    public Task<string?> LoadIndexedCommitAsync(Guid repoId, CancellationToken cancellationToken = default);
    public Task SetIndexedCommitAsync(Guid repoId, string indexedCommit, CancellationToken cancellationToken = default);

    // IGraphStore — search & traversal
    public Task<IReadOnlyList<VectorSearchResult>> VectorSearchSymbolsAsync(string queryText, int topK, CancellationToken cancellationToken = default);
    public Task<IReadOnlyList<VectorSearchResult>> VectorSearchClustersAsync(string queryText, int topK, CancellationToken cancellationToken = default);
    public Task<IReadOnlyList<TraversalHop>> TraverseFromAsync(TraversalRequest request, CancellationToken cancellationToken = default);
    public Task<Symbol?> GetSymbolByIdAsync(Guid symbolId, CancellationToken cancellationToken = default);
    public Task<IReadOnlyList<Symbol>> FindSymbolsByNameAsync(string name, CancellationToken cancellationToken = default);
    public Task<SourceFile?> GetFileByPathAsync(string path, CancellationToken cancellationToken = default);
    public Task<IReadOnlyDictionary<string, IReadOnlyList<Symbol>>> GetSymbolsByPathsAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    // IGraphStore — unresolved call sites
    public Task StageUnresolvedCallSiteBatchAsync(IReadOnlyList<UnresolvedCallSite> callSites, CancellationToken cancellationToken = default);
    public Task<IReadOnlyList<UnresolvedCallSite>> DrainUnresolvedCallSitesAsync(Guid? sourceFileId = null, CancellationToken cancellationToken = default);

    // IGraphStore — clustering
    public Task ApplyClusterAssignmentsAsync(IReadOnlyDictionary<Guid, (Guid, string)> assignments, CancellationToken cancellationToken = default);
    public Task ReplaceClusterSummariesAtomicallyAsync(IReadOnlyList<Cluster> clusters, CancellationToken cancellationToken = default);
}
```

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code.Sqlite/Migrations/SqliteMigrationRunner.cs
using Agency.GraphRAG.Code.Sqlite.Migrations;
using Microsoft.Data.Sqlite;

namespace Agency.GraphRAG.Code.Sqlite.Migrations;

public sealed class SqliteMigrationRunner
{
    public SqliteMigrationRunner(string connectionString);

    /// <summary>Applies all pending FluentMigrator migrations to the target database.</summary>
    public Task MigrateToLatestAsync(MigrationContext? context = null, CancellationToken cancellationToken = default);

    /// <summary>Enables foreign keys and attempts to load the sqlite-vec extension on the given connection.</summary>
    public static void ConfigureConnection(SqliteConnection connection);
}
```

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code.Sqlite/Migrations/MigrationContext.cs
namespace Agency.GraphRAG.Code.Sqlite.Migrations;

public sealed record class MigrationContext
{
    public const int DefaultEmbeddingDimensions = 1536;

    public int EmbeddingDimensions { get; init; } = DefaultEmbeddingDimensions;
}
```

## How It Works

### Schema and migrations

`InitializeSchemaAsync()` delegates to `SqliteMigrationRunner.MigrateToLatestAsync()`, which uses FluentMigrator to apply two versioned migrations in order:

1. **`M0001_InitialSchema`** — creates nine relational tables: `repos`, `projects`, `external_packages`, `files`, `modules`, `symbols`, `edges`, `clusters`, and `unresolved_call_sites`, plus covering indexes.
2. **`M0002_FtsAndVec`** — creates:
   - `symbols_fts` — FTS5 virtual table for full-text symbol-name lookup.
   - `symbols_vec` and `clusters_vec` — `vec0(...)` virtual tables for approximate nearest-neighbour vector search, dimensioned by `MigrationContext.EmbeddingDimensions`.
   - Triggers that keep `symbols_fts` synchronised when rows are inserted, updated, or deleted from `symbols`.

`SqliteMigrationRunner.ConfigureConnection()` is the `onConnectionOpen` hook passed to [[Agency.Sql.Sqlite]] `SqliteRunner`. It enables `PRAGMA foreign_keys = ON` and attempts to load the `sqlite-vec` extension by probing a list of candidate names and paths (e.g. `vec0`, `sqlite_vec.dll`, and any `*vec*.dll` found under `AppContext.BaseDirectory`).

### Runtime write path

All mutating operations are wrapped in `RunOperationAsync(operationName, …)`, which starts an `Activity`, increments the operations counter, records the duration histogram, and propagates any exception. Batch writes (`UpsertSymbolBatchAsync`, `UpsertEdgeBatchAsync`, `UpsertExternalPackageBatchAsync`) issue a single `BEGIN IMMEDIATE; … COMMIT;` statement built from per-row parameterised fragments to minimise round trips.

### Symbol embeddings

When upserting a symbol or cluster that carries no embedding, `SqliteGraphStore` calls `IEmbeddingGenerator.GenerateEmbeddingAsync()` to compute one. For batch upserts the store resolves embeddings for all items in the batch before building the SQL.

### Symbol search

`FindSymbolsByNameAsync()` checks whether `symbols_fts` exists:
- **FTS available** — combines exact-name matches from `symbols` with FTS5 matches in a CTE, deduplicates by `rowid`, and returns results ordered by match rank then name.
- **FTS unavailable** — falls back to `WHERE name = @name`.

`GetFileByPathAsync()` returns a single file by path (`SELECT * FROM files WHERE path = @path LIMIT 1`).

`GetSymbolsByPathsAsync()` joins symbols with files using a parameterized `IN` clause to support variable-length path lists. The query dynamically builds `WHERE f.path IN (@path0, @path1, ..., @pathN)` with one parameter per path, avoiding SQL injection while handling any number of paths. Results are grouped by file path.

`VectorSearchSymbolsAsync()` and `VectorSearchClustersAsync()` first try the `symbols_vec` / `clusters_vec` virtual tables; if the tables are absent or return no results they fall back to scanning all stored `BLOB` embeddings and computing cosine distance in-process.

### Cluster replacement

`ReplaceClusterSummariesAtomicallyAsync()` atomically deletes all existing `MemberOf` edges, clears the `clusters_vec` virtual table (if present), and bulk-inserts the new cluster set inside one `BEGIN IMMEDIATE; … COMMIT;` block.

### Graph traversal

`TraverseFromAsync()` accepts a `TraversalRequest` with seed symbol IDs, max hops (1–6), direction (`Outbound` / `Inbound` / `Both`), minimum confidence, and edge-kind filters, and issues a recursive CTE over `edges` to collect `TraversalHop` results.

## Observability

`SqliteGraphStore` emits OpenTelemetry signals through static `ActivitySource` and `Meter` instances:

| Signal | Name | Unit | Description |
|---|---|---|---|
| `ActivitySource` | `Agency.GraphRAG.Code.Sqlite` | — | Distributed tracing for every store operation |
| `Meter` | `Agency.GraphRAG.Code.Sqlite` | — | Metrics namespace |
| Counter | `graphrag.sqlite.operations` | `{operation}` | Total store operations executed |
| Histogram | `graphrag.sqlite.duration` | `ms` | Duration of each store operation |

Activities are tagged with operation-specific metadata: `graphrag.repo.id`, `graphrag.project.id`, `graphrag.file.id`, `graphrag.symbol.id`, `graphrag.symbol.count`, `graphrag.search.backend` (`sqlite-vec` or `fallback`), `graphrag.search.result_count`, `graphrag.traversal.max_hops`, `graphrag.traversal.direction`, and others.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.GraphRAG.Code]] | Provides `IGraphStore`, all domain types (`Repo`, `Symbol`, `Edge`, `Cluster`, …), and `TraversalRequest` that `SqliteGraphStore` implements and consumes |
| [[Agency.Sql.Sqlite]] | `SqliteRunner` is the SQL execution engine; `SqliteMigrationRunner.ConfigureConnection` is passed as its `onConnectionOpen` hook |
| [[Agency.Embeddings.Common]] | `IEmbeddingGenerator` is injected to compute embeddings when a symbol or cluster carries none |
| [[Agency.GraphRAG.Code.Postgres]] | The PostgreSQL equivalent of this project; both implement `IGraphStore` |

## Design Notes

- **Graceful degradation** — both FTS5 and `sqlite-vec` are optional. The store detects at runtime whether `symbols_fts`, `symbols_vec`, and `clusters_vec` exist before issuing queries, falling back to exact-match and raw-blob scanning respectively. This means the database is usable even if the native extension failed to load.
- **Atomic batch writes via string-built SQL** — instead of issuing one `INSERT … ON CONFLICT` per entity, batch operations accumulate parameterised SQL fragments into a single string wrapped in `BEGIN IMMEDIATE; … COMMIT;`. This avoids per-statement round trips while keeping parameter binding safe against injection.
- **Deterministic edge IDs** — `ApplyClusterAssignmentsAsync` derives edge GUIDs from a deterministic hash of the source symbol ID, target cluster ID, and edge kind string, ensuring the same assignment produces the same ID across re-runs and making upserts idempotent.
- **AsyncLocal migration context** — `SqliteMigrationRunner` stores `MigrationContext` in an `AsyncLocal<T>` field so that FluentMigrator migrations (which are instantiated by the DI container) can read caller-supplied settings such as `EmbeddingDimensions` without requiring constructor injection into migration classes.

