# Agency.GraphRAG.Code — Query Pipeline

#graphrag #querying #retrieval #hybrid-search #rag

The query pipeline answers natural-language questions about code by classifying the question, retrieving relevant context using hybrid search, and synthesizing answers through an LLM.

## Query Planner

Classifies the question into one of five types:

### Local queries

"What does method X do?" → vector search + 1-hop graph expansion

### Subsystem queries

"How does auth work?" → cluster summaries + symbol drill-down

### Global queries

"What does this codebase do?" → primary `business` cluster summaries; `infrastructure` clusters mentioned only as a footer

For Global queries, the planner uses `clusters.type` to drive pruning: `business` clusters lead, `infrastructure` clusters summarized in aggregate, `mixed` clusters flagged with a confidence note.

### Impact queries

"What calls X?" → graph traversal from a known symbol, no vector search needed

### Dependency queries

"What uses package X?", "What's affected if I upgrade Y?" → traversal over `depends_on` and `imports` edges, plus `references` edges with `external_likely` signal for framework-call queries

---

## Hybrid Retriever

For local/subsystem queries:

1. Embed the question
2. Vector search over `symbols.embedding` → top-k symbols
3. Edge expansion: 1–2 hops along `references`, `contains`, `imports` edges (filtered by confidence threshold) — implemented as recursive CTE
4. Pull cluster summary for each retrieved symbol's community

Both vector search and edge traversal are exposed as `IGraphStore` operations; the Hybrid Retriever composes them without knowing which store is underneath. On Postgres, vector search and traversal can be combined into a single query; on SQLite, they run as two queries with the application joining results — same logical result, different cost profile.

---

## Context Assembler

Assembles the retrieved results into a coherent context for the LLM:

- Deduplicates retrieved chunks
- Orders by relevance + structural locality (chunks from the same file/class kept together)
- Truncates to fit target context budget
- Includes: cluster summaries (high-level orientation) → symbol summaries (mid-level) → raw code (specifics)

---

## LLM Synthesis

The agent receives the assembled context and answers. The system prompt tells it the context comes from a fuzzy index and to flag uncertainty when reference confidence is low.

---

## Next: [[Agency.GraphRAG.Code.Storage]] — Schema, indexes, and storage abstraction
