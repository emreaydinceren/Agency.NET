# Code Indexer — High-Level Design Spec

**Status:** Draft v0.9
**Owner:** Emre
**Target stack:** C# / .NET, Postgres or SQLite (user choice), tree-sitter, LLM provider (configurable)
**Positioning:** GraphRAG-over-code module, slots into the Agency toolkit as `Agency.GraphRAG.Code`

---

## 1. Goal

Enable an LLM agent to answer questions about a large, possibly polyglot code repository **quickly and accurately**, including questions about structure, dependencies, and call relationships — without requiring the code to compile and without depending on language servers.

### Example queries the system must serve

- "How does authentication work in this repo?"
- "What modules depend on the payments service?"
- "What would break if I change the signature of `UserService.Authenticate`?"
- "Give me a tour of the codebase."
- "Where is feature X implemented?"

### Non-goals (V1)

- Precise refactoring (rename-symbol, find-all-references with zero false positives)
- Real-time editor integration (LSP-style)
- Build-aware analysis (errors, type-checking)

---

## 2. Design Principles

1. **Build-independent.** The indexer must work on broken, half-written, or partially-checked-in code.
2. **Polyglot from day one.** Tree-sitter for everything; no per-language hand-coded analyzers.
3. **Honest fuzziness.** Where symbol resolution is ambiguous, surface confidence scores rather than fake certainty.
4. **Hybrid retrieval.** Combine vector search, graph traversal, and pre-computed cluster summaries — no single signal is enough.
5. **Incremental where it matters, batch where it doesn't.** File-level updates are incremental; cluster summaries rebuild on a schedule.

---

## 3. Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                        Indexing Pipeline                         │
│                                                                  │
│  Repo Walker ─┬─▶ Tree-sitter Parser ──▶ Chunker ──▶ Summarizer │
│       │       │                                          │       │
│       │       └─▶ Manifest Parser ──────────────────────┤       │
│       │           (csproj, package.json, pyproject.toml) │       │
│       └──▶ Change Detector ──────────────────────────────┤       │
│                                                          ▼       │
│                                                IGraphStore Writer│
└──────────────────────────────────────────────────────────┬──────┘
                                                           │
                                                           ▼
                                  ┌────────────────────────────────┐
                                  │   IGraphStore (abstraction)    │
                                  │  ┌──────────┐    ┌──────────┐  │
                                  │  │ Postgres │ OR │  SQLite  │  │
                                  │  │ pgvector │    │sqlite-vec│  │
                                  │  └──────────┘    └──────────┘  │
                                  └────────────────┬───────────────┘
                                                   │
                                                   ▼
                            ┌─────────────────────────────────────┐
                            │    Background Cluster Worker        │
                            │ (Leiden in-process + LLM summaries) │
                            └─────────────────────────────────────┘
                                                   │
┌──────────────────────────────────────────────────┴──────────────┐
│                          Query Pipeline                          │
│                                                                  │
│  Question ──▶ Query Planner ──▶ Hybrid Retriever ──▶ Context     │
│                                  (vector + graph    Assembler    │
│                                   + cluster)                │    │
│                                                             ▼    │
│                                                    LLM Synthesis │
└──────────────────────────────────────────────────────────────────┘
```

---

## 4. Indexing Pipeline

### 4.1 Repo Walker (Git-aware)

- Operates on a git working tree. The repo's last indexed commit SHA is stored on a `Repo` node in the graph.
- **First index:** enumerates all tracked files via `git ls-files`, respecting `.gitignore` automatically. Applies a configurable allow/deny list on top.
- **Incremental index:** runs `git diff <indexedCommit> HEAD --name-status -M` to get the exact set of added (`A`), modified (`M`), deleted (`D`), and renamed (`R`) files. No filesystem scan needed.
- Detects language by extension + shebang/heuristic fallback.
- **Implementation note:** shells out to the `git` binary rather than using LibGit2Sharp. More reliable for diff/log operations, matches how other indexers do it, and avoids native-binding maintenance.
- **V1 constraint:** indexes committed state only. Users with uncommitted changes are expected to commit (or stash) before re-indexing. Working-tree overlay is a V2 consideration.

### 4.2 Tree-sitter Parser

- Tree-sitter runs **out-of-process** (Node sidecar or Rust CLI piped via JSON). Avoids the maintenance burden of TreeSitterSharp and keeps grammar updates decoupled from the .NET build.
- Parser receives a file, returns a structured AST as JSON.
- Grammars loaded dynamically per detected language.

### 4.3 Manifest Parser

Parses dependency manifests in parallel with code parsing. Establishes project boundaries and the first-party / third-party divide that downstream stages rely on.

**Supported manifests (V1):**

| Language | Primary | Lockfile | Fallback |
|---|---|---|---|
| C# | `*.csproj`, `*.sln`, `Directory.Packages.props` | — (NuGet resolves at build) | — |
| TypeScript/JS | `package.json` | `package-lock.json`, `yarn.lock`, `pnpm-lock.yaml` | — |
| Python | `pyproject.toml` | `poetry.lock`, `uv.lock` | `requirements.txt` |

**What it extracts:**
- Project name and root path
- Direct dependencies → `ExternalPackage` nodes with version (from manifest)
- Resolved versions from lockfile (preferred when present)
- Intra-repo project references (C# `<ProjectReference>`, npm/pnpm workspaces, Python path-based deps)
- Workspace/monorepo membership

**Behavioral rules:**
- Manifests are **re-parsed in full** when changed — no chunk-level diffing. They're small and structural; cheap to reconcile.
- A repo can have many manifests (multi-project solutions, pnpm workspaces, Python monorepos). Each becomes a `Project` node.
- **External packages are tracked as opaque nodes by default.** The indexer does not recurse into `node_modules`, `~/.nuget`, or site-packages. Indexing third-party code is a V2 opt-in for curated packages (e.g., your company's internal libraries).
- Python's manifest fragmentation is accepted as a known limitation: parse `pyproject.toml` first, fall back to `requirements.txt`, ignore `setup.py` / `setup.cfg` / `Pipfile` / `environment.yml` in V1.

**Why this matters for resolution:**
When the Chunker captures `import { foo } from 'lodash'`, the Manifest Parser has already established that `lodash` is an external package for this project. The reference resolver tags the `IMPORTS` edge as external and skips intra-repo name matching — eliminating a major source of false `REFERENCES` edges and saving resolution work.

### 4.4 Chunker

Splits the AST into **semantically meaningful blocks**:

- **Top-level:** namespaces, modules, files
- **Type-level:** classes, structs, interfaces, enums
- **Member-level:** methods, functions, properties
- **Statement-level:** only when a member is too large for a single embedding context

Each chunk carries:
- Stable ID (hash of file path + symbol name + signature)
- Source range (file, start/end line)
- Symbol kind, name, signature
- Imports/usings in scope
- Raw text

### 4.5 Summarizer

For each symbol-level chunk, an LLM produces:
- **One-line purpose** (used for embedding)
- **Detailed summary** (inputs, outputs, side effects, key calls — used for retrieval context)
- **Probable call targets** (extracted heuristically — names of methods/functions invoked)

**Interface-first ordering.** Interfaces and abstract classes are summarized *before* their implementations. Two reasons:

1. **Cardinality.** A bad summary on `IRepository` contaminates retrieval for every concrete repository in the codebase — the connective tissue of the graph deserves higher-quality summaries than ordinary leaves.
2. **Context propagation.** Implementation summaries are dramatically better when the prompt includes the interface's intent. "This is an implementation of `IPaymentProcessor` for Stripe" beats parsing the implementation cold every time.

Tree-sitter exposes inheritance and implementation relationships structurally, so the dependency order is computable: topologically sort symbols by `implements` / `inherits-from`, summarize roots first, propagate summaries down as context.

**Tiered models.** Interfaces, abstract classes, and public-API types use a stronger model. Concrete implementations and internal helpers use a cheaper model. One-line embedding summaries use the cheapest tier. Configurable per-tier model selection.

**Cross-language interface coverage:** C# `interface` and `abstract class`; TypeScript `interface` and `abstract class`. Python ABCs (`abc.ABC`) and `Protocol` classes are V1; duck-typed conventions without structural markers are V2.

**Cost mitigations:**
- Cache summaries keyed by chunk content hash (unchanged code → no re-summarization)
- Summarize file-level and class-level only; lazily summarize methods on first query (optional V1 toggle)
- Use cheaper model for one-liners and concrete leaves; reserve stronger model for interfaces, abstracts, and public APIs

### 4.6 Change Detector

Driven entirely by git diff output from the Repo Walker. Per file status:

- **`A` (added):** parse → chunk → summarize → insert `File`, `Symbol` nodes and `CONTAINS`, `DEFINES`, `IMPORTS` edges.
- **`M` (modified):** re-parse the file, hash each chunk, compare against existing `Symbol.contentHash` values. Only chunks with changed hashes are re-summarized. Edges sourcing from changed symbols are invalidated and re-resolved.
- **`D` (deleted):** remove the `File` node, cascade delete owned `Symbol` nodes, and clean up dangling edges.
- **`R` (renamed):** update `File.path` in place. **Preserve** `Symbol` nodes and their edges — this is the big win over content hashing, which would delete and recreate everything on a move.
- **Manifest changes:** any change to a tracked manifest (`*.csproj`, `package.json`, `pyproject.toml`, lockfiles) triggers a full re-parse of that manifest and reconciliation of the corresponding `Project` node's edges. Cheaper than chunk diffing.

After the batch commits, update `Repo.indexedCommit = HEAD`. If the indexer crashes mid-batch, the next run sees the old SHA and re-processes the same diff — operations are idempotent.

**Edge cases:**
- **Shallow clones** (common in CI): detected via `git rev-parse --is-shallow-repository`. History-aware features degrade; basic indexing still works.
- **Submodules:** out of scope for V1. Flagged as a known limitation.
- **Force-pushed branches / rewritten history:** if the stored `indexedCommit` is no longer reachable from `HEAD`, fall back to a full re-index rather than producing a corrupt diff.

### 4.7 Graph Store Writer

Persists nodes, edges, and embeddings via the `IGraphStore` abstraction (§5.0) in a single transactional batch per file. Implementation handles dialect-specific concerns; pipeline is storage-agnostic.

---

## 5. Storage Abstraction & Schema

### 5.0 IGraphStore

All persistence goes through an `IGraphStore` interface. Two implementations ship in V1:

- `PostgresGraphStore` — Postgres + `pgvector` + `pg_trgm`. Recommended for repos > 100k symbols, multi-developer/team scenarios, or anywhere a Postgres instance is already available.
- `SqliteGraphStore` — SQLite + `sqlite-vec` + FTS5. **Default.** Zero-setup, ships as a file alongside the indexer. Recommended for single-developer use and repos under ~100k symbols.

The interface exposes operations rather than queries: `UpsertSymbol`, `UpsertEdgeBatch`, `DeleteSymbolsByFile`, `VectorSearch`, `TraverseFrom`, `ApplyClusterAssignments`, etc. Query construction lives inside the implementation. Application code never sees SQL.

**Implementation strategy:** Dapper for raw SQL (not EF Core — workloads are batch inserts and recursive CTEs, not OLTP). Dialect differences are isolated to a small set of methods (vector search, fuzzy name matching, conflict resolution syntax). FluentMigrator handles schema versioning across both stores.

**Embedding dimension is configurable** per indexer instance — default 1536 (OpenAI compatible), overridable for local models (e.g., 768 for BGE-base, 1024 for BGE-large). Schema columns parametrize on the configured dimension at migration time.

**Default selection:** when the indexer is run with no storage config, it provisions a SQLite database file in the working directory. Power users opt into Postgres via connection string config.

### 5.1 Logical data model

The model is graph-shaped even though storage is relational. Nodes become rows in entity tables; edges become rows in a polymorphic `edges` table.

**Entity tables:**

| Table | Key columns |
|---|---|
| `repos` | id, root_path, indexed_commit, indexed_at, is_shallow |
| `projects` | id, repo_id, name, manifest_path, language, ecosystem (nuget/npm/pypi) |
| `external_packages` | id, project_id, name, version, version_resolved, ecosystem, scope (runtime/dev/peer) |
| `files` | id, repo_id, project_id, path, language, content_hash, last_indexed_at |
| `modules` | id, project_id, name, path (logical grouping: namespace, package, directory) |
| `symbols` | id, file_id, module_id, name, kind, signature, summary, embedding, content_hash, is_utility |
| `clusters` | id, summary, embedding, coherence, type (`business`/`infrastructure`/`mixed`), level |

**Single polymorphic edge table:**

| Column | Description |
|---|---|
| `source_id` | Foreign key — entity at edge origin |
| `source_kind` | Entity type at origin (`symbol`/`file`/`project`/etc.) |
| `target_id` | Foreign key — entity at edge target |
| `target_kind` | Entity type at target |
| `edge_kind` | One of: `contains`, `depends_on`, `imports`, `references`, `defines`, `member_of` |
| `confidence` | 0–1 score (relevant for `references` and weak `member_of`) |
| `signals` | JSON array (`name_match`, `llm_extraction`, `external_likely`, `unresolved`) — see §6.1 |
| `properties` | JSON for edge-kind-specific fields (e.g., `member_of.kind` = primary/utility) |

Polymorphic edges keep the schema simple at the cost of giving up referential integrity at the database level. The application layer is the source of truth for edge validity. For a code index where the writer is a single component (the indexer) and the schema is heavily indexed for retrieval rather than transactional updates, this is the right tradeoff.

### 5.2 Indexes

Indexes the query workload depends on:

- `symbols(file_id)`, `symbols(module_id)` — scoped lookups during reference resolution and incremental updates
- `symbols(name)` — name match during reference resolution
- Trigram index on `symbols(name)` (Postgres: `pg_trgm`; SQLite: FTS5) — fuzzy name matching
- HNSW vector index on `symbols(embedding)` and `clusters(embedding)` (Postgres: `pgvector`; SQLite: `sqlite-vec`)
- `edges(source_id, edge_kind)`, `edges(target_id, edge_kind)` — bidirectional traversal
- `edges(edge_kind, confidence)` — confidence-filtered queries

### 5.3 Postgres vs. SQLite: where they differ

| Concern | Postgres | SQLite |
|---|---|---|
| Vector type | `vector(N)` via `pgvector` | `BLOB` queried via `sqlite-vec` virtual table |
| Vector index | HNSW, IVFFlat | k-NN via `sqlite-vec` (no HNSW yet — improving) |
| Fuzzy name match | `pg_trgm` GIN index | FTS5 virtual table |
| JSON | `jsonb` | `JSON` (text) with `json_extract` |
| Concurrency | Multi-writer | Single-writer (WAL mode allows concurrent reads) |
| Recursive CTE | Yes, fast on deep | Yes, fine for typical depths (≤6 hops) |
| Conflict resolution | `ON CONFLICT (...) DO UPDATE` | `ON CONFLICT (...) DO UPDATE` |
| Deployment | Server | File |

The interface hides all of this. Implementation differences are isolated to roughly six methods.

### 5.4 Performance ceiling

For a repo of 100k symbols with ~500k edges (typical mid-sized service):

- SQLite: full index in single-digit minutes after summarization, query latency for 3-hop traversals well under 50ms
- Postgres: similar at this scale; pulls ahead at 1M+ symbols where HNSW vector search dominates

For very large repos (5M+ symbols, monorepos), Postgres is the only sensible choice. The spec assumes single-repo use cases of moderate size for V1.

---

## 6. Reference Resolution (the fuzzy call graph)

Without a language server, `REFERENCES` edges are inferred, not resolved. The Manifest Parser does the first cut: any import resolved to an `ExternalPackage` short-circuits — no intra-repo name matching attempted. For everything left, three signals combined:

1. **Name match within scope** — for each call-site identifier in a symbol's body, find candidate definitions reachable via the file's intra-repo imports/usings. Single match → high confidence. Multiple matches → split into low-confidence edges.
2. **LLM-extracted call targets** — the summarizer reports probable callees. Used as a second signal; agreement with (1) raises confidence, disagreement flags ambiguity.
3. **Heuristics** — receiver type hints from variable names (`var user = ...; user.Save()` likely targets a `User`-related `Save`). Optional, V2.

### 6.1 Signal taxonomy

Each `REFERENCES` edge carries a `confidence` score and a `signals` array drawn from a small enum:

| Signal | Meaning |
|---|---|
| `name_match` | Identifier resolved to one or more in-scope `Symbol` nodes |
| `llm_extraction` | Summarizer's probable-callees output named this target |
| `external_likely` | LLM extracted a callee with no name match, but the call site's surrounding imports reference a known `ExternalPackage` — likely a framework call (`services.AddScoped`, `Console.WriteLine`, `pd.read_csv`) into code we don't index |
| `unresolved` | LLM extracted a callee with no name match and no plausible external package — could be dynamic dispatch, reflection, or hallucination. Lowest-confidence bucket. |

Multiple signals can coexist on one edge. `["name_match", "llm_extraction"]` is the high-confidence agreement case. `["external_likely"]` flows into Dependency queries (§9.1) — "what frameworks does this code call into" becomes answerable. `["unresolved"]` edges are filtered out by default at query time but kept in the graph for diagnostic queries.

**Distinguishing `external_likely` from `unresolved`:** if the call site's receiver, surrounding usings/imports, or LLM-named callee can be plausibly traced to a known `ExternalPackage` for the source file's `Project`, tag `external_likely`. Otherwise `unresolved`. Exact heuristic is implementation tuning; a reasonable starting point is namespace-prefix matching against package names.

---

## 7. Graph Hydration (Indexing Order)

Reference edges need both endpoints to exist before they can be written — so indexing runs in two phases. Phase 1 populates every node the run will produce; Phase 2 resolves edges between them.

### 7.1 The fundamental problem

When parsing `OrderService.cs` and the indexer sees `inventory.Reserve()`, the target `InventoryService.Reserve` symbol may not be in the graph yet (file parsed later in the run) or may be ambiguous (multiple symbols named `Reserve` exist across the repo). A single-pass approach can't write that edge correctly. Two phases solve this cleanly.

### 7.2 Phase 1 — Definitions pass

Walk every file. Per file:

1. Tree-sitter parse → AST → chunks
2. Write `File`, `Symbol`, `Module` nodes
3. Write `CONTAINS`, `DEFINES` edges (file-local — no cross-file resolution needed)
4. Write `IMPORTS` edges where the target resolves to a known `File` or `ExternalPackage` (the Manifest Parser ran before the code pass, so external resolution is already done)
5. **Capture but do not yet write** the call sites: every identifier that looks like a method/function invocation in each symbol's body, with its scope (the source file's imports, receiver expression if any, and the LLM-extracted call targets from the summarizer)

These pending call sites land in a staging table — `UnresolvedCallSites` — keyed by source symbol ID.

At the end of Phase 1, the graph holds every node it will ever have for this run. References are still pending.

### 7.3 Phase 2 — Resolution pass

The full symbol table now exists. For each pending call site:

1. Look up candidate `Symbol` nodes by name
2. Filter candidates by reachability — only symbols in the same file/module, or in files reachable via the source file's `IMPORTS` edges
3. Score each candidate:
   - Exact name + signature match → high confidence
   - Name match only → medium confidence
   - Multiple matches → split into low-confidence edges across all candidates
   - LLM-extracted target agrees with name match → confidence boost
   - LLM target found, no name match → check whether the call site can be traced to a known `ExternalPackage`. Tag `["external_likely"]` if yes, `["unresolved"]` if no. (See §6.1 for signal semantics.)
4. Write `REFERENCES` edges with `confidence` and `signals` properties

Conceptually a join: `UnresolvedCallSites ⋈ Symbols on (name, scope)`. Implemented in SQL as batched inserts into the `edges` table — one transaction per source file's call sites, not per call site. The Postgres and SQLite implementations both express this as `INSERT INTO edges ... SELECT ... FROM unresolved_call_sites JOIN symbols ON ... WHERE reachable`, with provider-specific syntax for the recursive reachability check.

### 7.4 Incremental hydration (the common case)

When `git diff` reports `OrderService.cs` changed:

1. **Definitions pass, scoped.** Re-parse the file. Diff chunk hashes against existing `Symbol.contentHash`. Update changed symbols, delete removed ones, add new ones.
2. **Forward edge invalidation.** Delete all `REFERENCES` edges *sourcing from* changed/removed symbols. Don't touch incoming edges yet.
3. **Resolution pass, scoped.** Re-resolve outgoing call sites for the changed symbols only.
4. **Reverse invalidation.** Find `REFERENCES` edges from *other files* targeting changed/removed symbols in this file. Mark them dirty — the target's signature may have changed, or the target may no longer exist.
5. **Reverse resolution.** Re-resolve dirty edges. Source symbols haven't changed; only their target lookup needs to re-run.

The reverse invalidation step (4) is the easy one to forget. If `InventoryService.Reserve` is renamed to `ReserveStock`, every edge from every other file pointing at the old `Reserve` becomes stale. Skipping this step means accepting stale edges until the next full re-index.

**Invalidation policy (V1): pragmatic, not strict.** Reverse invalidation only triggers on:
- **Symbol deletion** — incoming edges become orphans, must be re-resolved or removed
- **Symbol rename** — old name no longer matches; edges by name will fail to resolve
- **Visibility change** (e.g., `public` → `private`) — affects reachability for callers

It does **not** trigger on:
- Method body changes — call site still resolves to the same symbol
- Signature changes (parameter list, return type) — the edge target is still correct; the signature mismatch shows up at query time as low-confidence match if the strict signature check is enabled

This is cheaper than strict invalidation (which would re-resolve on any signature change) and right for V1. The cost is occasional stale signature info in low-confidence scoring until the next full re-index — acceptable for a fuzzy index. Strict invalidation can be a configurable mode in V2 for users who want maximum precision.

### 7.5 End-to-end indexing order (fresh index)

```
1. Repo Walker        → list of files + git state
2. Manifest Parser    → Project, ExternalPackage nodes; intra-repo project graph
3. Tree-sitter Parser → ASTs (per-file, pipelined)
4. Chunker            → Symbol nodes + per-chunk call-site list (staged)
5. Summarizer         → Symbol summaries, embeddings, LLM call targets (joins staging)
6. Phase 1 writer     → entities (files, symbols, modules) and structural edges (contains, defines, imports) via IGraphStore
7. Phase 2 resolver   → REFERENCES edges
8. Cluster worker     → Leiden + Cluster nodes (async, separate stage — see §8)
```

Steps 3–5 pipeline per-file with no inter-file dependency. Steps 6 and 7 are the synchronization points where parallel work converges.

### 7.6 Design notes

- **Staging table location.** In V1, `unresolved_call_sites` is a regular table in the same store, dropped at the end of Phase 2. Same transactional semantics, no extra dependency.
- **Idempotency.** Both phases must be idempotent. If Phase 2 crashes halfway, re-running it should produce the same edges, not duplicates. `INSERT ... ON CONFLICT (...) DO UPDATE` (works identically in Postgres and SQLite) is the standard pattern.
- **Concurrency on SQLite.** SQLite is single-writer. The cluster worker (§8.5) does not run concurrently with incremental indexing on SQLite — it runs only when the indexer is idle. On Postgres, both can run concurrently. Spec assumes serialized execution as the V1 default; Postgres users get concurrent execution as a configuration option.

---

## 8. Cluster Layer (GraphRAG-style)

A background worker partitions the symbol graph into communities and produces an LLM summary per community. This is what answers global ("tour the codebase") and subsystem ("how does auth work") queries without retrieving every symbol individually.

### 8.1 The boundary problem

Pure Leiden community detection optimizes for modularity — dense intra-cluster edges, sparse inter-cluster edges. It has no concept of project, namespace, or assembly. Run unconstrained on a real codebase and you typically get one of two failure modes:

- **Over-merging:** a giant cluster forms around the DI container, shared utilities, or a `BaseService` class because everything calls into them.
- **Fragmentation:** every leaf service becomes its own cluster, losing the subsystem-level grouping developers actually think in.

Either way, cluster summary quality suffers. **Cluster summary quality is bottlenecked by cluster boundary quality** — a cluster spanning "auth + half of user management + the JWT wrapper" produces a vague summary; a cluster cleanly scoped to "JWT validation and refresh token handling" produces a sharp one. Tuning boundaries directly determines how good global and subsystem queries are.

### 8.2 V1 approach: boundary-aware Leiden

Use existing repository structure as a prior, not a hard rule. Two mechanisms:

**Hierarchical seeding by project.** Run Leiden separately within each `Project` node, then run a coarser pass at the project level to group related projects. Each project becomes a top-level cluster boundary; sub-clusters live inside. Project boundaries are effectively inviolable.

**Soft namespace bias via edge weights.** Before running Leiden, multiply edge weights:

- Intra-namespace `REFERENCES` edges: ×1.5 (favor keeping namespaces together)
- Inter-project `REFERENCES` edges: ×0.5 (resist crossing project boundaries unless call density really demands it)
- All other edges: base weight = `confidence`

Soft bias means Leiden *can* still cross a boundary when coupling is genuinely high — the structure surfaces hidden coupling rather than hiding it. Hard project seeding above prevents the giant-utility-cluster problem; soft namespace bias keeps related code together within a project.

Three configuration parameters, exposed in the indexer config:
- `projectBoundaryMode`: `hard` (default) | `soft` | `off`
- `namespaceWeightMultiplier`: default 1.5
- `interProjectWeightMultiplier`: default 0.5

### 8.3 Utility node handling (the God Object problem)

Boundary-aware weights are not enough on their own. A common library — `Logger`, `Result<T>`, `Constants`, a shared `BaseService` — used by 90% of symbols will still drag unrelated features into the same community. Modularity optimization is dominated by raw edge count, and a 1.5× intra-namespace boost loses to a node with thousands of inbound edges. The fix has to be structural, not just weighted.

**Detection: statistical signal + topological signal.**

Pure in-degree alone produces false positives. A `UserRepository` may have 200 inbound edges, but they're all genuinely about user management — that node belongs in the user cluster, not excluded. The combined signal:

1. **Statistical** — node degree (in + out) above a percentile threshold of the repo's degree distribution. Default: 99th percentile, with an absolute floor (e.g., degree > 50) to avoid noisy thresholds on small repos. Percentile is preferred over standard deviation because code graphs are typically long-tailed (non-normal).
2. **Topological** — after a *trial* Leiden run, the node's neighbors span many disparate clusters. A node whose callers cluster tightly is a legitimate subsystem hub (keep it). A node whose callers spread across the codebase is structural noise (exclude it).
3. **Convention** — optional hint from the Manifest Parser: anything in a `*.Common`, `*.Shared`, `*.Infrastructure`, `*.Utilities`, or `*.Abstractions` project is treated as a utility candidate, subject to the statistical and topological checks. Speeds up detection on conventional codebases.

A node is flagged as a utility when **statistical AND topological** both fire (with convention as a tiebreaker for borderline cases).

**Procedure: two-pass clustering.**

1. Trial Leiden run with all edges and boundary-aware weights from §8.2.
2. Compute per-node degree percentiles and caller-cluster spread (entropy across trial cluster assignments).
3. Flag utility nodes per the combined detection rule above.
4. Build a cleaned edge set: edges incident to utility nodes are excluded from the weight calculation. Utility nodes remain in the graph; only their edges are dropped from the modularity computation.
5. Run final Leiden on the cleaned edge set.
6. Post-hoc, assign utility nodes to clusters: by default, to a dedicated "Infrastructure" / "Shared" cluster per project. Optionally, to the cluster containing the bulk of the utility's *definition* if it lives clearly inside one subsystem.

**Weak membership semantics.**

Utility cluster membership is weaker than regular membership. The `MEMBER_OF` edge gains a `kind` property:
- `kind: "primary"` — the symbol is a defining part of this cluster
- `kind: "utility"` — the symbol is structural and lives in a Shared/Infrastructure cluster, not a topical one

This affects queries: "show me everything in the auth cluster" returns primary members only by default. Utility references are surfaced as *adjacent* via a separate query path, not as members. Prevents `Logger` from cluttering every subsystem view.

**Configuration parameters:**
- `utilityDegreePercentile`: default `99` (top 1% of nodes by degree)
- `utilityDegreeFloor`: default `50` (absolute minimum, overrides percentile on small repos)
- `utilityClusterSpreadThreshold`: default `0.6` (entropy threshold across trial clusters; higher = more selective)
- `utilityNamingHints`: default `["*.Common", "*.Shared", "*.Infrastructure", "*.Utilities", "*.Abstractions"]`
- `utilityAssignmentStrategy`: `dedicated` (default) | `byDefinition`

These thresholds are repo-dependent and need real-world tuning. The instrumentation in §8.7 supports parameter sweeps for this.

### 8.4 Cluster summarization

For each community, an LLM produces a **subsystem summary** plus an embedding of that summary for vector retrieval at query time. Stored as `Cluster` nodes with `MEMBER_OF` edges to constituent symbols.

**Cluster type classification.** Every cluster is classified during summarization into one of:

- `business` — domain-specific code that exists because of what the product does (e.g., "JWT validation and session management", "order placement and inventory reservation", "trip pricing rules")
- `infrastructure` — code that exists because of how software is built (e.g., logging, DI registration, error handling, config loading, shared types)
- `mixed` — clusters that genuinely combine both; flagged for re-cluster review

The classification is stored as `Cluster.type` and drives query-time pruning (§9.1).

**Two prompt templates, selected by cluster origin:**

1. **Primary clusters** (output of the main Leiden pass) get a *domain summary* prompt — "Identify the business concept this cluster owns. Describe the domain operation it implements." The LLM is asked to classify the cluster as `business`, `infrastructure`, or `mixed`, with a short justification. Most primary clusters classify as `business`.
2. **Utility clusters** (the dedicated Shared/Infrastructure clusters from §8.3) get an *infrastructure summary* prompt — "This cluster contains cross-cutting code used across the codebase. Describe its *role*, not a unifying topic." Always classified as `infrastructure`. Avoids the LLM hallucinating a fake unifying business concept.

**Coherence scoring.** Each cluster gets a self-rated coherence score on a 1–5 scale. The prompt is type-aware:
- For `business`: "Does this set of symbols represent a single coherent business concept?"
- For `infrastructure`: "Does this set of symbols represent a single coherent technical role?"

Stored as `Cluster.coherence`. Low-coherence clusters surface in queries with a confidence flag, and trigger a re-cluster suggestion to the operator.

`mixed` is the failure mode worth watching — a high count of `mixed` clusters means the boundary tuning (§8.2) is wrong for this codebase. Surfaced in the tuning instrumentation (§8.7).

### 8.5 Re-clustering policy

- Scheduled nightly for active repos, on-demand for ad-hoc indexing.
- **Not incremental in V1** — Leiden is run from scratch each time. Massively simpler than incremental community detection; the cost is acceptable on nightly cadence for repos up to ~1M symbols.
- **Old cluster summaries are replaced atomically** at the end of a re-cluster run, not piecewise. Avoids the "cluster definition shifted mid-query" hazard.

### 8.6 V2: multi-resolution clustering

Leiden has a resolution parameter — high resolution = many small clusters, low resolution = few big ones. No single resolution is right for all queries:

- "Tour of the codebase" → low resolution (5–15 clusters)
- "How does auth work" → medium resolution (matches subsystem-sized chunks)
- "What's the structure of the payments module" → high resolution within that subtree

V2 runs Leiden at multiple resolutions and stores them as parallel `Cluster` hierarchies (`Cluster.level` distinguishes them). The query planner selects the appropriate resolution per query based on scope. This is what Microsoft GraphRAG does. It costs more storage and compute; the query quality improvement justifies it once V1 boundary tuning is stable.

### 8.7 Tuning instrumentation

Boundary tuning is iterative, not a one-shot decision. The indexer exposes:

- Coherence score distribution across clusters (mean, low-end tail)
- Configurable re-cluster with parameter sweeps for offline tuning
- Per-cluster size distribution (flag clusters > 200 symbols as likely over-merged)

Operators tune `namespaceWeightMultiplier` and `interProjectWeightMultiplier` against their codebase by re-clustering and inspecting coherence.

---

## 9. Query Pipeline

### 9.1 Query Planner

Classifies the question into one of:

- **Local** — "What does method X do?" → vector search + 1-hop graph expansion
- **Subsystem** — "How does auth work?" → cluster summaries + symbol drill-down
- **Global** — "What does this codebase do?" → primary `business` cluster summaries; `infrastructure` clusters mentioned only as a footer ("plus standard infrastructure for logging, DI, error handling")
- **Impact** — "What calls X?" → graph traversal from a known symbol, no vector search needed
- **Dependency** — "What uses package X?", "What does this project depend on?", "What's affected if I upgrade Y?" → traversal over `depends_on` and `imports` edges, plus `references` edges with `external_likely` signal for framework-call queries ("what code calls into ASP.NET Core")

For Global queries specifically, the planner uses `clusters.type` to drive pruning: `business` clusters lead the response, `infrastructure` clusters are summarized in aggregate, `mixed` clusters surface with a confidence note. This prevents the "tour of the codebase" answer from being dominated by the DI container and logging plumbing.

### 9.2 Hybrid Retriever

For local/subsystem queries:

1. Embed the question
2. Vector search over `symbols.embedding` → top-k symbols
3. Edge expansion: 1–2 hops along `references`, `contains`, `imports` edges (filtered by confidence threshold) — implemented as recursive CTE in both Postgres and SQLite
4. Pull cluster summary for each retrieved symbol's community

Both vector search and edge traversal are exposed as `IGraphStore` operations; the Hybrid Retriever composes them without knowing which store is underneath. On Postgres, vector search and traversal can be combined into a single query; on SQLite, they run as two queries with the application joining results — same logical result, different cost profile.

### 9.3 Context Assembler

- Deduplicates retrieved chunks
- Orders by relevance + structural locality (chunks from the same file/class kept together)
- Truncates to fit target context budget
- Includes: cluster summaries (high-level orientation) → symbol summaries (mid-level) → raw code (specifics)

### 9.4 LLM Synthesis

The agent receives the assembled context and answers. No special handling beyond a system prompt that tells it the context comes from a fuzzy index and to flag uncertainty when reference confidence is low.

---

## 10. Key Tradeoffs & Open Questions

| Tradeoff | V1 choice | Reasoning |
|---|---|---|
| Storage backend | SQLite (default) or Postgres (user choice) via `IGraphStore` abstraction | SQLite for zero-setup single-developer use; Postgres for larger repos and team scenarios. Both ship in V1. |
| ORM strategy | Dapper, not EF Core | Workloads are batch inserts and recursive CTEs, not OLTP — EF's LINQ-to-SQL doesn't help for graph traversal |
| Graph storage paradigm | Polymorphic edges table | Schema simplicity + dialect portability; gives up DB-level referential integrity (enforced in app layer) |
| Tree-sitter in-process vs. out-of-process | Out-of-process | Sustainable, decouples grammar updates from .NET build |
| Summarize all methods vs. lazy | Configurable; default = classes + public methods only | Cost control on first index of large repos |
| Incremental Leiden vs. scheduled rebuild | Scheduled (nightly default) | Massively simpler; clusters are fault-tolerant to slight staleness |
| Cluster boundaries: pure Leiden vs. structure-aware | Hierarchical project seeding + soft namespace bias | Cluster summary quality bottlenecks query quality; existing repo structure is a strong prior |
| Utility node handling | Two-pass clustering: detect statistically + topologically, exclude edges from modularity, reassign post-hoc | Prevents God Objects (Logger, Result&lt;T&gt;) from collapsing the graph into one giant cluster |
| Single-resolution vs. multi-resolution clustering | Single resolution in V1, multi in V2 | Multi-resolution doubles storage/compute; defer until V1 boundary tuning is stable |
| Embedding granularity: file / class / method | Method (and class for context) | Matches retrieval granularity |
| Reference resolution: include LLM signal | Yes, with `external_likely` / `unresolved` distinction | Catches dynamic dispatch, reflection, and framework calls; separating external from unresolved makes signals queryable |
| Summarizer model selection | Tiered: stronger model for interfaces/abstracts/public APIs, cheaper for leaves | Interfaces are connective tissue — one bad summary contaminates many implementations |
| Cluster summary classification | Explicit `business` / `infrastructure` / `mixed` label | Drives Global query pruning so the codebase tour leads with domain logic, not DI plumbing |
| Reverse invalidation: strict vs. pragmatic | Pragmatic (rename/delete/visibility only) | Cheaper; signature changes show up as low-confidence at query time |

### Open questions to resolve before V1

1. **Embedding model.** Local (cheap, slower) or hosted (fast, ongoing cost)? Affects deployment story.
2. **Multi-repo support.** One graph per repo, or unified graph with `Repo` node? Probably the former for V1.
3. **Auth-aware indexing.** If the repo contains secrets/credentials, are summaries safe to send to a hosted LLM? Need a redaction pass or a "local model only" mode.
4. **Non-git repos.** V1 assumes git. Worth supporting Mercurial/SVN/plain directories? Probably no — adds complexity for a shrinking audience.

---

## 11. V1 Scope vs. Later

### V1 (MVP)

- Tree-sitter chunking for C#, TypeScript, Python
- Manifest parsing for `*.csproj`, `package.json` (+ lockfiles), `pyproject.toml` (+ `requirements.txt` fallback)
- External packages tracked as opaque nodes (no recursion into third-party code)
- Symbol + class summarization, with interface-first ordering and tiered models
- Git-driven change detection
- IGraphStore abstraction with two implementations: `SqliteGraphStore` (default, sqlite-vec + FTS5) and `PostgresGraphStore` (pgvector + pg_trgm)
- Configurable embedding dimension; default 1536
- FluentMigrator for schema versioning across both stores
- Hybrid retrieval (vector + 1-hop graph)
- Cluster summaries on nightly rebuild, with hierarchical project seeding + soft namespace bias
- Two-pass clustering with utility node detection (statistical + topological)
- Cluster classification (`business` / `infrastructure` / `mixed`) and coherence scoring
- Reference resolution with full signal taxonomy (`name_match`, `llm_extraction`, `external_likely`, `unresolved`)
- CLI: `index <repo>`, `query <question>`

### V2+

- Working-tree overlay (query against uncommitted changes)
- History-aware queries ("when did this method change", "who touched auth recently")
- Branch-aware indexing (index `main`, query feature branches via diff)
- Submodule support
- Opt-in indexing of curated third-party packages (e.g., internal company libraries)
- Additional Python manifest formats (`Pipfile`, `setup.py`, `environment.yml`)
- Multi-resolution clustering with resolution-aware query planning
- Strict reverse-invalidation mode (re-resolve on any signature change, not just rename/delete)
- Receiver-type heuristics for reference resolution
- Incremental Leiden
- Cross-repo indexing
- Diff-aware queries ("what changed in auth this week")
- Editor integration (VS Code / Visual Studio extension)
- Local-only mode (no hosted LLM calls)

---

## 12. How this fits into Agency

Package split:
- `Agency.GraphRAG.Code` — core indexer, IGraphStore abstraction, query pipeline
- `Agency.GraphRAG.Code.Sqlite` — SQLite implementation (default)
- `Agency.GraphRAG.Code.Postgres` — Postgres implementation

Integration:
- Reuses `Agency.Core` abstractions for LLM provider, embedding provider, and the agent message contract
- Exposes an `ICodeIndex` capability that any agent in an `IAgentTeam` can query
- The summarizer and query planner are themselves agents — eats its own dogfood as a reference implementation of multi-agent orchestration over a shared structured context

---
