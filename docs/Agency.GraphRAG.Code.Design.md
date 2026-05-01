# Agency.GraphRAG.Code — Design Decisions

#graphrag #design #tradeoffs #v1-scope

This document captures the key architectural decisions, tradeoffs, and scope delineation for the GraphRAG.Code system.

---

## Key Tradeoffs & Design Decisions

| Tradeoff | V1 choice | Reasoning |
| --- | --- | --- |
| Storage backend | SQLite (default) or Postgres (user choice) via `IGraphStore` abstraction | SQLite for zero-setup single-developer use; Postgres for larger repos and team scenarios. Both ship in V1. |
| ORM strategy | Dapper, not EF Core | Workloads are batch inserts and recursive CTEs, not OLTP — EF's LINQ-to-SQL doesn't help for graph traversal |
| Graph storage paradigm | Polymorphic edges table | Schema simplicity + dialect portability; gives up DB-level referential integrity (enforced in app layer) |
| Tree-sitter in-process vs. out-of-process | Out-of-process | Sustainable, decouples grammar updates from .NET build |
| Summarize all methods vs. lazy | Configurable; default = classes + public methods only | Cost control on first index of large repos |
| Incremental Leiden vs. scheduled rebuild | Scheduled (nightly default) | Massively simpler; clusters are fault-tolerant to slight staleness |
| Cluster boundaries: pure Leiden vs. structure-aware | Hierarchical project seeding + soft namespace bias | Cluster summary quality bottlenecks query quality; existing repo structure is a strong prior |
| Utility node handling | Two-pass clustering: detect statistically + topologically, exclude edges from modularity, reassign post-hoc | Prevents God Objects (Logger, Result\<T\>) from collapsing the graph into one giant cluster |
| Single-resolution vs. multi-resolution clustering | Single resolution in V1, multi in V2 | Multi-resolution doubles storage/compute; defer until V1 boundary tuning is stable |
| Embedding granularity: file / class / method | Method (and class for context) | Matches retrieval granularity |
| Reference resolution: include LLM signal | Yes, with `external_likely` / `unresolved` distinction | Catches dynamic dispatch, reflection, and framework calls; separating external from unresolved makes signals queryable |
| Summarizer model selection | Tiered: stronger model for interfaces/abstracts/public APIs, cheaper for leaves | Interfaces are connective tissue — one bad summary contaminates many implementations |
| Cluster summary classification | Explicit `business` / `infrastructure` / `mixed` label | Drives Global query pruning so the codebase tour leads with domain logic, not DI plumbing |
| Reverse invalidation: strict vs. pragmatic | Pragmatic (rename/delete/visibility only) | Cheaper; signature changes show up as low-confidence at query time |

---

## V1 Scope (MVP)

The following features ship in V1:

### Indexing & Parsing
- Tree-sitter chunking for C#, TypeScript, Python
- Manifest parsing for `*.csproj`, `package.json` (+ lockfiles), `pyproject.toml` (+ `requirements.txt` fallback)
- External packages tracked as opaque nodes (no recursion into third-party code)
- Git-driven change detection (added, modified, deleted, renamed files)

### Summarization
- Symbol + class summarization with interface-first ordering
- Tiered models: stronger for interfaces/abstracts/public APIs, cheaper for leaves
- LLM-extracted call targets for reference resolution signals

### Storage & Schema
- IGraphStore abstraction with two implementations:
  - `SqliteGraphStore` (default, sqlite-vec + FTS5)
  - `PostgresGraphStore` (pgvector + pg_trgm)
- Configurable embedding dimension; default 1536
- FluentMigrator for schema versioning across both stores

### Graph Hydration
- Two-phase indexing (definitions pass + resolution pass)
- Pragmatic reverse invalidation (rename/delete/visibility only)

### Clustering
- Cluster summaries on nightly rebuild
- Hierarchical project seeding + soft namespace bias
- Two-pass clustering with utility node detection (statistical + topological)
- Cluster classification (`business` / `infrastructure` / `mixed`) and coherence scoring

### Querying
- Hybrid retrieval (vector + 1-hop graph + cluster context)
- Five query types: Local, Subsystem, Global, Impact, Dependency
- Reference resolution with full signal taxonomy (`name_match`, `llm_extraction`, `external_likely`, `unresolved`)

### CLI
- `index <repo>` — local indexing (SQLite file) and remote indexing (Postgres connection string config)
- `query <question>` — answer questions about the indexed repository

---

## V2+ Roadmap

The following features are deferred to V2 and later:

### Querying & Retrieval
- Working-tree overlay (query against uncommitted changes)
- History-aware queries ("when did this method change", "who touched auth recently")
- Branch-aware indexing (index `main`, query feature branches via diff)
- Diff-aware queries ("what changed in auth this week")

### Indexing
- Opt-in indexing of curated third-party packages (e.g., internal company libraries)
- Additional Python manifest formats (`Pipfile`, `setup.py`, `environment.yml`)
- Submodule support

### Clustering
- Multi-resolution clustering with resolution-aware query planning
- Incremental Leiden (current: rebuild nightly from scratch)

### Reference Resolution
- Strict reverse-invalidation mode (re-resolve on any signature change, not just rename/delete)
- Receiver-type heuristics for reference resolution

### Deployment & Integration
- Cross-repo indexing
- Editor integration (VS Code / Visual Studio extension)
- Local-only mode (no hosted LLM calls)

---

## Known Limitations (V1)

The following are intentionally out of scope for V1:

- Precise refactoring (rename-symbol, find-all-references with zero false positives) — the index is fuzzy by design
- Real-time editor integration (LSP-style) — deferred to V2
- Build-aware analysis (errors, type-checking) — the indexer is build-independent
- Non-git repositories — assumes git as the VCS

---

## Performance Assumptions

For a typical mid-sized service (100k symbols with ~500k edges):

- **SQLite:** full index in single-digit minutes after summarization, query latency for 3-hop traversals well under 50ms
- **Postgres:** similar at this scale; pulls ahead at 1M+ symbols where HNSW vector search dominates

For very large repos (5M+ symbols, monorepos), Postgres is the only sensible choice.
