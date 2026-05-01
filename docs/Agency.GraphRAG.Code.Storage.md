# Agency.GraphRAG.Code — Storage & Schema

#graphrag #storage #schema #graph-database #postgres #sqlite

The storage layer abstracts over SQLite and PostgreSQL, providing a unified interface for persisting the code graph and exposing operations for querying.

---

## IGraphStore Abstraction

All persistence goes through an `IGraphStore` interface. Two implementations ship in V1:

### PostgresGraphStore

- Backend: Postgres + `pgvector` + `pg_trgm`
- Recommended for: repos > 100k symbols, multi-developer/team scenarios, or anywhere a Postgres instance is already available
- Strengths: Multi-writer concurrency, fast HNSW vector search at scale, native JSON support

### SqliteGraphStore

- Backend: SQLite + `sqlite-vec` + FTS5
- **Default.** Zero-setup, ships as a file alongside the indexer
- Recommended for: single-developer use and repos under ~100k symbols
- Strengths: Zero configuration, file-based deployment, local development

### Interface Design

The interface exposes operations rather than queries: `UpsertSymbol`, `UpsertEdgeBatch`, `DeleteSymbolsByFile`, `VectorSearch`, `TraverseFrom`, `ApplyClusterAssignments`, etc. Query construction lives inside the implementation. Application code never sees SQL.

**Implementation strategy:**

- **Dapper** for raw SQL (not EF Core — workloads are batch inserts and recursive CTEs, not OLTP)
- Dialect differences isolated to a small set of methods (vector search, fuzzy name matching, conflict resolution syntax)
- **FluentMigrator** handles schema versioning across both stores
- **Embedding dimension is configurable** per indexer instance — default 1536 (OpenAI compatible), overridable for local models (e.g., 768 for BGE-base, 1024 for BGE-large)
- **Default selection:** when the indexer is run with no storage config, it provisions a SQLite database file in the working directory. Power users opt into Postgres via connection string config.

---

## Logical Data Model

The model is graph-shaped even though storage is relational. Nodes become rows in entity tables; edges become rows in a polymorphic `edges` table.

### Entity Tables

| Table | Key columns |
| --- | --- |
| `repos` | id, root_path, indexed_commit, indexed_at, is_shallow |
| `projects` | id, repo_id, name, manifest_path, language, ecosystem (nuget/npm/pypi) |
| `external_packages` | id, project_id, name, version, version_resolved, ecosystem, scope (runtime/dev/peer) |
| `files` | id, repo_id, project_id, path, language, content_hash, last_indexed_at |
| `modules` | id, project_id, name, path (logical grouping: namespace, package, directory) |
| `symbols` | id, file_id, module_id, name, kind, signature, summary, embedding, content_hash, is_utility |
| `clusters` | id, summary, embedding, coherence, type (`business`/`infrastructure`/`mixed`), level |

### Single Polymorphic Edge Table

| Column | Description |
| --- | --- |
| `source_id` | Foreign key — entity at edge origin |
| `source_kind` | Entity type at origin (`symbol`/`file`/`project`/etc.) |
| `target_id` | Foreign key — entity at edge target |
| `target_kind` | Entity type at target |
| `edge_kind` | One of: `contains`, `depends_on`, `imports`, `references`, `defines`, `member_of` |
| `confidence` | 0–1 score (relevant for `references` and weak `member_of`) |
| `signals` | JSON array (`name_match`, `llm_extraction`, `external_likely`, `unresolved`) |
| `properties` | JSON for edge-kind-specific fields (e.g., `member_of.kind` = primary/utility) |

**Rationale:** Polymorphic edges keep the schema simple at the cost of giving up referential integrity at the database level. The application layer is the source of truth for edge validity. For a code index where the writer is a single component (the indexer) and the schema is heavily indexed for retrieval rather than transactional updates, this is the right tradeoff.

---

## Indexes

Indexes the query workload depends on:

- `symbols(file_id)`, `symbols(module_id)` — scoped lookups during reference resolution and incremental updates
- `symbols(name)` — name match during reference resolution
- Trigram index on `symbols(name)` (Postgres: `pg_trgm`; SQLite: FTS5) — fuzzy name matching
- HNSW vector index on `symbols(embedding)` and `clusters(embedding)` (Postgres: `pgvector`; SQLite: `sqlite-vec`)
- `edges(source_id, edge_kind)`, `edges(target_id, edge_kind)` — bidirectional traversal
- `edges(edge_kind, confidence)` — confidence-filtered queries

---

## Postgres vs. SQLite: Where They Differ

| Concern | Postgres | SQLite |
| --- | --- | --- |
| Vector type | `vector(N)` via `pgvector` | `BLOB` queried via `sqlite-vec` virtual table |
| Vector index | HNSW, IVFFlat | k-NN via `sqlite-vec` (no HNSW yet — improving) |
| Fuzzy name match | `pg_trgm` GIN index | FTS5 virtual table |
| JSON | `jsonb` | `JSON` (text) with `json_extract` |
| Concurrency | Multi-writer | Single-writer (WAL mode allows concurrent reads) |
| Recursive CTE | Yes, fast on deep | Yes, fine for typical depths (≤6 hops) |
| Conflict resolution | `ON CONFLICT (...) DO UPDATE` | `ON CONFLICT (...) DO UPDATE` |
| Deployment | Server | File |

**Key insight:** The interface hides all of this. Implementation differences are isolated to roughly six methods.

---

## Performance Ceiling

For a repo of 100k symbols with ~500k edges (typical mid-sized service):

- **SQLite:** full index in single-digit minutes after summarization, query latency for 3-hop traversals well under 50ms
- **Postgres:** similar at this scale; pulls ahead at 1M+ symbols where HNSW vector search dominates

For very large repos (5M+ symbols, monorepos), Postgres is the only sensible choice.

---

## Reference Resolution (The Fuzzy Call Graph)

Without a language server, `REFERENCES` edges are inferred, not resolved.

### Resolution Strategy

The Manifest Parser does the first cut: any import resolved to an `ExternalPackage` short-circuits — no intra-repo name matching attempted. For everything left, three signals are combined:

1. **Name match within scope** — for each call-site identifier in a symbol's body, find candidate definitions reachable via the file's intra-repo imports/usings. Single match → high confidence. Multiple matches → split into low-confidence edges.
2. **LLM-extracted call targets** — the summarizer reports probable callees. Used as a second signal; agreement with (1) raises confidence, disagreement flags ambiguity.
3. **Heuristics** — receiver type hints from variable names (`var user = ...; user.Save()` likely targets a `User`-related `Save`). Optional, V2.

### Signal Taxonomy

Each `REFERENCES` edge carries a `confidence` score and a `signals` array drawn from a small enum:

| Signal | Meaning |
| --- | --- |
| `name_match` | Identifier resolved to one or more in-scope `Symbol` nodes |
| `llm_extraction` | Summarizer's probable-callees output named this target |
| `external_likely` | LLM extracted a callee with no name match, but the call site's surrounding imports reference a known `ExternalPackage` — likely a framework call (`services.AddScoped`, `Console.WriteLine`, `pd.read_csv`) into code we don't index |
| `unresolved` | LLM extracted a callee with no name match and no plausible external package — could be dynamic dispatch, reflection, or hallucination. Lowest-confidence bucket. |

**Composability:** Multiple signals can coexist on one edge. `["name_match", "llm_extraction"]` is the high-confidence agreement case. `["external_likely"]` flows into Dependency queries — "what frameworks does this code call into" becomes answerable. `["unresolved"]` edges are filtered out by default at query time but kept in the graph for diagnostic queries.

**Distinguishing external_likely from unresolved:** if the call site's receiver, surrounding usings/imports, or LLM-named callee can be plausibly traced to a known `ExternalPackage` for the source file's `Project`, tag `external_likely`. Otherwise `unresolved`. Exact heuristic is implementation tuning; a reasonable starting point is namespace-prefix matching against package names.

---

## See Also

- [[Agency.GraphRAG.Code.Hydration]] — how two-phase indexing populates these tables
- [[Agency.GraphRAG.Code.Design]] — design decisions around storage and schema choices
