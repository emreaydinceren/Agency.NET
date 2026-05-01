# Agency.GraphRAG.Code — Graph Hydration (Two-Phase Indexing)

#graphrag #reference-resolution #call-graph #incremental-update

Reference edges need both endpoints to exist before they can be written. Indexing runs in two phases to handle this cleanly.

## The fundamental problem

When parsing `OrderService.cs` and the indexer sees `inventory.Reserve()`, the target `InventoryService.Reserve` symbol may not be in the graph yet or may be ambiguous (multiple symbols named `Reserve` exist). A single-pass approach can't write that edge correctly. Two phases solve this cleanly.

---

## Phase 1 — Definitions pass

Walk every file. Per file:

1. Tree-sitter parse → AST → chunks
2. Write `File`, `Symbol`, `Module` nodes
3. Write `CONTAINS`, `DEFINES` edges (file-local — no cross-file resolution needed)
4. Write `IMPORTS` edges where the target resolves to a known `File` or `ExternalPackage`
5. **Capture but do not yet write** the call sites: every identifier that looks like a method/function invocation, with its scope and LLM-extracted call targets

These pending call sites land in a staging table — `UnresolvedCallSites`.

At the end of Phase 1, the graph holds every node it will ever have for this run. References are still pending.

---

## Phase 2 — Resolution pass

The full symbol table now exists. For each pending call site:

1. Look up candidate `Symbol` nodes by name
2. Filter candidates by reachability — only symbols in the same file/module, or in files reachable via the source file's `IMPORTS` edges
3. Score each candidate:
   - Exact name + signature match → high confidence
   - Name match only → medium confidence
   - Multiple matches → split into low-confidence edges across all candidates
   - LLM-extracted target agrees with name match → confidence boost
   - LLM target found, no name match → check whether the call site can be traced to a known `ExternalPackage`. Tag `["external_likely"]` if yes, `["unresolved"]` if no.
4. Write `REFERENCES` edges with `confidence` and `signals` properties

---

## Incremental hydration (the common case)

When `git diff` reports `OrderService.cs` changed:

1. **Definitions pass, scoped.** Re-parse the file. Diff chunk hashes against existing `Symbol.contentHash`. Update changed symbols, delete removed ones, add new ones.
2. **Forward edge invalidation.** Delete all `REFERENCES` edges *sourcing from* changed/removed symbols.
3. **Resolution pass, scoped.** Re-resolve outgoing call sites for the changed symbols only.
4. **Reverse invalidation.** Find `REFERENCES` edges from *other files* targeting changed/removed symbols in this file. Mark them dirty.
5. **Reverse resolution.** Re-resolve dirty edges. Source symbols haven't changed; only their target lookup needs to re-run.

### Invalidation policy (V1): pragmatic, not strict

Reverse invalidation only triggers on symbol deletion, rename, or visibility change — not on method body changes or signature changes. This is cheaper than strict invalidation (which would re-resolve on any signature change) and right for V1. The cost is occasional stale signature info in low-confidence scoring until the next full re-index — acceptable for a fuzzy index. Strict invalidation can be a configurable mode in V2 for users who want maximum precision.

---

## End-to-end indexing order (fresh index)

```
1. Repo Walker        → list of files + git state
2. Manifest Parser    → Project, ExternalPackage nodes; intra-repo project graph
3. Tree-sitter Parser → ASTs (per-file, pipelined)
4. Chunker            → Symbol nodes + per-chunk call-site list (staged)
5. Summarizer         → Symbol summaries, embeddings, LLM call targets (joins staging)
6. Phase 1 writer     → entities (files, symbols, modules) and structural edges (contains, defines, imports) via IGraphStore
7. Phase 2 resolver   → REFERENCES edges
8. Cluster worker     → Leiden + Cluster nodes (async, separate stage — see [[Agency.GraphRAG.Code.Clustering]])
```

Steps 3–5 pipeline per-file with no inter-file dependency. Steps 6 and 7 are the synchronization points where parallel work converges.

---

## Next: [[Agency.GraphRAG.Code.Clustering]] — Community detection and cluster summarization
