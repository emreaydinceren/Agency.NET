# Agency.GraphRAG.Code — Design Overview

#graphrag #code-index #codegraph #rag #sqlite #fts #vector

Enable an LLM agent to answer questions about a large, possibly polyglot code repository **quickly and accurately**, including questions about structure, dependencies, and call relationships — without requiring the code to compile and without depending on language servers.

## Example queries the system must serve

- "How does authentication work in this repo?"
- "What modules depend on the payments service?"
- "What would break if I change the signature of `UserService.Authenticate`?"
- "Give me a tour of the codebase."
- "Where is feature X implemented?"

---

## Design Principles

1. **Build-independent.** The indexer must work on broken, half-written, or partially-checked-in code.
2. **Polyglot from day one.** Tree-sitter for everything; no per-language hand-coded analyzers.
3. **Honest fuzziness.** Where symbol resolution is ambiguous, surface confidence scores rather than fake certainty.
4. **Hybrid retrieval.** Combine vector search, graph traversal, and pre-computed cluster summaries — no single signal is enough.
5. **Incremental where it matters, batch where it doesn't.** File-level updates are incremental; cluster summaries rebuild on a schedule.

---

## Architecture Overview

```ascii
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
└──────────────────────────────────────────────────┬──────┘
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

## System Layers

The code graph system has six core layers, each documented separately:

1. **[[Agency.GraphRAG.Code.Indexing]]** — transforms raw repository files into semantic chunks with summaries and dependency information
2. **[[Agency.GraphRAG.Code.Hydration]]** — two-phase process that resolves cross-file references and builds the call graph
3. **[[Agency.GraphRAG.Code.Clustering]]** — community detection with boundary-aware tuning to group related code into coherent subsystems
4. **[[Agency.GraphRAG.Code.Querying]]** — hybrid retrieval that answers questions by combining vector search, graph traversal, and cluster summaries
5. **[[Agency.GraphRAG.Code.Storage]]** — abstraction layer over SQLite and PostgreSQL with schema design and reference signal taxonomy
6. **[[Agency.GraphRAG.Code.Design]]** — design tradeoffs, open questions, and V1 scope vs. V2+ roadmap
