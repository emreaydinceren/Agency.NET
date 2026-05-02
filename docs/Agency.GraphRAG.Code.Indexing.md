# Agency.GraphRAG.Code — Indexing Pipeline

#graphrag #indexing #tree-sitter #manifest-parser #chunker #summarizer

The indexing pipeline transforms raw repository files into a semantic graph: repository → files → AST chunks → summaries → nodes + edges ready for storage and querying.

## Registration

`AddCodeIndex()` registers the complete indexing service graph:

**Core pipeline services:**
- `GitProcessRunner`, `RepoWalker` — repository traversal and git diff
- `ManifestParserOrchestrator` (with `IManifestParser` implementations for C#, npm, Python) — dependency manifest extraction
- `ExternalPackageHeuristic`, `ScopeResolver`, `ReferenceScorer` — reference analysis
- `Phase1Writer`, `Phase2Resolver` — graph storage phases
- `ChangeDetector.ChangeDetector` — diff-driven change detection
- `IncrementalHydrator` — two-phase reference resolution

**Summarization services:**
- `SummaryCache` — cache summaries by content hash
- `ModelTierSelector` — selects model tier based on symbol kind (interface vs. implementation)
- `SummarizationPromptBuilder` — constructs LLM prompts for summaries
- `SymbolSummarizer` — LLM-based symbol summarization
- `TreeSitterClient` (via reflection) — Tree-sitter AST parsing
- `ChunkerDispatcher` (typed factory) — routes AST to language-specific chunkers
- `IWriteRequestBuilder` (via reflection) — builds write requests from AST chunks

**Query services:**
- `QueryClassifier`, `QueryPlanner` — query planning
- `HybridRetriever`, `ContextAssembler` — retrieval and context assembly
- `QueryPipeline`, `ICodeIndex` (via `CodeIndexCapability`) — query execution

**Graph store:**
- `IGraphStore` — backing store implementation (PostgreSQL or SQLite, loaded via reflection)

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

## How It Works

The `IndexingPipeline` orchestrates the complete indexing flow:

1. **Repository walk:** `RepoWalker` traverses the git tree using diff (incremental) or full scan (first index). Output: `WalkResult` with file metadata and git status per file.

2. **Manifest parsing:** `ManifestParserOrchestrator` parses all detected dependency manifests in parallel. Output: project boundaries and external package metadata.

3. **AST parsing and chunking:** `IWriteRequestBuilder.BuildAsync()` (implemented by TreeSitter's `WriteRequestBuilder`) parses each file's AST and chunks it into semantically meaningful blocks via `ChunkerDispatcher`. Each chunk is assigned a stable content hash. Output: `Phase1WriteRequest` objects, one per file, containing `Chunk` lists.

4. **Summarization:** `SymbolSummarizer.SummarizeAsync()` batches all chunks from all write requests and sends them to the LLM for summarization. It respects the interface-first ordering: interfaces and abstract classes are summarized before implementations. `ModelTierSelector` chooses the tier (strong model for public APIs / interfaces, cheaper model for internal helpers and one-liners). Each request's mutable `Summaries` dictionary is populated with the result.

5. **Change detection:** `ChangeDetector.Detect()` compares stored chunks (by path) against current chunks. For modified/renamed files, it fetches stored `Symbol` nodes from `IGraphStore.GetSymbolsByPathsAsync()`, hashes the current chunks, and returns a structured change report: which symbols changed, which are new, which were deleted.

6. **Storage (Phase1Writer):** Write requests with summaries are committed transactionally to the backing store.

7. **Reference resolution (Phase2Resolver):** After all nodes are written, `IncrementalHydrator` resolves cross-file and cross-project references in a second pass.

8. **Commit tracking:** After successful batch commit, `Repo.indexedCommit` is updated to HEAD. Crashes mid-batch leave the old SHA in place, so the next run is idempotent — same diff is re-processed.

## Change Detector

Driven entirely by git diff output from the Repo Walker. Per file status:

- **`A` (added):** parse → chunk → summarize → insert `File`, `Symbol` nodes and `CONTAINS`, `DEFINES`, `IMPORTS` edges.
- **`M` (modified):** re-parse the file, hash each chunk, compare against existing `Symbol.contentHash` values. Only chunks with changed hashes are re-summarized. Edges sourcing from changed symbols are invalidated and re-resolved.
- **`D` (deleted):** remove the `File` node, cascade delete owned `Symbol` nodes, and clean up dangling edges.
- **`R` (renamed):** update `File.path` in place. **Preserve** `Symbol` nodes and their edges — this is the big win over content hashing, which would delete and recreate everything on a move.
- **Manifest changes:** any change to a tracked manifest triggers a full re-parse of that manifest and reconciliation of the corresponding `Project` node's edges.

## Graph Store Writer

Persists nodes, edges, and embeddings via the `IGraphStore` abstraction in a single transactional batch per file. Implementation handles dialect-specific concerns; pipeline is storage-agnostic.

---

## API Surface

### IWriteRequestBuilder

```csharp
// File: src/GraphRAG.Code/Agency.GraphRAG.Code/Pipeline/IWriteRequestBuilder.cs

internal interface IWriteRequestBuilder
{
    /// <summary>Builds write requests for all processable files in the walk result.</summary>
    /// <param name="repo">The repository being indexed.</param>
    /// <param name="walkResult">The result of the repository walk.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A dictionary mapping file paths to their write requests.</returns>
    Task<IReadOnlyDictionary<string, Phase1WriteRequest>> BuildAsync(
        Repo repo,
        WalkResult walkResult,
        CancellationToken cancellationToken = default);
}
```

Implemented by `WriteRequestBuilder` in `Agency.GraphRAG.Code.TreeSitter.Pipeline`. Parses AST and generates `Phase1WriteRequest` objects with chunked symbols ready for summarization.

---

## Design Notes

- **Reflection-at-registration pattern:** `TreeSitterClient` and `IWriteRequestBuilder` (implemented by `WriteRequestBuilder` in the TreeSitter module) are registered via reflection using `Type.GetType()` and `ActivatorUtilities.CreateInstance()`. This avoids a circular project dependency: `Agency.GraphRAG.Code.TreeSitter` already depends on `Agency.GraphRAG.Code`, so the core project cannot directly reference TreeSitter. Reflection acts as the seam, using the same pattern as store implementations (Sqlite, Postgres).
- **Chunk content hashing:** Stability across renames relies on per-chunk content hashing. When a file is renamed, all chunks are re-hashed; hashes that match stored values are preserved. Only content changes trigger re-summarization.
- **Interface-first summarization:** Interfaces and abstractions are summarized before implementations, with higher-tier models reserved for public APIs and connective tissue. This improves summary quality downstream.

---

## Next: [[Agency.GraphRAG.Code.Hydration]] — Two-phase reference resolution
