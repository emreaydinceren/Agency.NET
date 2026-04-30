# Code Indexer — Linear Implementation Plan

**Source spec:** [`code-indexer-design-spec.md`](./code-indexer-design-spec.md)
**Mode:** TDD throughout. Each component is split into a *write tests* step (red) and an *implement until tests pass* step (green). Each step is sized for a single sub-agent run.

## Implementation Plan State Tracking

Orchestrator agent should maintain a checklist [`./code-indexer-implementation-status.md`](./code-indexer-implementation-status.md) file, where as sub-agents report progress, the checklist gets updated by the orchestrator.

## How to read this document

Each step has the following shape:

- **Goal** — single sentence outcome.
- **Read first** — files to load before starting (so the sub-agent has bounded context).
- **Deliverable** — exact files to create or edit.
- **Acceptance** — the verifiable check that proves the step is done.

A sub-agent should never modify files outside its **Deliverable** list. If it discovers it needs to, it must surface that to the parent rather than expanding scope.

---

## Reuse decisions (do not re-invent)

The following existing primitives are **reused as-is** by the Code Indexer:

| Existing | Used for |
|---|---|
| `Agency.Sql.Common.SqlRunnerBase` | Telemetry-wrapped DB execution skeleton |
| `Agency.Sql.Sqlite.SqliteRunner` | All SQLite I/O for `SqliteGraphStore` |
| `Agency.Sql.Postgre.PostgreSqlRunner` | All Postgres I/O for `PostgresGraphStore` |
| `Agency.Embeddings.Common.IEmbeddingGenerator` | Embedding generation in summarizer + retriever |
| `Agency.Embeddings.OpenAI` | Default embedding provider |
| `Agency.Llm.Common` (`ILlmClient`, message types) | Summarizer + cluster summary + query LLM |
| `Agency.Common.Dataset` | Returned by raw queries when needed for ad-hoc inspection |
| `Agency.Agentic` | The summarizer and query planner are themselves agents |

The Code Indexer adds a **new** `IGraphStore` abstraction (graph-shaped, distinct from the KV-shaped `IVectorStore`) and two implementations. The KV `IVectorStore` is **not** used by the Code Indexer — its shape (userId/sessionId/key) is wrong for a property graph with polymorphic edges and recursive reachability traversal.

---

## Package layout (target)

```
src/GraphRAG.Code/
  Agency.GraphRAG.Code/                  # Domain models, IGraphStore, pipelines, query
  Agency.GraphRAG.Code.Test/             # Unit tests
  Agency.GraphRAG.Code.Sqlite/           # SqliteGraphStore (default)
  Agency.GraphRAG.Code.Sqlite.Test/      # Functional + unit tests
  Agency.GraphRAG.Code.Postgres/         # PostgresGraphStore
  Agency.GraphRAG.Code.Postgres.Test/    # Functional + unit tests
  Agency.GraphRAG.Code.TreeSitter/       # Tree-sitter sidecar host
  Agency.GraphRAG.Code.Cli/              # `index <repo>` / `query <q>` console app
  Agency.GraphRAG.Code.E2E.Test/         # End-to-end tests over a sample repo
```

---

# Phase 0 — Scaffolding

### Step 1 — Add NuGet package versions

- **Goal:** Pin all new dependencies in central package management.
- **Read first:** `src/Directory.Packages.props`, spec §5.0, §8 (Leiden), §4.2 (tree-sitter).
- **Deliverable:** Edit `src/Directory.Packages.props` to add: `FluentMigrator`, `FluentMigrator.Runner`, `FluentMigrator.Runner.SQLite`, `FluentMigrator.Runner.Postgres`, `Dapper`, `LibGit2Sharp` *(only if shell-out fallback fails — default is `git` binary)*, `Microsoft.Data.Sqlite` *(already pinned)*, `sqlite-vec` *(via `SQLitePCLRaw.bundle_e_sqlite3`)*, `LinqGraph` or `MGroup.LinearAlgebra` *(or pure-C# Leiden — see Step 91)*, `Pgvector` *(already pinned)*, `System.CommandLine` *(for CLI)*, `xunit.v3` *(already pinned)*.
- **Acceptance:** `dotnet restore src/Agency.slnx` succeeds; no version specified outside this file.

### Step 2 — Create empty project skeletons

- **Goal:** Create empty `.csproj` files for all 8 GraphRAG.Code projects with TargetFramework=net10.0.
- **Read first:** `src/Agency.slnx`, an existing csproj like `src/Sql/Agency.Sql.Sqlite/Agency.Sql.Sqlite.csproj` (for style).
- **Deliverable:** 8 empty `.csproj` files under `src/GraphRAG.Code/<name>/`, each referencing `Directory.Build.props` implicitly. Add each to `src/Agency.slnx`.
- **Acceptance:** `dotnet build src/Agency.slnx` succeeds with all 8 projects empty.

### Step 3 — Wire test-project conventions

- **Goal:** Each `*.Test` project references xUnit v3, `Microsoft.NET.Test.Sdk`, the project under test, and uses `[Trait("Category","Functional")]` for tests requiring infra.
- **Read first:** `src/VectorStore/Agency.VectorStore.Sql.Sqlite.Test/Agency.VectorStore.Sql.Sqlite.Test.csproj` for a working example.
- **Deliverable:** Update the 4 Test csprojs created in Step 2 to mirror the style.
- **Acceptance:** `dotnet test src/Agency.slnx --filter "Category!=Functional"` finds 0 tests, exits 0.

### Step 4 — Add `InternalsVisibleTo` plumbing

- **Goal:** Implementation projects expose internals to their test project per project convention.
- **Read first:** Any existing project that uses `InternalsVisibleTo` (e.g., `src/Agentic/Agency.Agentic`).
- **Deliverable:** Each non-test project with
`<ItemGroup>
  <InternalsVisibleTo Include="<TestProjectName>"" />
</ItemGroup>`
- **Acceptance:** Build succeeds.

---

# Phase 1 — Domain Models

### Step 5 — Tests for Repo / Project / File / Module / ExternalPackage records

- **Goal:** TDD the entity records described in spec §5.1.
- **Read first:** Spec §5.1 (entity tables).
- **Deliverable:** `Agency.GraphRAG.Code.Test/Domain/EntityRecordsTests.cs` covering construction, equality, `with` expressions for each record. Include nullability tests.
- **Acceptance:** Tests fail to compile (records do not yet exist).

### Step 6 — Implement entity records

- **Goal:** Make Step 5 tests green.
- **Read first:** `Agency.GraphRAG.Code.Test/Domain/EntityRecordsTests.cs`.
- **Deliverable:** `Agency.GraphRAG.Code/Domain/Repo.cs`, `Project.cs`, `SourceFile.cs` (avoid `File` collision), `Module.cs`, `ExternalPackage.cs` as `record class` types with the columns from spec §5.1.
- **Acceptance:** Step 5 tests pass; no other tests touched.

### Step 7 — Tests for Symbol record + SymbolKind enum

- **Goal:** Cover Symbol construction with kind, signature, summary, content hash, embedding, `is_utility` flag.
- **Read first:** Spec §5.1 (`symbols` row), §4.4 (chunker fields).
- **Deliverable:** `Agency.GraphRAG.Code.Test/Domain/SymbolTests.cs`.
- **Acceptance:** Compile errors (Symbol does not exist).

### Step 8 — Implement Symbol + SymbolKind

- **Goal:** Step 7 green.
- **Read first:** Test file from Step 7.
- **Deliverable:** `Agency.GraphRAG.Code/Domain/Symbol.cs` with `enum SymbolKind { Namespace, Class, Struct, Interface, Enum, Method, Function, Property, Field }` and the `Symbol` record.
- **Acceptance:** Step 7 tests pass.

### Step 9 — Tests for Edge + EdgeKind + Signal enums

- **Goal:** Cover the polymorphic edge record in spec §5.1, signal taxonomy in §6.1.
- **Read first:** Spec §5.1 (edges columns), §6.1 (signals).
- **Deliverable:** `Agency.GraphRAG.Code.Test/Domain/EdgeTests.cs` covering `EdgeKind` (`Contains`, `DependsOn`, `Imports`, `References`, `Defines`, `MemberOf`), `Signal` (`NameMatch`, `LlmExtraction`, `ExternalLikely`, `Unresolved`), confidence range, signal-set semantics.
- **Acceptance:** Compile errors.

### Step 10 — Implement Edge + EdgeKind + Signal

- **Goal:** Step 9 green.
- **Read first:** Test file from Step 9.
- **Deliverable:** `Agency.GraphRAG.Code/Domain/Edge.cs`, `EdgeKind.cs`, `Signal.cs`. Edge records carry `properties` as a strongly-typed `IReadOnlyDictionary<string, object?>` plus a typed accessor for `member_of.kind` (primary/utility).
- **Acceptance:** Step 9 tests pass.

### Step 11 — Tests for Cluster record + ClusterType enum

- **Goal:** Cover spec §8.4 cluster classification (`business`/`infrastructure`/`mixed`) and coherence score.
- **Read first:** Spec §5.1, §8.4.
- **Deliverable:** `Agency.GraphRAG.Code.Test/Domain/ClusterTests.cs`.
- **Acceptance:** Compile errors.

### Step 12 — Implement Cluster + ClusterType

- **Goal:** Step 11 green.
- **Deliverable:** `Agency.GraphRAG.Code/Domain/Cluster.cs`, `ClusterType.cs`.
- **Acceptance:** Step 11 tests pass.

### Step 13 — Tests for UnresolvedCallSite staging record

- **Goal:** Cover spec §7.2 staging-table row contract: source symbol id, identifier, scope, optional LLM-extracted target.
- **Read first:** Spec §7.2, §7.3.
- **Deliverable:** `Agency.GraphRAG.Code.Test/Domain/UnresolvedCallSiteTests.cs`.
- **Acceptance:** Compile errors.

### Step 14 — Implement UnresolvedCallSite

- **Goal:** Step 13 green.
- **Deliverable:** `Agency.GraphRAG.Code/Domain/UnresolvedCallSite.cs`.
- **Acceptance:** Step 13 tests pass.

---

# Phase 2 — IGraphStore Interface

### Step 15 — Tests for IGraphStore method-signature contract

- **Goal:** Pin down the operation surface using a contract test (`AbstractGraphStoreContract` shared by both implementations later).
- **Read first:** Spec §5.0 (operations: `UpsertSymbol`, `UpsertEdgeBatch`, `DeleteSymbolsByFile`, `VectorSearch`, `TraverseFrom`, `ApplyClusterAssignments`).
- **Deliverable:** `Agency.GraphRAG.Code.Test/Storage/IGraphStoreContractTests.cs` — abstract base class with `[Fact]` methods that operate against an injected `IGraphStore` instance. Define the full operation list as expected method calls.
- **Acceptance:** Compile errors (interface does not exist).

### Step 16 — Implement IGraphStore + supporting DTOs

- **Goal:** Define the interface so the contract from Step 15 compiles.
- **Read first:** Test file from Step 15.
- **Deliverable:**
  - `Agency.GraphRAG.Code/Storage/IGraphStore.cs` — operations: `UpsertRepoAsync`, `UpsertProjectAsync`, `UpsertExternalPackageBatchAsync`, `UpsertFileAsync`, `UpsertModuleAsync`, `UpsertSymbolAsync`, `UpsertSymbolBatchAsync`, `UpsertEdgeBatchAsync`, `DeleteSymbolsByFileAsync`, `DeleteFileAsync`, `RenameFileAsync`, `LoadIndexedCommitAsync`, `SetIndexedCommitAsync`, `VectorSearchSymbolsAsync`, `VectorSearchClustersAsync`, `TraverseFromAsync`, `GetSymbolByIdAsync`, `FindSymbolsByNameAsync`, `StageUnresolvedCallSiteBatchAsync`, `DrainUnresolvedCallSitesAsync`, `ApplyClusterAssignmentsAsync`, `ReplaceClusterSummariesAtomicallyAsync`, `InitializeSchemaAsync`.
  - `Agency.GraphRAG.Code/Storage/VectorSearchResult.cs`, `TraversalHop.cs`, `TraversalRequest.cs` records.
- **Acceptance:** Build succeeds; contract test class compiles. Tests still fail (no implementation).

---

# Phase 3 — Schema Migrations

### Step 17 — Tests for SQLite migrations runner

- **Goal:** Verify FluentMigrator can build and run against an in-memory SQLite DB and produces the expected schema.
- **Read first:** Spec §5.0 (migrations), §5.1 (tables).
- **Deliverable:** `Agency.GraphRAG.Code.Sqlite.Test/Migrations/SqliteMigrationRunnerTests.cs` — verifies tables `repos`, `projects`, `external_packages`, `files`, `modules`, `symbols`, `edges`, `clusters`, `unresolved_call_sites` exist post-migration; verifies indices listed in spec §5.2.
- **Acceptance:** Compile errors.

### Step 18 — Implement SQLite migrations (initial schema)

- **Goal:** Step 17 green.
- **Read first:** Spec §5.1, §5.2, §5.3 (SQLite column types).
- **Deliverable:**
  - `Agency.GraphRAG.Code.Sqlite/Migrations/M0001_InitialSchema.cs` — FluentMigrator migration creating all tables. Embedding column is `BLOB` for `sqlite-vec`. Confidence on `edges` is `REAL`. JSON columns stored as `TEXT`.
  - `Agency.GraphRAG.Code.Sqlite/Migrations/SqliteMigrationRunner.cs` — small wrapper that builds a `MigrationRunner` against a `SqliteRunner`'s connection string and runs migrations to latest.
  - Embedding dimension parameterized via `MigrationContext.EmbeddingDimensions` (default 1536).
- **Acceptance:** Step 17 tests pass.

### Step 19 — Tests for SQLite FTS5 + sqlite-vec virtual tables

- **Goal:** Verify FTS5 virtual table on `symbols(name)` and `sqlite-vec` virtual tables for `symbols.embedding` and `clusters.embedding` exist after migration.
- **Read first:** Spec §5.2, §5.3, `SqliteKVStore.RegisterVectorFunctions` for callback pattern.
- **Deliverable:** Add new test methods to `SqliteMigrationRunnerTests.cs`. Verify FTS5 triggers exist that keep the FTS index in sync with `symbols`.
- **Acceptance:** Tests fail.

### Step 20 — Implement SQLite FTS5 + sqlite-vec migration

- **Goal:** Step 19 green.
- **Deliverable:** `Agency.GraphRAG.Code.Sqlite/Migrations/M0002_FtsAndVec.cs` creating `symbols_fts` (FTS5), AFTER INSERT/UPDATE/DELETE triggers on `symbols`, and `symbols_vec` / `clusters_vec` (`sqlite-vec` virtual tables). Helper for loading the `sqlite-vec` extension on connection open.
- **Acceptance:** Step 19 tests pass.

### Step 21 — Tests for Postgres migrations runner

- **Goal:** Mirror Step 17 for Postgres.
- **Read first:** Spec §5.1, §5.2, §5.3 (Postgres column types).
- **Deliverable:** `Agency.GraphRAG.Code.Postgres.Test/Migrations/PostgresMigrationRunnerTests.cs` — `[Trait("Category","Functional")]` because it requires Postgres + pgvector. Use `PostgreSqlRunner` to verify schema.
- **Acceptance:** Compile errors.

### Step 22 — Implement Postgres migrations

- **Goal:** Step 21 green.
- **Deliverable:**
  - `Agency.GraphRAG.Code.Postgres/Migrations/M0001_InitialSchema.cs` creating all tables. Embedding columns use `vector(N)`; JSON columns use `jsonb`; confidence is `REAL`.
  - `M0002_IndexesAndExtensions.cs` enabling `vector` and `pg_trgm`, creating HNSW index on embeddings, GIN trigram index on `symbols(name)`, and edge indices from spec §5.2.
  - `Agency.GraphRAG.Code.Postgres/Migrations/PostgresMigrationRunner.cs` mirroring the SQLite runner.
- **Acceptance:** Step 21 tests pass when Postgres+pgvector is available.

---

# Phase 4 — SqliteGraphStore (default backend)

Each operation gets its own pair of (test, implement) steps. All tests use a shared `[Fact]` fixture that spins up an in-memory SQLite DB and runs migrations.

### Step 23 — Test fixture for SqliteGraphStore

- **Goal:** Build a reusable `SqliteGraphStoreFixture` that creates a fresh SQLite DB, registers `vec_distance_cosine` if `sqlite-vec` is unavailable, runs migrations, returns a configured `SqliteGraphStore`.
- **Read first:** `Agency.VectorStore.Sql.Sqlite.Test` for fixture patterns.
- **Deliverable:** `Agency.GraphRAG.Code.Sqlite.Test/SqliteGraphStoreFixture.cs`, `Agency.GraphRAG.Code.Sqlite.Test/FakeEmbeddingGenerator.cs` (deterministic vectors for tests).
- **Acceptance:** Compile errors (SqliteGraphStore does not exist).

### Step 24 — Implement SqliteGraphStore class skeleton + InitializeSchema

- **Goal:** Empty class with constructor (takes `SqliteRunner`, `IEmbeddingGenerator`, `ILogger<SqliteGraphStore>`), `InitializeSchemaAsync` runs migrations.
- **Read first:** `src/VectorStore/Agency.VectorStore.Sql.Sqlite/SqliteKVStore.cs` for telemetry pattern.
- **Deliverable:** `Agency.GraphRAG.Code.Sqlite/SqliteGraphStore.cs` with ActivitySource + Meter + counter/histogram following the `SqliteKVStore` pattern, `InitializeSchemaAsync` only.
- **Acceptance:** Fixture compiles and `InitializeSchemaAsync` works in tests.

### Step 25 — Tests for UpsertRepo / SetIndexedCommit / LoadIndexedCommit

- **Read first:** Spec §4.1, §5.1.
- **Deliverable:** `Agency.GraphRAG.Code.Sqlite.Test/SqliteGraphStore_Repo_Tests.cs`.
- **Acceptance:** Tests fail.

### Step 26 — Implement Repo operations on SqliteGraphStore

- **Deliverable:** Add `UpsertRepoAsync`, `SetIndexedCommitAsync`, `LoadIndexedCommitAsync` methods.
- **Acceptance:** Step 25 tests pass.

### Step 27 — Tests for UpsertProject + UpsertExternalPackageBatch

- **Read first:** Spec §4.3, §5.1.
- **Deliverable:** `Agency.GraphRAG.Code.Sqlite.Test/SqliteGraphStore_Project_Tests.cs`.
- **Acceptance:** Tests fail.

### Step 28 — Implement Project / ExternalPackage operations

- **Deliverable:** Add `UpsertProjectAsync`, `UpsertExternalPackageBatchAsync`. Use `INSERT ... ON CONFLICT (...) DO UPDATE` so calls are idempotent (spec §7.6 idempotency requirement).
- **Acceptance:** Step 27 tests pass.

### Step 29 — Tests for UpsertFile / DeleteFile / RenameFile

- **Read first:** Spec §4.6 (rename preserves symbols).
- **Deliverable:** `Agency.GraphRAG.Code.Sqlite.Test/SqliteGraphStore_File_Tests.cs` — must verify rename keeps symbol IDs and edges.
- **Acceptance:** Tests fail.

### Step 30 — Implement File operations

- **Deliverable:** Add `UpsertFileAsync`, `DeleteFileAsync` (cascade-delete owned symbols and orphan edges in a single transaction), `RenameFileAsync` (in-place path update).
- **Acceptance:** Step 29 tests pass.

### Step 31 — Tests for UpsertModule + UpsertSymbol(Batch)

- **Read first:** Spec §5.1, §4.4.
- **Deliverable:** `Agency.GraphRAG.Code.Sqlite.Test/SqliteGraphStore_Symbol_Tests.cs` — covers single + batch upserts, embedding round-trip.
- **Acceptance:** Tests fail.

### Step 32 — Implement Module + Symbol operations

- **Deliverable:** Add `UpsertModuleAsync`, `UpsertSymbolAsync`, `UpsertSymbolBatchAsync`. Embedding is serialized to `BLOB` via `sqlite-vec` helper or to `TEXT` JSON if `sqlite-vec` is unavailable (controlled by ctor flag).
- **Acceptance:** Step 31 tests pass.

### Step 33 — Tests for UpsertEdgeBatch (all 6 edge kinds)

- **Read first:** Spec §5.1, §6.1.
- **Deliverable:** `Agency.GraphRAG.Code.Sqlite.Test/SqliteGraphStore_Edge_Tests.cs` — exercises `Contains`, `Defines`, `Imports`, `DependsOn`, `References`, `MemberOf`. References edges include `confidence` and `signals` array.
- **Acceptance:** Tests fail.

### Step 34 — Implement Edge batch operations

- **Deliverable:** Add `UpsertEdgeBatchAsync` using a parameterized batch insert with `ON CONFLICT (source_id, source_kind, target_id, target_kind, edge_kind) DO UPDATE`. Signals stored as JSON text.
- **Acceptance:** Step 33 tests pass.

### Step 35 — Tests for FindSymbolsByName + GetSymbolById

- **Deliverable:** `Agency.GraphRAG.Code.Sqlite.Test/SqliteGraphStore_Lookup_Tests.cs` — exercises exact + fuzzy via FTS5.
- **Acceptance:** Tests fail.

### Step 36 — Implement symbol lookups

- **Deliverable:** `FindSymbolsByNameAsync(name, fuzzy: bool)` — exact uses `WHERE name = ?`; fuzzy uses `symbols_fts MATCH ?`. `GetSymbolByIdAsync(id)`.
- **Acceptance:** Step 35 tests pass.

### Step 37 — Tests for VectorSearchSymbols + VectorSearchClusters

- **Read first:** `SqliteKVStore.SearchAsync` (vector pattern), spec §9.2.
- **Deliverable:** `Agency.GraphRAG.Code.Sqlite.Test/SqliteGraphStore_VectorSearch_Tests.cs`.
- **Acceptance:** Tests fail.

### Step 38 — Implement vector search

- **Deliverable:** `VectorSearchSymbolsAsync(query, topK)`, `VectorSearchClustersAsync(query, topK)`. Generates embedding, runs `sqlite-vec` k-NN. Returns `VectorSearchResult` with score + symbol/cluster ID.
- **Acceptance:** Step 37 tests pass.

### Step 39 — Tests for TraverseFrom (recursive CTE, 1-3 hops)

- **Read first:** Spec §5.3 (recursive CTE depth ≤6), §9.2.
- **Deliverable:** `Agency.GraphRAG.Code.Sqlite.Test/SqliteGraphStore_Traverse_Tests.cs` — graph fixtures with known shapes; assert hop count + edge-kind filter + confidence-threshold filter behave correctly.
- **Acceptance:** Tests fail.

### Step 40 — Implement TraverseFrom

- **Deliverable:** Recursive CTE walker accepting `seedSymbolIds`, `edgeKinds`, `maxHops`, `minConfidence`, `direction` (outgoing/incoming/both).
- **Acceptance:** Step 39 tests pass.

### Step 41 — Tests for staging table operations

- **Read first:** Spec §7.2, §7.3.
- **Deliverable:** `Agency.GraphRAG.Code.Sqlite.Test/SqliteGraphStore_Staging_Tests.cs` — tests `StageUnresolvedCallSiteBatchAsync`, `DrainUnresolvedCallSitesAsync(scope)` with a per-file scope and a global scope.
- **Acceptance:** Tests fail.

### Step 42 — Implement staging table operations

- **Deliverable:** Stage = batch insert into `unresolved_call_sites`; Drain = SELECT + DELETE in a single transaction, scoped by source_file_id when specified.
- **Acceptance:** Step 41 tests pass.

### Step 43 — Tests for cluster operations

- **Read first:** Spec §8.4, §8.5.
- **Deliverable:** `Agency.GraphRAG.Code.Sqlite.Test/SqliteGraphStore_Cluster_Tests.cs` — covers `ApplyClusterAssignmentsAsync` (symbol→cluster `MEMBER_OF` edges) and `ReplaceClusterSummariesAtomicallyAsync` (replaces all clusters in one transaction).
- **Acceptance:** Tests fail.

### Step 44 — Implement cluster operations

- **Deliverable:** `ApplyClusterAssignmentsAsync` writes `MEMBER_OF` edges with `kind` property. `ReplaceClusterSummariesAtomicallyAsync` deletes existing clusters + their `MEMBER_OF` edges and inserts the new set, all inside one transaction.
- **Acceptance:** Step 43 tests pass.

### Step 45 — Run full SqliteGraphStore contract tests

- **Goal:** Wire the abstract contract from Step 15 to use the SqliteGraphStore fixture.
- **Deliverable:** `Agency.GraphRAG.Code.Sqlite.Test/SqliteGraphStoreContractTests.cs : IGraphStoreContractTests` that injects the SQLite implementation.
- **Acceptance:** All contract tests pass against SQLite.

---

# Phase 5 — PostgresGraphStore

This phase mirrors Phase 4. Each step pair creates a Postgres-equivalent test/impl. Postgres tests are `[Trait("Category","Functional")]` and require `docker-compose up`.

### Step 46 — Test fixture for PostgresGraphStore

- **Read first:** `Agency.VectorStore.Sql.Postgre.Test` for fixture patterns + `docker-compose.yml`.
- **Deliverable:** `Agency.GraphRAG.Code.Postgres.Test/PostgresGraphStoreFixture.cs` — uses a unique schema-per-test for isolation; tears down on dispose.
- **Acceptance:** Compile errors.

### Step 47 — Implement PostgresGraphStore skeleton + InitializeSchema

- **Goal:** Empty class with constructor (takes `PostgreSqlRunner`, `IEmbeddingGenerator`, `ILogger<PostgresGraphStore>`); `InitializeSchemaAsync` runs the FluentMigrator migrations from Phase 3.
- **Read first:** `src/VectorStore/Agency.VectorStore.Sql.Postgre/PostgreKVStore.cs` for telemetry pattern; `src/Sql/Agency.Sql.Postgre/PostgreSqlRunner.cs` (uses `NpgsqlDataSource` with `UseVector()`).
- **Deliverable:** `Agency.GraphRAG.Code.Postgres/PostgresGraphStore.cs` with `ActivitySourceName = "Agency.GraphRAG.Code.Postgres"`, `MeterName = "Agency.GraphRAG.Code.Postgres"`, counter `graphstore.operations`, histogram `graphstore.duration`. Only `InitializeSchemaAsync` is implemented in this step — invokes `PostgresMigrationRunner` from Step 22 with the configured embedding dimension.
- **Acceptance:** `PostgresGraphStoreFixture` from Step 46 compiles; calling `InitializeSchemaAsync` against a real Postgres database creates all tables and indexes from spec §5.1/§5.2.

### Step 48 — Tests for Postgres Repo / SetIndexedCommit / LoadIndexedCommit

- **Goal:** Cover spec §4.1's `Repo` node + indexed commit SHA persistence on Postgres.
- **Read first:** Spec §4.1, §5.1; Step 25 SQLite test as the parity template.
- **Deliverable:** `Agency.GraphRAG.Code.Postgres.Test/PostgresGraphStore_Repo_Tests.cs` `[Trait("Category","Functional")]`. Mirrors Step 25 assertions: upsert idempotency, `indexed_commit` round-trip, `is_shallow` flag, `indexed_at` timestamp populated.
- **Acceptance:** Tests fail to compile (methods do not yet exist on `PostgresGraphStore`).

### Step 49 — Implement Repo operations on PostgresGraphStore

- **Goal:** Step 48 green.
- **Read first:** Test file from Step 48; `PostgreKVStore.UpsertAsync` for the `INSERT ... ON CONFLICT (...) DO UPDATE` pattern with `NpgsqlParameter`.
- **Deliverable:** Add `UpsertRepoAsync`, `SetIndexedCommitAsync`, `LoadIndexedCommitAsync` to `PostgresGraphStore.cs`. Use `TIMESTAMPTZ` for `indexed_at` (set via `NOW()` server-side).
- **Acceptance:** Step 48 tests pass when Postgres is up.

### Step 50 — Tests for Postgres UpsertProject + UpsertExternalPackageBatch

- **Goal:** Cover spec §4.3 — projects + external packages, including `ecosystem` (nuget/npm/pypi) and `scope` (runtime/dev/peer) columns.
- **Read first:** Spec §4.3, §5.1; Step 27 SQLite test.
- **Deliverable:** `Agency.GraphRAG.Code.Postgres.Test/PostgresGraphStore_Project_Tests.cs` — mirrors Step 27 assertions. Adds a Postgres-specific case verifying batch insert of 1000 external packages completes in under one second (uses `NpgsqlBinaryImporter` if needed).
- **Acceptance:** Tests fail.

### Step 51 — Implement Project / ExternalPackage operations on PostgresGraphStore

- **Goal:** Step 50 green.
- **Read first:** Test file from Step 50.
- **Deliverable:** Add `UpsertProjectAsync` and `UpsertExternalPackageBatchAsync` to `PostgresGraphStore.cs`. Use `INSERT ... ON CONFLICT ... DO UPDATE` for idempotency. Batch insert uses a single multi-row `VALUES` statement parameterized via `Npgsql` array unnesting (`unnest(@names::text[], @versions::text[], ...)`).
- **Acceptance:** Step 50 tests pass.

### Step 52 — Tests for Postgres UpsertFile / DeleteFile / RenameFile

- **Goal:** Cover spec §4.6 — rename preserves `Symbol.id` and edges, delete cascades.
- **Read first:** Spec §4.6 ("R (renamed): update File.path in place. Preserve Symbol nodes and their edges."); Step 29 SQLite test.
- **Deliverable:** `Agency.GraphRAG.Code.Postgres.Test/PostgresGraphStore_File_Tests.cs` — explicitly asserts that after `RenameFileAsync`, `Symbol.id`s and incoming/outgoing edges remain unchanged. Asserts `DeleteFileAsync` cascades to symbols + edges in a single transaction.
- **Acceptance:** Tests fail.

### Step 53 — Implement File operations on PostgresGraphStore

- **Goal:** Step 52 green.
- **Read first:** Test file from Step 52.
- **Deliverable:** Add `UpsertFileAsync`, `DeleteFileAsync` (single transaction: `DELETE FROM symbols WHERE file_id = @id; DELETE FROM edges WHERE source_id IN (...) OR target_id IN (...); DELETE FROM files WHERE id = @id`), `RenameFileAsync` (`UPDATE files SET path = @new WHERE id = @id`). All wrapped in a `BEGIN/COMMIT` block.
- **Acceptance:** Step 52 tests pass.

### Step 54 — Tests for Postgres UpsertModule + UpsertSymbol(Batch)

- **Goal:** Cover symbol upsert with `vector(N)` embedding and `JSONB` summary structure.
- **Read first:** Spec §5.1 (`symbols` row), §5.3 (`vector(N)` via pgvector); Step 31 SQLite test.
- **Deliverable:** `Agency.GraphRAG.Code.Postgres.Test/PostgresGraphStore_Symbol_Tests.cs` — round-trips a 1536-dimensional vector through `Pgvector.Vector`; verifies cosine similarity between two known symbols matches expected value (within `1e-6`); verifies `is_utility` flag persistence; verifies batch upsert idempotency.
- **Acceptance:** Tests fail.

### Step 55 — Implement Module + Symbol operations on PostgresGraphStore

- **Goal:** Step 54 green.
- **Read first:** Test file from Step 54; `PostgreKVStore.UpsertAsync` for `vector` literal + `Pgvector.Vector` parameter handling.
- **Deliverable:** Add `UpsertModuleAsync`, `UpsertSymbolAsync`, `UpsertSymbolBatchAsync` to `PostgresGraphStore.cs`. Embeddings bound as `Pgvector.Vector`; summaries as `NpgsqlDbType.Jsonb`. Batch upsert uses `unnest(...)` array binding for ≥100 rows in one round-trip.
- **Acceptance:** Step 54 tests pass.

### Step 56 — Tests for Postgres UpsertEdgeBatch (use `jsonb` for signals)

- **Goal:** Cover all 6 edge kinds round-tripped through Postgres with `jsonb` signals + `properties`.
- **Read first:** Spec §5.1 (edges columns), §6.1 (signal taxonomy); Step 33 SQLite test.
- **Deliverable:** `Agency.GraphRAG.Code.Postgres.Test/PostgresGraphStore_Edge_Tests.cs` — exercises `Contains`, `Defines`, `Imports`, `DependsOn`, `References`, `MemberOf`. References edges include `confidence` (`REAL`) and `signals` array (`jsonb`). Verifies the `member_of.kind` ∈ {primary, utility} property survives round-trip via `properties` jsonb.
- **Acceptance:** Tests fail.

### Step 57 — Implement Edge batch operations on PostgresGraphStore (`jsonb` signals)

- **Goal:** Step 56 green.
- **Read first:** Test file from Step 56.
- **Deliverable:** Add `UpsertEdgeBatchAsync` to `PostgresGraphStore.cs`. Batched insert via `unnest(...)` for source/target/kind columns + JSONB columns. Conflict key `(source_id, source_kind, target_id, target_kind, edge_kind)` triggers `DO UPDATE SET confidence = EXCLUDED.confidence, signals = EXCLUDED.signals, properties = EXCLUDED.properties`.
- **Acceptance:** Step 56 tests pass.

### Step 58 — Tests for Postgres symbol lookups (`pg_trgm` GIN fuzzy)

- **Goal:** Cover exact lookup + trigram-based fuzzy match using the `pg_trgm` GIN index from Step 22.
- **Read first:** Spec §5.2, §5.3 ("Postgres: pg_trgm GIN index"); Step 35 SQLite test.
- **Deliverable:** `Agency.GraphRAG.Code.Postgres.Test/PostgresGraphStore_Lookup_Tests.cs` — exercises `FindSymbolsByNameAsync(exactName)`, `FindSymbolsByNameAsync(name, fuzzy: true)` returning candidates ranked by `similarity(name, @q) DESC`. Verifies `GetSymbolByIdAsync` returns null for unknown id.
- **Acceptance:** Tests fail.

### Step 59 — Implement symbol lookups on PostgresGraphStore (`pg_trgm` similarity)

- **Goal:** Step 58 green.
- **Read first:** Test file from Step 58.
- **Deliverable:** Add `FindSymbolsByNameAsync(name, fuzzy)` to `PostgresGraphStore.cs`. Exact path uses `WHERE name = @q`. Fuzzy path uses `WHERE name % @q ORDER BY similarity(name, @q) DESC LIMIT @k` (the `%` operator from `pg_trgm` triggers the GIN index). Add `GetSymbolByIdAsync(id)`.
- **Acceptance:** Step 58 tests pass.

### Step 60 — Tests for Postgres vector search (`<=>` cosine + HNSW)

- **Goal:** Cover spec §9.2 vector retrieval over `symbols.embedding` and `clusters.embedding` using HNSW + cosine.
- **Read first:** `PostgreKVStore.SearchAsync` for `<=>` cosine pattern; spec §5.2, §5.3, §9.2; Step 37 SQLite test.
- **Deliverable:** `Agency.GraphRAG.Code.Postgres.Test/PostgresGraphStore_VectorSearch_Tests.cs` — pre-loads 50 symbols with deterministic embeddings; asserts top-k by cosine distance matches expected ordering; asserts HNSW index is used (`EXPLAIN (FORMAT JSON)` contains `Index Scan using idx_vector_search`).
- **Acceptance:** Tests fail.

### Step 61 — Implement vector search on PostgresGraphStore

- **Goal:** Step 60 green.
- **Read first:** Test file from Step 60.
- **Deliverable:** Add `VectorSearchSymbolsAsync(queryText, topK, filter)` and `VectorSearchClustersAsync(queryText, topK)` to `PostgresGraphStore.cs`. Both generate the embedding via `IEmbeddingGenerator`, then `SELECT ... FROM symbols ORDER BY embedding <=> @qVector::vector LIMIT @k`. Returns `VectorSearchResult { Id, Score = 1 - distance }`.
- **Acceptance:** Step 60 tests pass.

### Step 62 — Tests for Postgres TraverseFrom (recursive CTE)

- **Goal:** Cover spec §5.3 (recursive CTE depth ≤6) + spec §9.2 (1–2 hop edge expansion).
- **Read first:** Spec §5.3, §9.2; Step 39 SQLite test.
- **Deliverable:** `Agency.GraphRAG.Code.Postgres.Test/PostgresGraphStore_Traverse_Tests.cs` — graph fixtures with known shapes (chain, fan-out, cycle); asserts hop-count limit, edge-kind filter, confidence-threshold filter, and `direction` ∈ {outgoing, incoming, both}. Includes a cycle test asserting recursion terminates.
- **Acceptance:** Tests fail.

### Step 63 — Implement TraverseFrom on PostgresGraphStore

- **Goal:** Step 62 green.
- **Read first:** Test file from Step 62.
- **Deliverable:** Add `TraverseFromAsync(seedIds, edgeKinds, maxHops, minConfidence, direction)` to `PostgresGraphStore.cs`. Uses `WITH RECURSIVE walk AS (... UNION ALL ... WHERE depth < @max AND NOT visited)` pattern. Returns `TraversalHop[]` with depth + path metadata. Cycle break via `CYCLE` clause (Postgres 14+) or visited-set in CTE.
- **Acceptance:** Step 62 tests pass.

### Step 64 — Tests for Postgres staging-table operations

- **Goal:** Cover spec §7.2/§7.3 staging of `UnresolvedCallSites` between Phase 1 and Phase 2.
- **Read first:** Spec §7.2, §7.3; Step 41 SQLite test.
- **Deliverable:** `Agency.GraphRAG.Code.Postgres.Test/PostgresGraphStore_Staging_Tests.cs` — verifies `StageUnresolvedCallSiteBatchAsync` round-trips scope + LLM-extracted target; `DrainUnresolvedCallSitesAsync(scope)` returns + deletes rows for a given source-file scope; `DrainUnresolvedCallSitesAsync(global)` drains everything; concurrent drains do not double-process rows (uses `FOR UPDATE SKIP LOCKED`).
- **Acceptance:** Tests fail.

### Step 65 — Implement staging-table operations on PostgresGraphStore

- **Goal:** Step 64 green.
- **Read first:** Test file from Step 64.
- **Deliverable:** Add `StageUnresolvedCallSiteBatchAsync` (batched `INSERT` via `unnest(...)`), `DrainUnresolvedCallSitesAsync(scope)` (single transaction: `DELETE FROM unresolved_call_sites WHERE source_file_id = @id RETURNING *` — leverages Postgres `DELETE ... RETURNING`). Add `FOR UPDATE SKIP LOCKED` only if a concurrency test is added.
- **Acceptance:** Step 64 tests pass.

### Step 66 — Tests for Postgres cluster operations

- **Goal:** Cover spec §8.4/§8.5 — `ApplyClusterAssignmentsAsync` writes `MEMBER_OF` edges with `kind` property; `ReplaceClusterSummariesAtomicallyAsync` swaps the entire cluster set in one transaction.
- **Read first:** Spec §8.4, §8.5; Step 43 SQLite test.
- **Deliverable:** `Agency.GraphRAG.Code.Postgres.Test/PostgresGraphStore_Cluster_Tests.cs` — verifies atomic replacement (mid-transaction failures roll back; readers never see a partial set), verifies `MEMBER_OF.kind` property persists for both `primary` and `utility` assignments.
- **Acceptance:** Tests fail.

### Step 67 — Implement cluster operations on PostgresGraphStore

- **Goal:** Step 66 green.
- **Read first:** Test file from Step 66.
- **Deliverable:** Add `ApplyClusterAssignmentsAsync(assignments)` (batched `MEMBER_OF` edge insert with `properties.kind`) and `ReplaceClusterSummariesAtomicallyAsync(clusters)` (single transaction: `DELETE FROM edges WHERE edge_kind = 'member_of'; DELETE FROM clusters; INSERT INTO clusters ...; INSERT INTO edges ...; COMMIT`).
- **Acceptance:** Step 66 tests pass.

> **Phase 5 dialect rules.** Each Postgres step pair mirrors the Phase 4 SQLite step pair; differences are isolated to: `vector(N)` column type via `Pgvector.Vector`, `<=>` cosine operator for similarity, `jsonb` for `signals`/`properties`/`summary`, `pg_trgm` `%` and `similarity()` for fuzzy match, `unnest(...)` for batch inserts, and `WITH RECURSIVE ... CYCLE` for traversal cycle detection.

### Step 68 — Run full PostgresGraphStore contract tests

- **Deliverable:** `PostgresGraphStoreContractTests : IGraphStoreContractTests`.
- **Acceptance:** All contract tests pass when Postgres is up.

---

# Phase 6 — Repo Walker

### Step 69 — Tests for GitProcessRunner (shells out to `git`)

- **Goal:** Cover spec §4.1's choice to shell out to git binary instead of LibGit2Sharp.
- **Read first:** Spec §4.1.
- **Deliverable:** `Agency.GraphRAG.Code.Test/Walker/GitProcessRunnerTests.cs` — tests against a temp git repo: `LsFiles`, `DiffNameStatus(from, to)`, `RevParseHead`, `IsShallowRepository`, `IsAncestor(commitA, commitB)`. Uses real `git` binary (skip if missing).
- **Acceptance:** Tests fail.

### Step 70 — Implement GitProcessRunner

- **Deliverable:** `Agency.GraphRAG.Code/Walker/GitProcessRunner.cs` — wraps `Process.Start("git", ...)` with output capture. Returns parsed `GitDiffEntry[]` with status A/M/D/R, oldPath, newPath.
- **Acceptance:** Step 69 tests pass.

### Step 71 — Tests for LanguageDetector

- **Deliverable:** `Agency.GraphRAG.Code.Test/Walker/LanguageDetectorTests.cs` — extension + shebang fallback for `.cs`, `.ts`, `.tsx`, `.js`, `.jsx`, `.py`. Returns `Language.Unknown` for unsupported.
- **Acceptance:** Tests fail.

### Step 72 — Implement LanguageDetector

- **Deliverable:** `Agency.GraphRAG.Code/Walker/Language.cs` enum + `LanguageDetector.cs`.
- **Acceptance:** Step 71 tests pass.

### Step 73 — Tests for RepoWalker (full + incremental)

- **Read first:** Spec §4.1, §4.6.
- **Deliverable:** `Agency.GraphRAG.Code.Test/Walker/RepoWalkerTests.cs` — covers: first index emits all tracked files; incremental emits A/M/D/R since stored commit; shallow-clone detection; force-push divergence triggers full re-index signal.
- **Acceptance:** Tests fail.

### Step 74 — Implement RepoWalker

- **Deliverable:** `Agency.GraphRAG.Code/Walker/RepoWalker.cs`. Returns `WalkResult { Mode = Full | Incremental | RecoveryFull, Files = WalkedFile[] }`.
- **Acceptance:** Step 73 tests pass.

---

# Phase 7 — Manifest Parser

### Step 75 — Tests for CSharpManifestParser

- **Read first:** Spec §4.3 (C# row).
- **Deliverable:** `Agency.GraphRAG.Code.Test/Manifest/CSharpManifestParserTests.cs` — fixture `.csproj`, `Directory.Packages.props`, and `<ProjectReference>` cases.
- **Acceptance:** Tests fail.

### Step 76 — Implement CSharpManifestParser

- **Deliverable:** `Agency.GraphRAG.Code/Manifest/CSharpManifestParser.cs` — parses csproj XML, extracts package refs (with versions resolved through `Directory.Packages.props` chain), and intra-repo project refs.
- **Acceptance:** Step 75 tests pass.

### Step 77 — Tests for NpmManifestParser

- **Read first:** Spec §4.3 (TypeScript/JS row).
- **Deliverable:** `Agency.GraphRAG.Code.Test/Manifest/NpmManifestParserTests.cs` — fixture `package.json` plus each lockfile flavor; pnpm workspace; npm workspace.
- **Acceptance:** Tests fail.

### Step 78 — Implement NpmManifestParser

- **Deliverable:** `Agency.GraphRAG.Code/Manifest/NpmManifestParser.cs` — JSON parsing for `package.json`; lockfile resolved-version preference (`pnpm-lock.yaml` > `package-lock.json` > `yarn.lock`).
- **Acceptance:** Step 77 tests pass.

### Step 79 — Tests for PythonManifestParser

- **Read first:** Spec §4.3 (Python row + fragmentation note).
- **Deliverable:** `Agency.GraphRAG.Code.Test/Manifest/PythonManifestParserTests.cs` — `pyproject.toml` (poetry + uv), fallback to `requirements.txt`. Verify ignored: `setup.py`, `Pipfile`, `environment.yml`.
- **Acceptance:** Tests fail.

### Step 80 — Implement PythonManifestParser

- **Deliverable:** `Agency.GraphRAG.Code/Manifest/PythonManifestParser.cs`. Use `Tomlyn` (add to `Directory.Packages.props` if missing) for TOML parsing.
- **Acceptance:** Step 79 tests pass.

### Step 81 — Tests for ManifestParserOrchestrator

- **Goal:** Spec §4.3 says manifests are re-parsed in full when changed; multi-project repos produce many `Project` nodes.
- **Deliverable:** `Agency.GraphRAG.Code.Test/Manifest/ManifestParserOrchestratorTests.cs` — emits `Project` and `ExternalPackage` records into IGraphStore via fakes; intra-repo `DEPENDS_ON` edges across projects.
- **Acceptance:** Tests fail.

### Step 82 — Implement ManifestParserOrchestrator

- **Deliverable:** `Agency.GraphRAG.Code/Manifest/ManifestParserOrchestrator.cs` — discovers manifests under repo root, dispatches to per-language parser, writes to IGraphStore.
- **Acceptance:** Step 81 tests pass.

---

# Phase 8 — Tree-sitter Sidecar

### Step 83 — Decide & document the sidecar binary

- **Goal:** Pick the sidecar implementation: a Node script using `tree-sitter` npm packages, or the Rust `tree-sitter` CLI. Capture the rationale.
- **Deliverable:** `docs/code-indexer-treesitter-sidecar.md` — short ADR. Recommendation: Node-based sidecar; bundles per-language grammars under one process; communicates via JSON over stdin/stdout. Vendored under `tools/treesitter-sidecar/`.
- **Acceptance:** Doc reviewed.

### Step 84 — Build the sidecar

- **Goal:** Produce the actual sidecar that takes JSON-line requests `{file, language, source}` and emits AST JSON.
- **Read first:** Step 83 ADR.
- **Deliverable:** `tools/treesitter-sidecar/` with `package.json`, `index.js`, language grammars for C#, TypeScript, JavaScript, Python. README with `npm install` instructions.
- **Acceptance:** `node tools/treesitter-sidecar/index.js` accepts a JSON request on stdin and emits AST JSON on stdout for each supported language.

### Step 85 — Tests for TreeSitterClient (.NET process host)

- **Read first:** Step 84 sidecar protocol; spec §4.2.
- **Deliverable:** `Agency.GraphRAG.Code.Test/TreeSitter/TreeSitterClientTests.cs` — parses small sample sources for each language, verifies node count and root kind. Skip if `node` is not on PATH.
- **Acceptance:** Tests fail.

### Step 86 — Implement TreeSitterClient

- **Deliverable:** `Agency.GraphRAG.Code.TreeSitter/TreeSitterClient.cs` — long-lived `Process`, JSON-line protocol, request multiplexing, restart on crash. Exposes `Task<ParsedFile> ParseAsync(string path, Language lang, string source)`.
- **Acceptance:** Step 85 tests pass.

### Step 87 — Tests for AST traversal helpers

- **Deliverable:** `Agency.GraphRAG.Code.Test/TreeSitter/AstTraversalTests.cs` — `FindNodesOfKind`, `GetIdentifier`, `GetSourceRange` over fixture ASTs.
- **Acceptance:** Tests fail.

### Step 88 — Implement AST traversal helpers

- **Deliverable:** `Agency.GraphRAG.Code.TreeSitter/AstNode.cs`, `AstTraversal.cs`.
- **Acceptance:** Step 87 tests pass.

---

# Phase 9 — Chunker

### Step 89 — Tests for the Chunker contract (language-agnostic interface)

- **Read first:** Spec §4.4.
- **Deliverable:** `Agency.GraphRAG.Code.Test/Chunker/ChunkerContractTests.cs` — abstract contract: emits chunks at namespace/type/member/statement granularity; chunk IDs are stable across re-runs (hash of `path + symbol_name + signature`); each chunk carries imports-in-scope and source range.
- **Acceptance:** Compile errors.

### Step 90 — Implement Chunker contract + ChunkBuilder

- **Deliverable:** `Agency.GraphRAG.Code/Chunker/IChunker.cs`, `Chunk.cs`, `ChunkBuilder.cs` (stable hashing + range tracking).
- **Acceptance:** Contract test compiles.

### Step 91 — Tests for CSharpChunker

- **Goal:** Cover spec §4.4 chunk granularity for C# (namespaces, classes, structs, interfaces, enums, methods, properties, fields) plus §4.5 inheritance/implementation extraction so the summarizer can topologically sort.
- **Read first:** Spec §4.4, §4.5; the contract test from Step 89; sample C# files under `src/Agentic/Agency.Agentic` (especially `Agent.cs` and `Tools/ToolRegistry.cs`) for realistic shapes.
- **Deliverable:** `Agency.GraphRAG.Code.Test/Chunker/CSharpChunkerTests.cs` plus fixture files under `Agency.GraphRAG.Code.Test/Chunker/Fixtures/csharp/`:
  - `Simple.cs` — single class with two methods → asserts 1 namespace chunk + 1 class chunk + 2 method chunks, parent/child links.
  - `Interface.cs` — interface + abstract class + concrete implementation → asserts the chunk metadata exposes `Inherits` / `Implements` so SummarizationOrder (Step 99) can topologically sort.
  - `Records.cs` — `record class` + primary-constructor parameters → asserts records are captured as `SymbolKind.Class` with their primary constructor as a member chunk.
  - `LargeMethod.cs` — a single method exceeding the configured `MaxChunkChars` → asserts the chunker emits statement-level sub-chunks per spec §4.4 ("Statement-level: only when a member is too large for a single embedding context").
  - `Generics.cs` — generic class + generic method → asserts type parameters appear in the captured `Signature`.
  - All fixtures additionally assert: chunk IDs are stable hashes of `(file path, fully-qualified symbol name, signature)`; `UsingsInScope` reflects the file's `using` directives; `SourceRange` start/end lines are correct.
- **Acceptance:** Tests fail to compile (`CSharpChunker` does not yet exist).

### Step 92 — Implement CSharpChunker (consumes tree-sitter AST; emits Chunks)

- **Goal:** Step 91 green; produces chunks consistent with the Step 90 contract.
- **Read first:** Test file from Step 91; `Agency.GraphRAG.Code.TreeSitter/AstNode.cs` + `AstTraversal.cs` from Step 88; spec §4.4, §4.5; `tree-sitter-c-sharp` node-kind reference (commonly: `namespace_declaration`, `class_declaration`, `struct_declaration`, `interface_declaration`, `enum_declaration`, `method_declaration`, `property_declaration`, `field_declaration`, `record_declaration`, `using_directive`, `base_list`).
- **Deliverable:** `Agency.GraphRAG.Code/Chunker/CSharpChunker.cs : IChunker` plus internal helpers in `Chunker/Internal/CSharp/` for: file-scope vs. block-scoped namespaces, base-list extraction (populates `Inherits` for the first class-type entry, `Implements` for the rest), generic type parameter rendering into `Signature`, primary-constructor parameter capture for records, and statement-level fallback when a member chunk exceeds `ChunkerOptions.MaxChunkChars` (default 6000 — pinned in `ChunkerOptions.cs`).
- **Acceptance:** Step 91 tests pass; the Phase 9 contract test passes when injected with `CSharpChunker`.

### Step 93 — Tests for TypeScriptChunker

- **Goal:** Cover spec §4.4 chunk granularity for TypeScript and JavaScript (the parser is invoked with the matching grammar per `Language`) including ESM imports, named exports, and the spec §4.5 interface/abstract-class coverage.
- **Read first:** Spec §4.4, §4.5; contract test from Step 89; the existing chunker fixture pattern from Step 91.
- **Deliverable:** `Agency.GraphRAG.Code.Test/Chunker/TypeScriptChunkerTests.cs` plus fixtures under `Agency.GraphRAG.Code.Test/Chunker/Fixtures/typescript/`:
  - `Simple.ts` — module with one class + two functions → asserts module/class/function granularity and that top-level functions are emitted as `SymbolKind.Function` (not `Method`).
  - `Interface.ts` — `interface` + `abstract class` + concrete subclass → asserts `Inherits`/`Implements` exposed on chunks for interface-first ordering.
  - `Imports.ts` — mix of `import { x } from 'pkg'`, `import * as ns from './rel'`, default imports → asserts `ImportsInScope` records package name + imported identifiers + whether the source is a relative path or a bare specifier (used by the Manifest Parser link in spec §4.3).
  - `JsxComponent.tsx` — function component + class component → asserts the `.tsx` grammar is selected and JSX elements do not break chunk boundaries.
  - `JsModule.js` — plain JS with CommonJS `require` and ES `export default` → asserts JS uses the JavaScript tree-sitter grammar and still yields stable chunk IDs.
  - All fixtures additionally assert: chunk IDs stable across re-runs; `Signature` includes TS type annotations when present; arrow-function consts assigned at module scope are captured as `SymbolKind.Function`.
- **Acceptance:** Tests fail to compile (`TypeScriptChunker` does not exist).

### Step 94 — Implement TypeScriptChunker

- **Goal:** Step 93 green.
- **Read first:** Test file from Step 93; `tree-sitter-typescript` and `tree-sitter-javascript` node-kind references (commonly: `program`, `class_declaration`, `abstract_class_declaration`, `interface_declaration`, `function_declaration`, `method_definition`, `lexical_declaration` for `const`-bound arrow functions, `import_statement`, `export_statement`, `extends_clause`, `implements_clause`).
- **Deliverable:** `Agency.GraphRAG.Code/Chunker/TypeScriptChunker.cs : IChunker` plus internal helpers in `Chunker/Internal/TypeScript/` for: dispatching to the TS vs. TSX vs. JS grammar based on `Language`, walking `import_statement` nodes into `ImportsInScope` entries (preserving bare-specifier vs. relative-path distinction so the resolver in spec §6.1 can tag `external_likely`), capturing `extends_clause` / `implements_clause` into `Inherits` / `Implements`, treating top-level arrow-function consts (`const foo = (...) => ...`) as `SymbolKind.Function`, statement-level fallback over `MaxChunkChars`.
- **Acceptance:** Step 93 tests pass; the Phase 9 contract test passes when injected with `TypeScriptChunker` for each of `Language.TypeScript`, `Language.Tsx`, `Language.JavaScript`.

### Step 95 — Tests for PythonChunker (covers ABC + Protocol per spec §4.5)

- **Goal:** Cover spec §4.4 chunk granularity for Python, plus the §4.5 V1 interface coverage requirement: `abc.ABC` subclasses and `typing.Protocol` classes must be detected as interfaces so the summarizer can topologically sort impls below them.
- **Read first:** Spec §4.4, §4.5 ("Cross-language interface coverage: ... Python ABCs (`abc.ABC`) and `Protocol` classes are V1; duck-typed conventions without structural markers are V2").
- **Deliverable:** `Agency.GraphRAG.Code.Test/Chunker/PythonChunkerTests.cs` plus fixtures under `Agency.GraphRAG.Code.Test/Chunker/Fixtures/python/`:
  - `simple.py` — module with one class + two top-level functions → asserts module/class/function granularity; class methods emitted as `SymbolKind.Method`, top-level functions as `SymbolKind.Function`.
  - `abc_interface.py` — `class IFoo(abc.ABC):` with `@abstractmethod` + concrete impl `class Foo(IFoo):` → asserts `IFoo` is captured with `SymbolKind.Interface`, `Foo` carries `Implements = ["IFoo"]`. Also covers the equivalent `class IFoo(ABC):` form when `from abc import ABC` is in scope.
  - `protocol_interface.py` — `class IFoo(Protocol):` with method stubs → asserts `IFoo` is captured with `SymbolKind.Interface` even though there is no abstract decorator; covers both `from typing import Protocol` and `from typing_extensions import Protocol`.
  - `duck_typed.py` — class `Foo` that implements the same shape as `IFoo` without inheriting → asserts the chunker does **not** emit an `Implements` link (V2 territory; assert explicitly to lock the V1 boundary).
  - `imports.py` — `import os`, `from collections import deque`, `from .helpers import bar`, `import numpy as np` → asserts `ImportsInScope` distinguishes stdlib / third-party / relative imports.
  - `large_function.py` — single function exceeding `MaxChunkChars` → asserts statement-level fallback per spec §4.4.
  - All fixtures additionally assert chunk-ID stability and `Signature` reflecting type hints when present.
- **Acceptance:** Tests fail to compile (`PythonChunker` does not exist).

### Step 96 — Implement PythonChunker

- **Goal:** Step 95 green.
- **Read first:** Test file from Step 95; `tree-sitter-python` node-kind reference (commonly: `module`, `class_definition`, `function_definition`, `decorated_definition`, `import_statement`, `import_from_statement`, `argument_list` on a class definition for base classes); spec §4.5 interface coverage rules.
- **Deliverable:** `Agency.GraphRAG.Code/Chunker/PythonChunker.cs : IChunker` plus internal helpers in `Chunker/Internal/Python/` for: walking `class_definition` base lists and resolving them through the file's `import_from_statement` aliases (so `class IFoo(abc.ABC)` and `class IFoo(ABC)` after `from abc import ABC` both yield `SymbolKind.Interface`), recognizing `Protocol` from both `typing` and `typing_extensions`, populating `ImportsInScope` with stdlib/third-party/relative tagging, statement-level fallback over `MaxChunkChars`. **Explicitly do not** emit `Implements` for duck-typed conformance — that boundary is V2 (spec §4.5).
- **Acceptance:** Step 95 tests pass; the Phase 9 contract test passes when injected with `PythonChunker`. The duck-typed test from Step 95 asserts the V1 boundary holds.

> Each language step pair uses fixture source files in `Agency.GraphRAG.Code.Test/Chunker/Fixtures/<lang>/`. Acceptance for every pair: language-specific tests pass + the Phase 9 contract test (Step 89) passes when injected with the language chunker.

### Step 97 — Tests for ChunkerDispatcher

- **Deliverable:** `Agency.GraphRAG.Code.Test/Chunker/ChunkerDispatcherTests.cs` — routes by `Language`; throws on unsupported.
- **Acceptance:** Tests fail.

### Step 98 — Implement ChunkerDispatcher

- **Deliverable:** `Agency.GraphRAG.Code/Chunker/ChunkerDispatcher.cs`.
- **Acceptance:** Step 97 tests pass.

---

# Phase 10 — Summarizer

### Step 99 — Tests for SymbolSummarizationOrder (topological by interface/inherits)

- **Read first:** Spec §4.5 (interface-first ordering).
- **Deliverable:** `Agency.GraphRAG.Code.Test/Summarizer/SummarizationOrderTests.cs` — fixture sets with interface→impl chains, abstract bases, multi-level inheritance.
- **Acceptance:** Tests fail.

### Step 100 — Implement SummarizationOrder

- **Deliverable:** `Agency.GraphRAG.Code/Summarizer/SummarizationOrder.cs` — topological sort with cycle handling (rare; warn + fall back to file order).
- **Acceptance:** Step 99 tests pass.

### Step 101 — Tests for SummaryCache (content-hash keyed)

- **Read first:** Spec §4.5 (cache by chunk content hash).
- **Deliverable:** `Agency.GraphRAG.Code.Test/Summarizer/SummaryCacheTests.cs`.
- **Acceptance:** Tests fail.

### Step 102 — Implement SummaryCache

- **Deliverable:** `Agency.GraphRAG.Code/Summarizer/SummaryCache.cs` — backed by a small SQLite or in-memory cache keyed by `(chunk_content_hash, model_tier)`.
- **Acceptance:** Step 101 tests pass.

### Step 103 — Tests for ModelTierSelector

- **Read first:** Spec §4.5 (interfaces/abstracts → strong; leaves → cheap; one-liners → cheapest).
- **Deliverable:** `Agency.GraphRAG.Code.Test/Summarizer/ModelTierSelectorTests.cs`.
- **Acceptance:** Tests fail.

### Step 104 — Implement ModelTierSelector + SummarizerOptions

- **Deliverable:** `Agency.GraphRAG.Code/Summarizer/ModelTierSelector.cs` + `SummarizerOptions.cs` (per-tier model name pulled from config).
- **Acceptance:** Step 103 tests pass.

### Step 105 — Tests for SummarizationPromptBuilder

- **Goal:** Cover prompt-templating: detailed summary template, one-liner template, interface-context-injection for impls.
- **Deliverable:** `Agency.GraphRAG.Code.Test/Summarizer/SummarizationPromptBuilderTests.cs` — golden-string assertions on rendered prompts.
- **Acceptance:** Tests fail.

### Step 106 — Implement SummarizationPromptBuilder

- **Deliverable:** `Agency.GraphRAG.Code/Summarizer/SummarizationPromptBuilder.cs`. Three templates: `OneLine`, `Detailed`, `DetailedForImplementation` (which receives parent interface summaries).
- **Acceptance:** Step 105 tests pass.

### Step 107 — Tests for SymbolSummarizer

- **Goal:** Mock `ILlmClient` + `IEmbeddingGenerator` and verify the summarizer composes order, prompt, model tier, cache, and probable-callees extraction.
- **Read first:** Spec §4.5; `Agency.Llm.Common.ILlmClient` interface.
- **Deliverable:** `Agency.GraphRAG.Code.Test/Summarizer/SymbolSummarizerTests.cs`.
- **Acceptance:** Tests fail.

### Step 108 — Implement SymbolSummarizer

- **Deliverable:** `Agency.GraphRAG.Code/Summarizer/SymbolSummarizer.cs` — produces `SymbolSummary { OneLine, Detailed, ProbableCallees, OneLineEmbedding }`. Uses `IEmbeddingGenerator` for the one-line embedding.
- **Acceptance:** Step 107 tests pass.

---

# Phase 11 — Change Detector

### Step 109 — Tests for ChangeDetector

- **Read first:** Spec §4.6.
- **Deliverable:** `Agency.GraphRAG.Code.Test/ChangeDetector/ChangeDetectorTests.cs` — feeds canned `WalkResult`s, verifies the produced `ChangeSet` (added files, modified files with per-chunk hash diffs, deleted files, renamed files preserving symbol IDs, manifest changes flagged separately).
- **Acceptance:** Tests fail.

### Step 110 — Implement ChangeDetector

- **Deliverable:** `Agency.GraphRAG.Code/ChangeDetector/ChangeDetector.cs` + `ChangeSet.cs`. Compares per-chunk content hashes against `Symbol.content_hash` from IGraphStore.
- **Acceptance:** Step 109 tests pass.

---

# Phase 12 — Reference Resolution

### Step 111 — Tests for ScopeResolver (file-local + import-reachable symbols)

- **Read first:** Spec §6 (name match within scope), §7.3 (reachability filter).
- **Deliverable:** `Agency.GraphRAG.Code.Test/References/ScopeResolverTests.cs`.
- **Acceptance:** Tests fail.

### Step 112 — Implement ScopeResolver

- **Deliverable:** `Agency.GraphRAG.Code/References/ScopeResolver.cs` — given source file ID + `IGraphStore`, returns the set of symbol IDs reachable for name resolution.
- **Acceptance:** Step 111 tests pass.

### Step 113 — Tests for ReferenceScorer (signal taxonomy)

- **Read first:** Spec §6.1 (signal taxonomy table).
- **Deliverable:** `Agency.GraphRAG.Code.Test/References/ReferenceScorerTests.cs` — covers each signal combination:
  - `name_match` only → mid confidence
  - `name_match` + `llm_extraction` → high confidence
  - `llm_extraction` only with traceable external pkg → `external_likely`
  - `llm_extraction` only without traceable pkg → `unresolved`
  - Multiple name matches → split low-confidence edges
- **Acceptance:** Tests fail.

### Step 114 — Implement ReferenceScorer

- **Deliverable:** `Agency.GraphRAG.Code/References/ReferenceScorer.cs` + `ResolutionResult.cs`. Confidence formula documented in xml-doc.
- **Acceptance:** Step 113 tests pass.

### Step 115 — Tests for ExternalPackageHeuristic

- **Read first:** Spec §6.1 (distinguishing external_likely from unresolved).
- **Deliverable:** `Agency.GraphRAG.Code.Test/References/ExternalPackageHeuristicTests.cs` — namespace-prefix matching against `ExternalPackage.name`.
- **Acceptance:** Tests fail.

### Step 116 — Implement ExternalPackageHeuristic

- **Deliverable:** `Agency.GraphRAG.Code/References/ExternalPackageHeuristic.cs`.
- **Acceptance:** Step 115 tests pass.

---

# Phase 13 — Graph Hydration

### Step 117 — Tests for Phase1Writer (definitions pass)

- **Read first:** Spec §7.2.
- **Deliverable:** `Agency.GraphRAG.Code.Test/Hydration/Phase1WriterTests.cs` — for each parsed file, writes File/Symbol/Module nodes + Contains/Defines/Imports edges + stages call sites. Verifies idempotency (re-run produces same graph).
- **Acceptance:** Tests fail.

### Step 118 — Implement Phase1Writer

- **Deliverable:** `Agency.GraphRAG.Code/Hydration/Phase1Writer.cs`.
- **Acceptance:** Step 117 tests pass.

### Step 119 — Tests for Phase2Resolver

- **Read first:** Spec §7.3.
- **Deliverable:** `Agency.GraphRAG.Code.Test/Hydration/Phase2ResolverTests.cs` — verifies reachability filter + scoring + idempotent edge inserts (use `ON CONFLICT DO UPDATE`).
- **Acceptance:** Tests fail.

### Step 120 — Implement Phase2Resolver

- **Deliverable:** `Agency.GraphRAG.Code/Hydration/Phase2Resolver.cs`. Drains staging table per source file in batched transactions.
- **Acceptance:** Step 119 tests pass.

### Step 121 — Tests for IncrementalHydrator (forward + reverse invalidation)

- **Read first:** Spec §7.4 (incremental + reverse invalidation policy).
- **Deliverable:** `Agency.GraphRAG.Code.Test/Hydration/IncrementalHydratorTests.cs` — covers all 5 spec scenarios (definitions scope, forward edge invalidation, scoped resolution, reverse invalidation, reverse resolution). Verifies pragmatic invalidation policy (rename/delete/visibility only).
- **Acceptance:** Tests fail.

### Step 122 — Implement IncrementalHydrator

- **Deliverable:** `Agency.GraphRAG.Code/Hydration/IncrementalHydrator.cs`.
- **Acceptance:** Step 121 tests pass.

### Step 123 — Tests for IndexingPipeline (top-level orchestrator)

- **Read first:** Spec §7.5 (end-to-end indexing order).
- **Deliverable:** `Agency.GraphRAG.Code.Test/Pipeline/IndexingPipelineTests.cs` — drives a small fixture repo end-to-end through Walker → ManifestParser → TreeSitter → Chunker → Summarizer → Phase1 → Phase2.
- **Acceptance:** Tests fail.

### Step 124 — Implement IndexingPipeline

- **Deliverable:** `Agency.GraphRAG.Code/Pipeline/IndexingPipeline.cs` — accepts repo path, runs full or incremental, updates `Repo.indexed_commit` at the end (spec §4.6 idempotency rule).
- **Acceptance:** Step 123 tests pass.

---

# Phase 14 — Cluster Layer

### Step 125 — Tests for EdgeWeighter (boundary-aware weights)

- **Read first:** Spec §8.2.
- **Deliverable:** `Agency.GraphRAG.Code.Test/Cluster/EdgeWeighterTests.cs` — verifies intra-namespace ×1.5, inter-project ×0.5, default = confidence; verifies `projectBoundaryMode` modes.
- **Acceptance:** Tests fail.

### Step 126 — Implement EdgeWeighter + ClusterOptions

- **Deliverable:** `Agency.GraphRAG.Code/Cluster/EdgeWeighter.cs`, `ClusterOptions.cs` carrying spec §8.2 + §8.3 config knobs with their default values.
- **Acceptance:** Step 125 tests pass.

### Step 127 — Tests for LeidenRunner (graph partitioning)

- **Read first:** Spec §8 overview; pick a Leiden implementation (recommendation: port the reference algorithm — modularity gain + local move + refine + aggregate — directly into C#; the graph sizes are ≤1M nodes which is well within tractable for an in-process implementation).
- **Deliverable:** `Agency.GraphRAG.Code.Test/Cluster/LeidenRunnerTests.cs` — known-shape graph fixtures (e.g., two cliques + bridge): asserts 2 communities; barbell graph: 2 communities; complete graph: 1 community. Resolution parameter sanity test.
- **Acceptance:** Tests fail.

### Step 128 — Implement LeidenRunner

- **Deliverable:** `Agency.GraphRAG.Code/Cluster/LeidenRunner.cs` — pure C# Leiden with `Partition Run(WeightedGraph, double resolution, int seed)`. Internal helpers in `Cluster/Internal/` for modularity gain, local move, refinement, aggregation.
- **Acceptance:** Step 127 tests pass.

### Step 129 — Tests for HierarchicalProjectSeeder

- **Read first:** Spec §8.2 (hierarchical seeding).
- **Deliverable:** `Agency.GraphRAG.Code.Test/Cluster/HierarchicalProjectSeederTests.cs` — verifies project boundaries are inviolable in `hard` mode; nested partition produced.
- **Acceptance:** Tests fail.

### Step 130 — Implement HierarchicalProjectSeeder

- **Deliverable:** `Agency.GraphRAG.Code/Cluster/HierarchicalProjectSeeder.cs`.
- **Acceptance:** Step 129 tests pass.

### Step 131 — Tests for UtilityNodeDetector (statistical + topological + convention)

- **Read first:** Spec §8.3.
- **Deliverable:** `Agency.GraphRAG.Code.Test/Cluster/UtilityNodeDetectorTests.cs` — covers each of the three signals plus the "AND" combination rule; degree percentile + floor; entropy threshold; naming hint glob match.
- **Acceptance:** Tests fail.

### Step 132 — Implement UtilityNodeDetector

- **Deliverable:** `Agency.GraphRAG.Code/Cluster/UtilityNodeDetector.cs`.
- **Acceptance:** Step 131 tests pass.

### Step 133 — Tests for TwoPassClusterer

- **Read first:** Spec §8.3 (two-pass procedure: trial Leiden → flag utility → cleaned Leiden → reassign).
- **Deliverable:** `Agency.GraphRAG.Code.Test/Cluster/TwoPassClustererTests.cs`.
- **Acceptance:** Tests fail.

### Step 134 — Implement TwoPassClusterer

- **Deliverable:** `Agency.GraphRAG.Code/Cluster/TwoPassClusterer.cs`. Wires `LeidenRunner`, `UtilityNodeDetector`, `EdgeWeighter`, `HierarchicalProjectSeeder`. Outputs a `ClusterAssignment[]` with `Kind` ∈ {primary, utility} for each MEMBER_OF edge.
- **Acceptance:** Step 133 tests pass.

### Step 135 — Tests for ClusterSummarizer + classification

- **Read first:** Spec §8.4 (two prompt templates, type classification, coherence scoring).
- **Deliverable:** `Agency.GraphRAG.Code.Test/Cluster/ClusterSummarizerTests.cs` — mocks ILlmClient; verifies primary clusters use domain prompt, utility clusters use infrastructure prompt; verifies type extracted from response; coherence stored.
- **Acceptance:** Tests fail.

### Step 136 — Implement ClusterSummarizer

- **Deliverable:** `Agency.GraphRAG.Code/Cluster/ClusterSummarizer.cs`. Embeds the summary text via `IEmbeddingGenerator`.
- **Acceptance:** Step 135 tests pass.

### Step 137 — Tests for ClusterWorker (top-level driver)

- **Read first:** Spec §8.5 (atomic replacement, scheduling).
- **Deliverable:** `Agency.GraphRAG.Code.Test/Cluster/ClusterWorkerTests.cs` — runs full pipeline; assert `ReplaceClusterSummariesAtomicallyAsync` is called once at the end.
- **Acceptance:** Tests fail.

### Step 138 — Implement ClusterWorker

- **Deliverable:** `Agency.GraphRAG.Code/Cluster/ClusterWorker.cs`. Reads graph from IGraphStore, runs `TwoPassClusterer`, summarizes, atomically commits.
- **Acceptance:** Step 137 tests pass.

### Step 139 — Tests for ClusterTuningInstrumentation

- **Read first:** Spec §8.7.
- **Deliverable:** `Agency.GraphRAG.Code.Test/Cluster/ClusterTuningInstrumentationTests.cs` — coherence distribution, mixed-cluster count, oversized-cluster flag (>200 symbols).
- **Acceptance:** Tests fail.

### Step 140 — Implement ClusterTuningInstrumentation

- **Deliverable:** `Agency.GraphRAG.Code/Cluster/ClusterTuningInstrumentation.cs`.
- **Acceptance:** Step 139 tests pass.

---

# Phase 15 — Query Pipeline

### Step 141 — Tests for QueryClassifier

- **Read first:** Spec §9.1 (5 query categories).
- **Deliverable:** `Agency.GraphRAG.Code.Test/Query/QueryClassifierTests.cs` — uses an LLM mock that returns one of `Local | Subsystem | Global | Impact | Dependency`. Verify prompt + parsing.
- **Acceptance:** Tests fail.

### Step 142 — Implement QueryClassifier

- **Deliverable:** `Agency.GraphRAG.Code/Query/QueryClassifier.cs`. Uses cheapest LLM tier.
- **Acceptance:** Step 141 tests pass.

### Step 143 — Tests for QueryPlanner (dispatches by category)

- **Read first:** Spec §9.1.
- **Deliverable:** `Agency.GraphRAG.Code.Test/Query/QueryPlannerTests.cs` — verifies each category routes to the correct retrieval strategy (Global → cluster summaries with `business`-only by default).
- **Acceptance:** Tests fail.

### Step 144 — Implement QueryPlanner

- **Deliverable:** `Agency.GraphRAG.Code/Query/QueryPlanner.cs` + `QueryPlan.cs`.
- **Acceptance:** Step 143 tests pass.

### Step 145 — Tests for HybridRetriever (vector + traversal + cluster)

- **Read first:** Spec §9.2.
- **Deliverable:** `Agency.GraphRAG.Code.Test/Query/HybridRetrieverTests.cs` — uses SqliteGraphStoreFixture; pre-populates a small graph; asserts top-k by vector + 1-2-hop expansion + cluster summary attachment.
- **Acceptance:** Tests fail.

### Step 146 — Implement HybridRetriever

- **Deliverable:** `Agency.GraphRAG.Code/Query/HybridRetriever.cs`. Composes `IGraphStore.VectorSearchSymbolsAsync` + `TraverseFromAsync`.
- **Acceptance:** Step 145 tests pass.

### Step 147 — Tests for ContextAssembler

- **Read first:** Spec §9.3.
- **Deliverable:** `Agency.GraphRAG.Code.Test/Query/ContextAssemblerTests.cs` — dedup, structural locality ordering, token budget truncation, stratified inclusion (cluster → symbol → raw).
- **Acceptance:** Tests fail.

### Step 148 — Implement ContextAssembler

- **Deliverable:** `Agency.GraphRAG.Code/Query/ContextAssembler.cs`.
- **Acceptance:** Step 147 tests pass.

### Step 149 — Tests for QueryPipeline (top-level driver)

- **Read first:** Spec §9.
- **Deliverable:** `Agency.GraphRAG.Code.Test/Query/QueryPipelineTests.cs` — wires Planner→Retriever→Assembler→LLM. End-to-end with mocked LLM verifying the assembled prompt structure.
- **Acceptance:** Tests fail.

### Step 150 — Implement QueryPipeline

- **Deliverable:** `Agency.GraphRAG.Code/Query/QueryPipeline.cs`. System prompt warns about fuzzy index + low-confidence flagging (spec §9.4).
- **Acceptance:** Step 149 tests pass.

---

# Phase 16 — CLI

### Step 151 — Tests for CLI command parsing

- **Read first:** Spec §11 V1 (`index <repo>`, `query <question>`).
- **Deliverable:** `Agency.GraphRAG.Code.Cli.Test/CommandParsingTests.cs` — uses System.CommandLine; verifies arg shape: `index <repo> [--store sqlite|postgres] [--connection ...]` and `query <q> [--store ...] [--top-k N]`.
- **Acceptance:** Tests fail.

### Step 152 — Implement CLI scaffolding

- **Deliverable:** `Agency.GraphRAG.Code.Cli/Program.cs` with System.CommandLine root + two subcommands. Wires to `IndexingPipeline` / `QueryPipeline` via DI host.
- **Acceptance:** Step 151 tests pass.

### Step 153 — Tests for CLI Storage selection

- **Deliverable:** `Agency.GraphRAG.Code.Cli.Test/StorageSelectionTests.cs` — when no `--store` flag, defaults to SQLite file in cwd. Postgres requires connection string.
- **Acceptance:** Tests fail.

### Step 154 — Implement Storage selection in CLI

- **Deliverable:** Update `Program.cs` to honor selection, default to SQLite per spec §5.0.
- **Acceptance:** Step 153 tests pass.

---

# Phase 17 — Agent Integration

### Step 155 — Tests for ICodeIndex capability

- **Read first:** Spec §12; existing `Agency.Agentic` tool patterns in `src/Agentic/Agency.Agentic/Tools`.
- **Deliverable:** `Agency.GraphRAG.Code.Test/Agentic/CodeIndexCapabilityTests.cs` — interface contract (`Task<string> AskAsync(string question, ...)`).
- **Acceptance:** Tests fail.

### Step 156 — Implement ICodeIndex + CodeIndexCapability

- **Deliverable:** `Agency.GraphRAG.Code/Agentic/ICodeIndex.cs`, `CodeIndexCapability.cs` wrapping `QueryPipeline`.
- **Acceptance:** Step 155 tests pass.

### Step 157 — Tests for CodeIndexAgentTool (Agentic ToolRegistry adapter)

- **Read first:** `src/Agentic/Agency.Agentic/Tools/ToolRegistry.cs`, `ToolDefinitionFunction.cs`.
- **Deliverable:** `Agency.GraphRAG.Code.Test/Agentic/CodeIndexAgentToolTests.cs` — verifies tool definition shape, registration, and forwarding to `ICodeIndex`.
- **Acceptance:** Tests fail.

### Step 158 — Implement CodeIndexAgentTool

- **Deliverable:** `Agency.GraphRAG.Code/Agentic/CodeIndexAgentTool.cs` matching the existing `AgentTool` pattern.
- **Acceptance:** Step 157 tests pass.

### Step 159 — DI extensions

- **Deliverable:** `Agency.GraphRAG.Code/DependencyInjection/CodeIndexServiceCollectionExtensions.cs` with `AddCodeIndex(this IServiceCollection, Action<CodeIndexOptions>)`. Picks SQLite or Postgres based on options.
- **Acceptance:** Smoke test — DI container resolves `IGraphStore` and `IndexingPipeline` end-to-end.

---

# Phase 18 — End-to-End Functional

### Step 160 — Pin the Agency repo as the E2E target (dogfood)

- **Goal:** Use this very repo as the E2E test target instead of a synthetic fixture. The Agency solution is already polyglot-adjacent enough (multi-project C# + manifests + intra-repo `<ProjectReference>` graph) and has well-known symbols around the chat-agent flow that produce verifiable query answers.
- **Read first:** `Wiki/Home.md`; `src/Agentic/Agency.Agentic/Agent.cs`, `ChatSession.cs`, `IConversationManager.cs`, `InMemoryConversationManager.cs`, `SystemPromptBuilder.cs`; `src/Agentic/Agency.Agentic.Console/Program.cs`.
- **Deliverable:**
  - `Agency.GraphRAG.Code.E2E.Test/AgencyRepoFixture.cs` — locates the repo root by walking up from `AppContext.BaseDirectory` until it finds `src/Agency.slnx`. Captures the current commit SHA at fixture init so test reruns are reproducible against the same snapshot. Provides a `string RepoRoot` and `string CommitSha` property to test classes.
  - `Agency.GraphRAG.Code.E2E.Test/AgencyRepoExpectations.cs` — single source of truth for assertion symbol names so test queries don't drift if the repo evolves. Examples: `ChatAgentSymbols = ["Agent", "ChatSession", "IConversationManager", "InMemoryConversationManager", "SystemPromptBuilder"]`, `LlmClientSymbols = ["ClaudeClient", "OpenAIClient"]`, etc. Symbols are matched by name (not by FQN) so internal namespace shuffles don't break tests.
- **Acceptance:** Fixture compiles; calling `RepoRoot` from a test returns a path containing `src/Agency.slnx`. No new files committed under `tests/fixtures/`.

### Step 161 — E2E test: full SQLite index of the Agency repo

- **Read first:** Step 160 fixture + expectations; spec §11 V1 acceptance.
- **Deliverable:** `Agency.GraphRAG.Code.E2E.Test/SqliteFullIndexTests.cs` — `[Trait("Category","Functional")]`. Indexes the Agency repo into a temp SQLite file in `Path.GetTempPath()`. Asserts:
  - `Symbol` row count is non-zero and within a sanity band (e.g., 200 ≤ count ≤ 50000).
  - Every name in `AgencyRepoExpectations.ChatAgentSymbols` resolves to at least one `Symbol` row.
  - `IConversationManager` (an interface) has at least one inbound `REFERENCES` edge from `InMemoryConversationManager` (its impl) — verifies spec §6 reference resolution end-to-end.
  - At least one `Cluster` row has `type = 'business'` (spec §8.4 classification).
  - `Repo.indexed_commit` matches the captured commit SHA from the fixture.
- **Acceptance:** Test passes locally with LM Studio configured (or mocked LLM if `AGENCY_E2E_MOCK_LLM=1`).

### Step 162 — E2E test: incremental SQLite re-index of the Agency repo

- **Deliverable:** `Agency.GraphRAG.Code.E2E.Test/SqliteIncrementalTests.cs` — runs against a `git worktree add` of the Agency repo into a temp dir so the user's working tree is never touched. Indexes once, then appends a no-op comment to a single file inside the worktree, commits, re-indexes, asserts:
  - Only the changed file's symbols had their `content_hash` updated and embeddings regenerated.
  - The unchanged-file symbol IDs and embeddings are byte-identical pre/post.
  - The temporary worktree is removed in test teardown via `git worktree remove --force`.
- **Acceptance:** Test passes; no residual worktree under the user's working tree after the run.

### Step 163 — E2E test: query pipeline answers known questions

- **Deliverable:** `Agency.GraphRAG.Code.E2E.Test/QueryPipelineTests.cs` — runs the query pipeline against the SQLite index from Step 161. Each test method asserts the answer (case-insensitive substring match) mentions specific symbols from `AgencyRepoExpectations`:
  - `"How does chat with agent work?"` → must mention at least 3 of: `Agent`, `ChatSession`, `IConversationManager`, `InMemoryConversationManager`, `SystemPromptBuilder`. Must classify as `Subsystem` query (planner output assertion).
  - `"What does Agency.Llm.Claude depend on?"` → must classify as `Dependency` query; answer must mention the `Anthropic` external package.
  - `"What calls IConversationManager?"` → must classify as `Impact` query; answer must mention `InMemoryConversationManager` and at least one consumer in `Agency.Agentic`.
  - `"Give me a tour of the codebase."` → must classify as `Global` query; answer must lead with `business` clusters and only mention infrastructure clusters in a footer (spec §9.1 Global pruning rule).
  - `"What does ChatSession.SendAsync do?"` → must classify as `Local` query; answer must reference the actual method in `ChatSession`.
- **Acceptance:** All 5 tests pass with LM Studio (or `AGENCY_E2E_MOCK_LLM=1` returning canned answers built from the retrieved context).

### Step 164 — E2E test: Postgres parity

- **Deliverable:** `Agency.GraphRAG.Code.E2E.Test/PostgresParityTests.cs` — same Agency-repo fixture and same 5 questions as Step 163, run against `PostgresGraphStore`. Asserts answer parity: each answer's mentioned-symbol set under SQLite is a subset of the mentioned-symbol set under Postgres (or vice versa) with at most one symbol of difference, to allow for HNSW vs. linear k-NN ranking divergence.
- **Acceptance:** Test passes when Postgres+pgvector is up via `docker-compose up`.

---

# Phase 19 — Documentation & Wiki

### Step 165 — Wiki pages for new projects

- **Read first:** `Wiki/Home.md`, an existing project wiki page.
- **Deliverable:** `Wiki/Agency.GraphRAG.Code.md`, `Wiki/Agency.GraphRAG.Code.Sqlite.md`, `Wiki/Agency.GraphRAG.Code.Postgres.md`, `Wiki/Agency.GraphRAG.Code.Cli.md`, plus an updated section in `Wiki/Home.md` linking them.
- **Acceptance:** Each page documents purpose, key types, configuration, and minimal usage example. Style matches existing wiki pages.

### Step 166 — README for the CLI

- **Deliverable:** `src/GraphRAG.Code/Agency.GraphRAG.Code.Cli/README.md` with the `index` and `query` examples for both SQLite and Postgres backends.
- **Acceptance:** Doc reviewed.

---

# Done criteria for V1

All of the following must hold at the end of Phase 19:

1. `dotnet build src/Agency.slnx` is warning-free across all 8 new projects.
2. `dotnet test src/Agency.slnx --filter "Category!=Functional"` passes.
3. `dotnet test src/Agency.slnx --filter "Category=Functional"` passes when LM Studio + Postgres+pgvector are up.
4. CLI dogfood — indexer can answer questions about its own host repo:
   - `dotnet run --project src/GraphRAG.Code/Agency.GraphRAG.Code.Cli -- index .` (run from the Agency repo root) produces a SQLite file in cwd.
   - Subsequent `dotnet run --project src/GraphRAG.Code/Agency.GraphRAG.Code.Cli -- query "How does chat with agent work?"` returns a coherent answer that mentions at least 3 of: `Agent`, `ChatSession`, `IConversationManager`, `InMemoryConversationManager`, `SystemPromptBuilder` (the chat-agent symbols defined in `src/Agentic/Agency.Agentic`).
   - Same pair via `--store postgres --connection "<dev_db>"` against the docker-compose Postgres also returns a coherent answer (parity with the SQLite path).
5. Spec §11 V1 scope checklist is fully ticked off.
6. Spec §10 V1 tradeoffs are honored in code.

---

# Sequencing notes for the orchestrating agent

- **Hard gates:** Phases 0 → 1 → 2 must complete sequentially. Within Phase 4 / Phase 5, individual operation pairs (test+implement) can be done in any order once the fixture step is complete.
- **Parallelization opportunities:** Phase 5 (Postgres) can run in parallel with Phase 6+ once Phase 4 is done. Phase 8 (tree-sitter sidecar) can run in parallel with Phase 6/7. Phase 14 (Cluster Layer) blocks on Phase 13 (Hydration) but can be developed in parallel up through Step 134; Step 138 (ClusterWorker) requires a working IGraphStore.
- **Fail-loud rule:** If a sub-agent's test step produces 0 failing tests instead of compile errors / red tests, treat the step as not-done — the test was probably tautological.
- **Scope guard:** A sub-agent may NOT modify any file outside its declared **Deliverable** list. Discoveries that require widening scope must be surfaced to the parent agent for re-planning.
