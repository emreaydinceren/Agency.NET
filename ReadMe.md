# Agency

A .NET 10 toolkit for building RAG pipelines and AI agents in idiomatic C#—featuring pluggable vector/KV stores, native MCP integration, and OpenTelemetry throughout.

[![NuGet](https://img.shields.io/nuget/v/Agency.Agentic.svg)](https://www.nuget.org/packages/Agency.Agentic)
[![License](https://img.shields.io/github/license/YOUR-GH-USERNAME/Agency.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)

Agency is a layered set of single-responsibility libraries that compose into a complete RAG and agent pipeline for .NET: **embed → store → retrieve → format → chat**. Every component is an interface; every backend is swappable.

## Why Agency

The .NET ecosystem has a real gap. Most production-quality RAG and agent tooling lives in Python; the C# alternatives are usually thin wrappers around Python services or abstraction-first frameworks where the actual control flow disappears under five layers of indirection.

Agency takes a different stance:

- **Explicit over magical.** The agent loop is a `while` loop you can read. ReAct steps are visible C# code, not configuration buried under attributes.
- **Layered, not monolithic.** Embeddings, vector storage, KV storage, LLM providers, ingestion, agents, and MCP are each a separate package behind an interface. Pick what you need; ignore the rest.
- **Production-shaped from day one.** OpenTelemetry on every operation. Tests categorized. Package versions centralized. No hidden globals.
- **Modern .NET.** Built on .NET 10. Uses the official Anthropic and OpenAI SDKs. Uses Semantic Kernel where it earns its place (semantic chunking). Uses the official MCP C# SDK.

If you've ever wanted a readable reference implementation of a RAG + agent stack in idiomatic C#, this is meant to be that.

## Features

- **RAG pipeline** — ingestion, chunking, embedding, vector retrieval, and Markdown formatting for context injection.
- **Pluggable vector stores** — PostgreSQL with `pgvector` + HNSW, or SQLite with an in-process cosine UDF.
- **Pluggable KV stores** — same backends, for metadata filtering and short-term agent memory.
- **Two first-class LLM providers** — Anthropic Claude and OpenAI (also covers any OpenAI-compatible endpoint, including LM Studio and Ollama).
- **Autonomous agent loop** — `Context`, `StopConditions`, `AgentEvent`. An interactive REPL harness ships in the box.
- **MCP server included** — `Agency.Mcp.Memory` exposes `Memorize`, `Recall`, and `Forget` tools over any `IKVStore`. Drop into Claude Desktop, Cline, or any MCP-aware client.
- **OpenTelemetry throughout** — every SQL query, embedding call, vector op, LLM request, and ingestion run emits traces and metrics through named `ActivitySource` and `Meter` instances.

## Quick start

> ⚠️ The snippets below are illustrative and show the shape of the API. Verify constructor signatures and method names against the current source before publishing your own quickstart. Runnable examples live in `examples/`.

### 1. Install

```bash
dotnet add package Agency.Agentic
dotnet add package Agency.Llm.Claude              # or Agency.Llm.OpenAI
dotnet add package Agency.Embeddings.OpenAI
dotnet add package Agency.VectorStore.Sql.Sqlite  # or .Postgres
dotnet add package Agency.Ingestion.SemanticKernel
```

### 2. Ingest documents

```csharp
using Agency.Ingestion;
using Agency.Ingestion.FileSystem;
using Agency.Ingestion.SemanticKernel;
using Agency.Embeddings.OpenAI;
using Agency.VectorStore.Sql.Sqlite;

var embedder = new OpenAIEmbeddingGenerator(apiKey, "text-embedding-3-small");
var store    = new SqliteVectorStore("Data Source=agency.db");
var chunker  = new SemanticKernelChunker(maxTokens: 512);

var pipeline = new DefaultIngestionPipeline<string>(
    loader:   new DirectoryLoader("./docs"),
    chunker:  chunker,
    embedder: embedder,
    store:    store);

await pipeline.RunAsync();
```

### 3. Chat with retrieval

```csharp
using Agency.Agentic;
using Agency.Llm.Claude;
using Agency.RagFormatter;

var llm = new ClaudeModelProvider(anthropicApiKey, model: "claude-sonnet-4-5");

var question = "How do I configure the SQLite vector store?";
var hits     = await store.SearchAsync(await embedder.EmbedAsync(question), topK: 5);
var grounded = hits.ToMarkdownTable();

var context = new Context(systemPrompt: "Answer using only the provided context.");
context.AddUser($"Context:\n{grounded}\n\nQuestion: {question}");

var agent = new Agent(llm, stopConditions: StopConditions.NoToolCall);

await foreach (var ev in agent.RunAsync(context))
    Console.WriteLine(ev);
```

### 4. Try the REPL

```bash
dotnet run --project src/Agency.Agentic.Console
```

## Architecture

```
Documents
   └─ Agency.Ingestion.FileSystem     (load files)
   └─ Agency.Ingestion.SemanticKernel (chunk text)
   └─ Agency.Ingestion                (orchestrate pipeline)
          │
          ▼
   Agency.Embeddings.Common           (IEmbeddingGenerator)
   Agency.Embeddings.OpenAI           (OpenAI-compatible)
          │
          ▼
   Agency.VectorStore.Common          (IVectorStore)
   Agency.VectorStore.Sql.Postgres     (pgvector / HNSW)
   Agency.VectorStore.Sql.Sqlite      (SQLite + cosine UDF)
          │
   Agency.KeyValueStore.Common        (IKVStore)
       Agency.KeyValueStore.Sql.Postgres
   Agency.KeyValueStore.Sql.Sqlite
          │
          ▼
   Agency.Sql.Common                  (SqlRunnerBase: OTel + execution)
   Agency.Sql.Postgres / Agency.Sql.Sqlite
   Agency.Common                      (Dataset, IColumnMetadata)
   Agency.RagFormatter                (Dataset → Markdown)
          │
          ▼
   Agency.Llm.Common                  (IModelProvider + tool types)
   Agency.Llm.Claude                  (Anthropic SDK)
   Agency.Llm.OpenAI                  (OpenAI SDK)
          │
          ▼
   Agency.Agentic                     (autonomous agent loop)
   Agency.Agentic.Console             (interactive REPL)
   Agency.Console                     (one-shot RAG demo)
          │
          ▼
   Agency.Mcp.Memory                  (MCP server: Memorize / Recall / Forget)
```

## Packages

| Package | Purpose |
| --- | --- |
| `Agency.Common` | `Dataset`, `IColumnMetadata`; zero-dependency shared types |
| `Agency.Embeddings.Common` | `IEmbeddingGenerator` interface |
| `Agency.Embeddings.OpenAI` | OpenAI-compatible embedding generator |
| `Agency.VectorStore.Common` | `IVectorStore`, `Query`, `SearchHit<T>` |
| `Agency.VectorStore.Sql.Postgres` | pgvector + HNSW backend |
| `Agency.VectorStore.Sql.Sqlite` | SQLite + in-process cosine UDF |
| `Agency.KeyValueStore.Common` | `IKVStore`, JSON metadata helpers |
| `Agency.KeyValueStore.Sql.Postgres` | PostgreSQL KV backend |
| `Agency.KeyValueStore.Sql.Sqlite` | SQLite KV backend |
| `Agency.Sql.Common` | `SqlRunnerBase`: OTel + execution skeleton |
| `Agency.Sql.Postgres` | PostgreSQL runner + `vectorize()` macro |
| `Agency.Sql.Sqlite` | SQLite runner + `vectorize()` macro |
| `Agency.RagFormatter` | `Dataset.ToMarkdownTable()` for context injection |
| `Agency.Ingestion` | Abstractions + `DefaultIngestionPipeline<T>` |
| `Agency.Ingestion.FileSystem` | File and directory loaders |
| `Agency.Ingestion.SemanticKernel` | SK `TextChunker`-based splitter |
| `Agency.Llm.Common` | `IModelProvider`, tool types |
| `Agency.Llm.Claude` | Anthropic Claude provider |
| `Agency.Llm.OpenAI` | OpenAI / OpenAI-compatible provider |
| `Agency.Agentic` | Autonomous agent loop, `Context`, `StopConditions`, `AgentEvent` |
| `Agency.Agentic.Console` | Multi-turn interactive REPL |
| `Agency.Console` | One-shot RAG demo |
| `Agency.Mcp.Memory` | MCP server: `Memorize` / `Recall` / `Forget` |

## Observability

Every library exposes a named `ActivitySource` and `Meter`. Wire them into your OpenTelemetry pipeline to get distributed traces and metrics on every operation:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddSource("Agency.*")
        .AddOtlpExporter())
    .WithMetrics(m => m
        .AddMeter("Agency.*")
        .AddOtlpExporter());
```

## Testing

```bash
# Unit tests — fast, no external dependencies
dotnet test src/Agency.slnx --filter "Category!=Functional"

# Functional tests — require a running LLM endpoint (e.g. LM Studio on localhost)
dotnet test --filter "Category=Functional"
```

Functional tests are tagged `[Trait("Category", "Functional")]` so CI excludes them by default and they run on demand.

## Using the MCP memory server

`Agency.Mcp.Memory` is a stdio MCP server. Point any MCP client at it to get `Memorize` / `Recall` / `Forget` tools backed by an `IKVStore`. Example client config (Claude Desktop / Cline format):

```json
{
  "mcpServers": {
    "agency-memory": {
      "command": "dotnet",
      "args": ["run", "--project", "path/to/Agency.Mcp.Memory"]
    }
  }
}
```

## Status

Pre-1.0 and under active development. Interfaces are stabilizing but may still shift between minor versions — pin package versions if you depend on this in anything you care about.

## Roadmap

- [ ] Additional vector store backends (Qdrant, Weaviate)
- [ ] Additional LLM providers
- [ ] Streaming token support across all providers
- [ ] [Add your specific roadmap items]

## Contributing

Issues and PRs welcome. For non-trivial changes, open an issue first so we can talk through the design before code gets written.

## License

[Add a LICENSE file. MIT and Apache-2.0 are the standard choices for libraries you want adopted.]

## Author

Built by [Your Name] — [LinkedIn / personal site / GitHub profile].
