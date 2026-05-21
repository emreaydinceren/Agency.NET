<!-- last-wiki-commit: 49c891fedc9f015dc717e337cf99f4a89998e1fa -->
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
   Agency.VectorStore.Common          (IVectorStore interface)
   Agency.VectorStore.Sql.Postgre     (pgvector / HNSW backend)
   Agency.VectorStore.Sql.Sqlite      (SQLite + cosine UDF backend)
          │
   Agency.KeyValueStore.Common        (IKVStore interface — metadata/KV layer)
   Agency.KeyValueStore.Sql.Postgre   (PostgreSQL KV backend)
   Agency.KeyValueStore.Sql.Sqlite    (SQLite KV backend)
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

- [[Agency.VectorStore.Common]] — `IVectorStore`, `Query`, `SearchHit<T>`
- [[Agency.VectorStore.Sql.Postgre]] — pgvector + HNSW index backend
- [[Agency.VectorStore.Sql.Sqlite]] — SQLite + in-process cosine UDF backend

### Key-Value Store

- [[Agency.KeyValueStore.Common]] — `IKVStore`, `Query`, `SearchHit<T>`, JSON metadata helpers
- [[Agency.KeyValueStore.Sql.Postgre]] — PostgreSQL KV backend with metadata filtering
- [[Agency.KeyValueStore.Sql.Sqlite]] — SQLite KV backend with metadata filtering

### RAG

- [[Agency.RagFormatter]] — `Dataset.ToMarkdownTable()` for LLM context injection

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
