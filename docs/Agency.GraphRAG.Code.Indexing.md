# Agency.GraphRAG.Code — Indexing Pipeline

#graphrag #indexing #tree-sitter #manifest-parser #chunker #summarizer

The indexing pipeline transforms raw repository files into a semantic graph: repository → files → AST chunks → summaries → nodes + edges ready for storage and querying.

## Repo Walker (Git-aware)

- Operates on a git working tree. The repo's last indexed commit SHA is stored on a `Repo` node in the graph.
- **First index:** enumerates all tracked files via `git ls-files`, respecting `.gitignore` automatically. Applies a configurable allow/deny list on top.
- **Incremental index:** runs `git diff <indexedCommit> HEAD --name-status -M` to get the exact set of added (`A`), modified (`M`), deleted (`D`), and renamed (`R`) files. No filesystem scan needed.
- Detects language by extension + shebang/heuristic fallback.
- **Implementation note:** shells out to the `git` binary rather than using LibGit2Sharp. More reliable for diff/log operations, matches how other indexers do it, and avoids native-binding maintenance.
- **V1 constraint:** indexes committed state only. Users with uncommitted changes are expected to commit (or stash) before re-indexing. Working-tree overlay is a V2 consideration.

## Tree-sitter Parser

- Tree-sitter runs **out-of-process** (Node sidecar or Rust CLI piped via JSON). Avoids the maintenance burden of TreeSitterSharp and keeps grammar updates decoupled from the .NET build.
- Parser receives a file, returns a structured AST as JSON.
- Grammars loaded dynamically per detected language.

## Manifest Parser

Parses dependency manifests in parallel with code parsing. Establishes project boundaries and the first-party / third-party divide that downstream stages rely on.

### Supported manifests (V1)

| Language | Primary | Lockfile | Fallback |
| --- | --- | --- | --- |
| C# | `*.csproj`, `*.sln`, `Directory.Packages.props` | — (NuGet resolves at build) | — |
| TypeScript/JS | `package.json` | `package-lock.json`, `yarn.lock`, `pnpm-lock.yaml` | — |
| Python | `pyproject.toml` | `poetry.lock`, `uv.lock` | `requirements.txt` |

### What it extracts

- Project name and root path
- Direct dependencies → `ExternalPackage` nodes with version (from manifest)
- Resolved versions from lockfile (preferred when present)
- Intra-repo project references (C# `<ProjectReference>`, npm/pnpm workspaces, Python path-based deps)
- Workspace/monorepo membership

### Behavioral rules

- Manifests are **re-parsed in full** when changed — no chunk-level diffing. They're small and structural; cheap to reconcile.
- A repo can have many manifests (multi-project solutions, pnpm workspaces, Python monorepos). Each becomes a `Project` node.
- **External packages are tracked as opaque nodes by default.** The indexer does not recurse into `node_modules`, `~/.nuget`, or site-packages. Indexing third-party code is a V2 opt-in for curated packages.
- Python's manifest fragmentation is accepted: parse `pyproject.toml` first, fall back to `requirements.txt`, ignore `setup.py` / `setup.cfg` / `Pipfile` / `environment.yml` in V1.

## Chunker

Splits the Abstract Syntax Tree (AST) into **semantically meaningful blocks**:

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

## Summarizer

For each symbol-level chunk, an LLM produces:

- **One-line purpose** (used for embedding)
- **Detailed summary** (inputs, outputs, side effects, key calls — used for retrieval context)
- **Probable call targets** (extracted heuristically — names of methods/functions invoked)

### Interface-first ordering

Interfaces and abstract classes are summarized *before* their implementations. Two reasons:

1. **Cardinality.** A bad summary on `IRepository` contaminates retrieval for every concrete repository — the connective tissue deserves higher-quality summaries.
2. **Context propagation.** Implementation summaries are dramatically better when the prompt includes the interface's intent.

### Tiered models

Interfaces, abstract classes, and public-API types use a stronger model. Concrete implementations and internal helpers use a cheaper model. One-line embedding summaries use the cheapest tier. Configurable per-tier model selection.

### Cost mitigations

- Cache summaries keyed by chunk content hash (unchanged code → no re-summarization)
- Summarize file-level and class-level only; lazily summarize methods on first query (optional toggle)
- Use cheaper model for one-liners and concrete leaves; reserve stronger model for interfaces, abstracts, and public APIs

## Change Detector

Driven entirely by git diff output from the Repo Walker. Per file status:

- **`A` (added):** parse → chunk → summarize → insert `File`, `Symbol` nodes and `CONTAINS`, `DEFINES`, `IMPORTS` edges.
- **`M` (modified):** re-parse the file, hash each chunk, compare against existing `Symbol.contentHash` values. Only chunks with changed hashes are re-summarized. Edges sourcing from changed symbols are invalidated and re-resolved.
- **`D` (deleted):** remove the `File` node, cascade delete owned `Symbol` nodes, and clean up dangling edges.
- **`R` (renamed):** update `File.path` in place. **Preserve** `Symbol` nodes and their edges — this is the big win over content hashing, which would delete and recreate everything on a move.
- **Manifest changes:** any change to a tracked manifest triggers a full re-parse of that manifest and reconciliation of the corresponding `Project` node's edges.

After the batch commits, update `Repo.indexedCommit = HEAD`. If the indexer crashes mid-batch, the next run sees the old SHA and re-processes the same diff — operations are idempotent.

## Graph Store Writer

Persists nodes, edges, and embeddings via the `IGraphStore` abstraction in a single transactional batch per file. Implementation handles dialect-specific concerns; pipeline is storage-agnostic.

---

## Next: [[Agency.GraphRAG.Code.Hydration]] — Two-phase reference resolution
