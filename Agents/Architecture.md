# Architecture

This is a .NET 10 solution that provides a RAG (Retrieval-Augmented Generation) pipeline: generate embeddings → store in PostgreSQL/pgvector → query → format results → send to LLM.

## Project Dependency Graph

```
Agency.Common               (base: Dataset, IEmbeddingGenerator)
    ↓
Agency.Embeddings           (IEmbeddingGenerator → OpenAI-compatible API)
Agency.SQL                  (PostgreSQL + pgvector; SQLQueryEmbedder injects embeddings)
Agency.RagFormatter         (Dataset → Markdown table for LLM context)
Agency.Llm.Abstractions     (ILlmClient interface)
    ↓
Agency.Llm.Claude           (Anthropic SDK implementation)
Agency.Llm.OpenAI           (OpenAI SDK implementation)
```

## ILlmClient Abstraction

All LLM providers implement `ILlmClient` (`Agency.Llm.Abstractions`):

- `SendAsync()` — single request/response
- `StreamAsync()` — returns `IAsyncEnumerable<string>` chunks

Both `ClaudeClient` and `OpenAIClient` follow the same structure: constructor takes `IOptions<TOptions>` and `ILogger<T>`, with full OpenTelemetry instrumentation (ActivitySource + Meter with request count, error count, duration, and token usage histograms).

## Observability Pattern

Every client (LLM and embeddings) exposes:

- An `ActivitySource` for distributed tracing
- A `Meter` with counters (`requests`, `errors`) and histograms (`duration`, `tokens.input`, `tokens.output`)
- Tags include: `system`, `model`, `method`, `token_type`

## SQLQueryEmbedder

`Agency.SQL/SQLQueryEmbedder.cs` uses regex to find `vectorize('<text>')` placeholders in SQL queries and replaces them with pgvector literal format (`[f1,f2,...]`) using the injected `IEmbeddingGenerator`. This allows writing SQL like:

```sql
SELECT * FROM docs ORDER BY embedding <-> vectorize('search query') LIMIT 5
```

## Infrastructure

- PostgreSQL 18 + pgvector extension via Docker (`src/docker-compose.yml`)
- Credentials: `dev_user` / `dev_password`, database: `dev_db`, port `5432`
- Functional LLM tests target LM Studio at `http://llm-host.example:1234`
