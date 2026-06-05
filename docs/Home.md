<!-- last-wiki-commit: 3c1bba6829de7c7a1b35d79880c248a40a519ef4 -->
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
   Agency.VectorStore.Sql.Postgres    (pgvector / HNSW backend)
   Agency.VectorStore.Sql.Sqlite      (SQLite + cosine UDF backend)
          │
   Agency.KeyValueStore.Common        (IKVStore interface — metadata/KV layer)
   Agency.KeyValueStore.Sql.Postgres  (PostgreSQL KV backend)
   Agency.KeyValueStore.Sql.Sqlite    (SQLite KV backend)
          │
          ▼
   Agency.Sql.Common                        (SqlRunnerBase: shared OTel + execution skeleton)
   Agency.Sql.Postgres / Agency.Sql.Sqlite  (raw SQL runner + vectorize() macro)
   Agency.Common                           (Dataset, IColumnMetadata)
   Agency.RagFormatter                     (Dataset → Markdown table)
          │
          ▼
   Agency.Llm.Common                  (IModelProvider interface + tool types)
   Agency.Llm.Claude                  (Anthropic SDK implementation)
   Agency.Llm.OpenAI                  (OpenAI SDK implementation)
          │
          ▼
   Agency.Harness                     (agent loop + lifecycle hooks + stop
                                       conditions + structured Context +
                                       tool registry + MCP client pool)
   Agency.Harness.Console             (interactive REPL chat harness)
   Agency.Console                     (one-shot RAG demo stub)
          │
          ▼
   Agency.Mcp.Memory                  (MCP server: scoped Memorize / Recall /
                                       Forget / ListGlobalKeys via IKVStore)
          │
          ▼
   Agency.Memory.Common               (Record, ContentType, IMemoryStore,
                                       RankingFormula, MemoryOptions)
   Agency.Memory.Sql.Postgres         (PostgreSQL + pgvector memory store)
   Agency.Memory.Retrieval            (gate + over-fetch + composite re-rank
                                       → ctx.Knowledge / ctx.Memory)
   Agency.Memory.Distiller            (async distillation pipeline)
   Agency.Memory.Consolidator         (merge/consolidate memory records)
   Agency.Memory.Hygiene              (TTL + low-importance pruning sweeper)
```

## Project Pages

### Foundations

- [[Projects/Agency.Common]] — Shared tabular data types (`Dataset` and `IColumnMetadata`). Zero dependencies, used by SQL runners, RAG formatting, and LLM context injection. **TLDR:** A simple schema for passing query results through the pipeline.
- [[Projects/Agency.Embeddings.Common]] — The `IEmbeddingGenerator` interface with a `BatchingEmbeddingGenerator` decorator to coalesce individual embedding requests into efficient batch API calls. **TLDR:** Turn text into vectors; batch 'em to reduce API round-trips.
- [[Projects/Agency.Llm.Common]] — Provider-agnostic contracts: `IModelProvider` to list available models, `LlmClientOptions` for configuration, and tool-calling types (`ITool`, `IToolRegistry`, `ToolDefinition`). **TLDR:** The shared language between all LLM providers and the agent.

### Embeddings

- [[Projects/Agency.Embeddings.OpenAI]] — OpenAI-compatible embeddings client with full OpenTelemetry instrumentation. Wraps the official .NET SDK and exposes duration, request counts, and token usage metrics. **TLDR:** Call your embedding model (LM Studio, OpenAI, etc.); get telemetry for free.

### SQL

- [[Projects/Agency.Sql.Common]] — Abstract base class `SqlRunnerBase` that provides shared OTel tracing, logging, and the core execute/query methods. Subclasses only implement the provider-specific connection and command building. **TLDR:** One skeleton for all SQL runners; providers just plug in their dialect.
- [[Projects/Agency.Sql.Postgres]] — PostgreSQL runner with built-in support for `vectorize(text)` macros to inline embedding generation directly in SQL queries. **TLDR:** Run SQL on Postgres; embed text inline without extra round-trips.
- [[Projects/Agency.Sql.Sqlite]] — SQLite runner with `vectorize(text)` macro support and a cosine-distance UDF for lightweight vector search. **TLDR:** Same thing but with SQLite—great for dev and testing.

### Vector Store

- [[Projects/Agency.VectorStore.Common]] — The `IVectorStore` interface for semantic search (upsert, search, delete), `Query` and `SearchHit<T>` models, and JSON metadata helpers. Results include similarity %, recency (minutes/hours), and can be converted to `Dataset` for RAG. **TLDR:** Unified interface for vector storage; works with any backend.
- [[Projects/Agency.VectorStore.Sql.Postgres]] — pgvector + HNSW index implementation with metadata filtering and session/user scoping. **TLDR:** Production-grade vector store on Postgres with fast approximate nearest-neighbor search.
- [[Projects/Agency.VectorStore.Sql.Sqlite]] — SQLite-backed vector store with in-process cosine-distance UDF and full-text substring search. **TLDR:** Lightweight vector store for development; good for single-machine or test environments.

### Key-Value Store

- [[Projects/Agency.KeyValueStore.Common]] — The `IKVStore` interface for general key-value storage (upsert, search by key/value/metadata, list keys, delete), `Query` and `SearchHit<TValue>` models, and Dataset conversion. No vector search—just text matching and metadata filtering. **TLDR:** A simple metadata store; pair it with a vector store or use standalone for structured facts.
- [[Projects/Agency.KeyValueStore.Sql.Postgres]] — PostgreSQL KV implementation with JSON metadata, session/user scoping, and substring value matching. **TLDR:** Durable key-value store on Postgres.
- [[Projects/Agency.KeyValueStore.Sql.Sqlite]] — SQLite KV implementation following the same pattern. **TLDR:** Lightweight key-value store for dev or single-machine use.

### RAG

- [[Projects/Agency.RagFormatter]] — Single method: `Dataset.ToMarkdownTable()` converts query results into Markdown tables ready for injection into LLM prompts. **TLDR:** Turn your query results into a prompt-friendly table.

### Ingestion

- [[Projects/Agency.Ingestion]] — Core abstractions (`IDocumentLoader`, `ITextSplitter`, `IIngestionPipeline<T>`) and a concrete `DefaultIngestionPipeline` that orchestrates load → split → convert → upsert into a vector store with parallelism, error tracking, and OTel observability. **TLDR:** Load documents, chunk 'em, vectorize, and store—all in one coordinated pipeline.
- [[Projects/Agency.Ingestion.FileSystem]] — Document loaders for files and directories with recursive traversal and configurable filters. **TLDR:** Read text files and directories into documents.
- [[Projects/Agency.Ingestion.SemanticKernel]] — Text splitter backed by Semantic Kernel's `TextChunker` with sliding-window chunking. **TLDR:** Split long documents into overlapping chunks.

### LLM Providers

- [[Projects/Agency.Llm.Claude]] — Anthropic Claude provider factory that creates `IChatClient` instances with OpenTelemetry and logging middleware. Also implements `IModelProvider` to list available Claude models. **TLDR:** Hook up to Claude; get tracing, logging, and model discovery out of the box.
- [[Projects/Agency.Llm.OpenAI]] — OpenAI-compatible provider (supports OpenAI, Azure OpenAI, local endpoints) with the same OTel and logging middleware. **TLDR:** Works with OpenAI and any OpenAI-compatible endpoint.

### Agent

- [[Projects/Agency.Harness]] — Autonomous agent loop: think (LLM call) → act (tool use) → observe (tool result) → repeat until a `StopCondition` fires. Features include lifecycle hooks (`OnPreToolUse` can allow/deny/rewrite tool calls), a structured `Context` (user input, history, tools, memory, grounding data), composable stop conditions (step count, token budget, no-tool-use), built-in tool registry with per-tool enable/disable, MCP client pool for external tools, and a typed `AgentEvent` stream so callers can react to each stage. **TLDR:** The core agent loop—handles multi-turn reasoning, tool use, and lifecycle events.
- [[Projects/Agency.Harness.Console]] — Interactive REPL chat harness for agents. Multi-turn conversations, token/cost tracking, streaming output, and session lifecycle management. **TLDR:** Chat with your agent in a terminal.
- [[Projects/Agency.Console]] — Stub executable that prints "Hello, World!". **TLDR:** A minimal entry point for testing the solution setup.

### MCP Servers

- [[Projects/Agency.Mcp.Memory]] — Standalone MCP server exposing four scoped memory tools (`Memorize`, `Recall`, `Forget`, `ListGlobalKeys`) backed by any `IKVStore` (SQLite or Postgres). Memory is partitioned by user and session, organized by domain, and queryable by key/value/tags. **TLDR:** Give your agent a persistent memory; runs as a separate MCP server.

### Long-Term Memory

- [[Projects/Agency.Memory.Common]] — Shared types for the memory system: `Record` (fact or episodic memory with embedding, importance, tags, timestamps), `ContentType` (Fact vs. Memory), `IMemoryStore`, search queries/hits, job payloads, and event types. Zero dependencies; all subsystems (Distiller, Retrieval, Consolidator, Hygiene) depend only on this. **TLDR:** The shared contract for all memory subsystems—one stable surface, no circular deps.
- [[Projects/Agency.Memory.Sql.Postgres]] — PostgreSQL memory store with pgvector for semantic search. Handles upsert keys, schema init, in-process caching, dead-lettering, and watermark advancement. **TLDR:** Production memory store—semantic search on Postgres with all the scaffolding.
- [[Projects/Agency.Memory.Retrieval]] — Read-path orchestration: gate check → query embedding → over-fetch → composite re-rank (similarity + recency + importance + session-match) → partition by `ContentType` → inject into agent `Context`. **TLDR:** Smart memory retrieval—combines semantic similarity, freshness, importance, and user session to serve the right facts and memories.
- [[Projects/Agency.Memory.Distiller]] — Async background job that extracts high-level insights (episodes, lessons, summaries) from conversation history via an LLM and stores them as new memory records. Per-session bounded channels, exponential backoff on failure, dead-letter queue for persistent failures. **TLDR:** Automatically learn from conversations—turn raw chat history into distilled insights.
- [[Projects/Agency.Memory.Consolidator]] — Sub-agent that reconciles duplicate or stale memory records using LLM-assisted merge/delete decisions. Triggered by mutations, runs per-user with an internal agent using four tools. **TLDR:** Keep memory clean—automatically merge duplicates and remove stale records.
- [[Projects/Agency.Memory.Hygiene]] — Background sweeper that deletes expired records (TTL-based) and low-importance stale items on a jittered schedule. Emits metrics and OTel spans per sweep. **TLDR:** Automatic cleanup—remove old and unimportant memories on a schedule.

## Cross-Cutting Concerns

### Observability

Every library exposes a named `ActivitySource` and `Meter` following the same pattern. Configure your OpenTelemetry pipeline with these source names to get distributed traces and metrics for every SQL query, embedding call, vector store operation, LLM request, and ingestion run. The agent loop adds its own instruments under `Agency.Harness.Agent` — counters for turns, errors, tool calls, and tokens, plus a turn-duration histogram (all tagged with `agent.model` and `agent.client_type`).

### Centralized Package Management

All NuGet package versions are pinned in `src/Directory.Build.props`. Add or update versions there; reference packages by name only (without version) in individual `.csproj` files.

### Testing

- Non-functional unit tests: `dotnet test src/Agency.slnx --filter "Category!=Functional"`
- Functional LLM tests (requires LM Studio): `dotnet test --filter "Category=Functional"`
- E2E console tests: part of `Agency.Harness.Console.Test` with `[Trait("Category", "Functional")]`
