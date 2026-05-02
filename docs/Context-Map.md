# Agency — Context Map

#agency #index #rag #agentic #dotnet

A .NET 10 toolkit for building RAG (Retrieval-Augmented Generation) pipelines and autonomous AI agents. The solution provides a layered set of libraries — each with a single, well-defined responsibility — that compose into a complete pipeline: embed → store → retrieve → format → chat.

## Architecture

```ASCIIDOC
Documents
   └─ Agency.Ingestion.FileSystem     (load files)
   └─ Agency.Ingestion.SemanticKernel (chunk text)
   └─ Agency.Ingestion                (orchestrate pipeline)
          │
          ▼
   Agency.Embeddings.Common           (IEmbeddingGenerator interface)
   Agency.Embeddings.OpenAI           (OpenAI-compatible implementation)
          │
          ▼
   Agency.VectorStore.Common          (IKVStore interface)
   Agency.VectorStore.Sql.Postgre     (pgvector / HNSW backend)
   Agency.VectorStore.Sql.Sqlite      (SQLite + cosine UDF backend)
          │
          ▼
   Agency.Sql.Common                        (SqlRunnerBase: shared OTel + execution skeleton)
   Agency.Sql.Postgre / Agency.Sql.Sqlite  (raw SQL runner + vectorize() macro)
   Agency.Common                           (Dataset, IColumnMetadata)
   Agency.RagFormatter                     (Dataset → Markdown table)
          │
          ▼
   Agency.Llm.Common                  (IModelProvider interface + tool types)
   Agency.Llm.Claude                  (Anthropic SDK implementation)
   Agency.Llm.OpenAI                  (OpenAI SDK implementation)
          │
          ▼
   Agency.Agentic                     (autonomous agent loop)
   Agency.Agentic.Console             (interactive REPL chat harness)
   Agency.Console                     (one-shot RAG demo stub)
          │
          ▼
   Agency.GraphRAG.Code               (code graph indexing, clustering, query)
   Agency.GraphRAG.Code.Sqlite        (SQLite IGraphStore + FTS5 + sqlite-vec)
   Agency.GraphRAG.Code.Postgres      (PostgreSQL IGraphStore + pgvector)
   Agency.GraphRAG.Code.Cli           (index / query CLI)
   Agency.GraphRAG.Code.TreeSitter    (Tree-sitter sidecar: AST parsing)
          │
          ▼
   Agency.Mcp.Memory                  (MCP server: Memorize / Recall / Forget via IKVStore)
```

## Project Pages

### Foundations

- [[Agency.Common]] — `Dataset` and `IColumnMetadata`; zero-dependency shared types
- [[Agency.Embeddings.Common]] — `IEmbeddingGenerator` interface
- [[Agency.Llm.Common]] — `IModelProvider`, tool types

### Embeddings

- [[Agency.Embeddings.OpenAI]] — OpenAI-compatible embedding generator with OTel

### SQL

- [[Agency.Sql.Common]] — `SqlRunnerBase` abstract class: shared OTel telemetry + execution skeleton
- [[Agency.Sql.Postgre]] — PostgreSQL runner + `vectorize()` macro
- [[Agency.Sql.Sqlite]] — SQLite runner + `vectorize()` macro

### Vector Store

- [[Agency.VectorStore.Common]] — `IKVStore`, `Query`, `SearchHit<T>`
- [[Agency.VectorStore.Sql.Postgre]] — pgvector + HNSW index backend
- [[Agency.VectorStore.Sql.Sqlite]] — SQLite + in-process cosine UDF backend

### RAG

- [[Agency.RagFormatter]] — `Dataset.ToMarkdownTable()` for LLM context injection

### Code Graph RAG

The code graph indexing layer enables LLM agents to understand large, polyglot code repositories **without compilation** by building a queryable knowledge graph: code → chunks → embeddings + summaries → structured graph (entities + relationships) → hybrid retrieval (vector + graph traversal + community clusters).

**Design & Architecture:**

- [[Agency.GraphRAG.Code.Overview]] — system overview, design principles, architecture diagram
- [[Agency.GraphRAG.Code.Design]] — tradeoff analysis, V1 scope vs. V2+, performance assumptions

**Core Layers:**

- [[Agency.GraphRAG.Code.Indexing]] — Repo Walker, Tree-sitter Parser, Manifest Parser, Chunker, Summarizer, Change Detector
- [[Agency.GraphRAG.Code.Hydration]] — two-phase indexing, reference resolution, incremental updates
- [[Agency.GraphRAG.Code.Clustering]] — Leiden-based community detection, boundary-aware tuning, utility node handling, cluster summarization
- [[Agency.GraphRAG.Code.Querying]] — query planner, hybrid retriever, context assembly, LLM synthesis
- [[Agency.GraphRAG.Code.Storage]] — IGraphStore abstraction, schema design, indexes, Postgres vs. SQLite comparison, reference signal taxonomy

**API & Implementations:**

- [[Agency.GraphRAG.Code]] — core API surface and interfaces (ICodeIndex, IGraphStore)
- [[Agency.GraphRAG.Code.Sqlite]] — SQLite-backed `IGraphStore` with FTS5 fuzzy name matching and `sqlite-vec` for vector search; zero-setup default for single-developer and repos < 100k symbols
- [[Agency.GraphRAG.Code.Postgres]] — PostgreSQL-backed `IGraphStore` with pgvector (HNSW) for high-performance vector search and `pg_trgm` for trigram fuzzy matching; recommended for large repos and team scenarios
- [[Agency.GraphRAG.Code.Cli]] — `index` / `query` CLI: local indexing (SQLite file) and remote indexing (Postgres connection string config)
- [[Agency.GraphRAG.Code.TreeSitter]] — Tree-sitter out-of-process client for polyglot AST parsing (C#, TypeScript, Python); decouples grammar updates from .NET build

### Ingestion

- [[Agency.Ingestion]] — abstractions + `DefaultIngestionPipeline<T>`
- [[Agency.Ingestion.FileSystem]] — file and directory loaders
- [[Agency.Ingestion.SemanticKernel]] — SK `TextChunker`-based splitter

### LLM Providers

- [[Agency.Llm.Claude]] — Anthropic Claude via Stainless SDK
- [[Agency.Llm.OpenAI]] — OpenAI-compatible via official .NET SDK

### Agent

- [[Agency.Agentic]] — autonomous agent loop, `Context`, `StopConditions`, `AgentEvent`
- [[Agency.Agentic.Console]] — multi-turn interactive REPL chat harness
- [[Agency.Console]] — one-shot RAG demo stub

### MCP Servers

- [[Agency.Mcp.Memory]] — stdio MCP server exposing `Memorize` / `Recall` / `Forget` tools backed by `IKVStore`

## Cross-Cutting Concerns

### Observability

Every library exposes a named `ActivitySource` and `Meter` following the same pattern. Configure your OpenTelemetry pipeline with these source names to get distributed traces and metrics for every SQL query, embedding call, vector store operation, LLM request, and ingestion run.

### Centralized Package Management

All NuGet package versions are pinned in `src/Directory.Build.props`. Add or update versions there; reference packages by name only (without version) in individual `.csproj` files.

### Testing

- Non-functional unit tests: `dotnet test src/Agency.slnx --filter "Category!=Functional"`
- Functional LLM tests (requires LM Studio): `dotnet test --filter "Category=Functional"`
- E2E console tests: part of `Agency.Agentic.Console.Test` with `[Trait("Category", "Functional")]`
