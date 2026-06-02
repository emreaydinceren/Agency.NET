# Agency

A .NET 10 toolkit for building RAG pipelines and AI agents in idiomatic C#—featuring pluggable vector/KV stores, native MCP integration, and OpenTelemetry throughout.

[![NuGet](https://img.shields.io/nuget/v/Agency.Harness.svg)](https://www.nuget.org/packages/Agency.Harness)
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
- **Autonomous agent loop** — a readable think → act → observe loop driven by a structured `Context`, composable `StopConditions`, and a stream of typed `AgentEvent`s. An interactive REPL harness ships in the box.
- **Lifecycle hooks for governance** — five hooks (`OnSessionStarted`, `OnPreToolUse`, `OnPostToolUse`, `OnAssistantTurn`, `OnStop`) let you intercept the loop. `OnPreToolUse` can **Allow**, **Deny** (with a reason), or **Rewrite** a tool call's arguments before it runs. Pre-built hooks ship for command denylisting and audit logging.
- **Budget & token guardrails** — stop the loop on step count, no-more-tool-calls, accumulated USD cost, or total tokens. Compose any combination with `StopConditions.Any(...)`.
- **Stateful, structured context** — context is assembled from typed sub-contexts (query, temporal, environmental, user, knowledge, memory) rather than a raw prompt string. Domain `Knowledge.Facts` and `Memory.LongTermMemory` are re-injected into the system prompt on **every** loop iteration, so grounding never drifts out of the window.
- **Multi-turn sessions with per-turn timeouts** — `ChatSession` / `Agent.ChatAsync` preserve conversation history across turns; `AgentOptions.TurnTimeoutSeconds` bounds each turn.
- **Built-in tools + pluggable registry** — `read_file`, `write_file`, `execute_powershell`, and a `subagent_tool` ship out of the box behind a name-keyed `ToolRegistry` with per-tool enable/disable.
- **MCP in both directions** — the harness is an MCP **client** (`McpClientPool` connects to external stdio/HTTP MCP servers and exposes their tools to the agent) *and* ships an MCP **server** (`Agency.Mcp.Memory`). Consume any MCP server's tools; expose Agency's scoped memory to any MCP-aware host.
- **MCP memory server included** — `Agency.Mcp.Memory` exposes `Memorize`, `Recall`, `Forget`, and `ListGlobalKeys` over any `IKVStore`, with memory scoped by user/session, grouped by domain, and filterable by tags. Drop into Claude Desktop, Cline, or any MCP-aware client.
- **OpenTelemetry throughout** — every SQL query, embedding call, vector op, LLM request, agent turn, tool call, and ingestion run emits traces and metrics through named `ActivitySource` and `Meter` instances.

## Quick start

> The **agent, hooks, and MCP** snippets below (steps 3–6) are verified against the current public API — their constructor signatures and method names match the source. The **ingestion** snippet (step 2) shows the intended shape only; verify its type names and constructors against the current source before copying it. Runnable examples live in `examples/`.

### 1. Install

```bash
dotnet add package Agency.Harness
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

### 3. Run an agent

The agent takes an `IChatClient` (from `Microsoft.Extensions.AI`), a model id, and a structured `Context`. Use `ClaudeClient` / `OpenAIClient` to build the `IChatClient`, `Agent.CreateContext(...)` to assemble the typed context, and consume the `AgentEvent` stream:

```csharp
using Agency.Harness;
using Agency.Harness.Contexts;
using Agency.Harness.Tools;
using Agency.Llm.Claude;
using Agency.Llm.Common;
using Microsoft.Extensions.AI;

// 1. Build an IChatClient for your provider.
IChatClient chat = new ClaudeClient(new LlmClientOptions
{
    ClientType = "Claude",
    ApiKey     = anthropicApiKey,
}).CreateChatClient();

// 2. Register the tools the agent may call.
var registry = new ToolRegistry([new ReadFileTool(), new ExecutePowershellTool()]);
var tools    = new ToolContext { Registry = registry };

// 3. Create the agent and a context that seeds the conversation.
var agent = new Agent(chat, model: "claude-sonnet-4-5", clientType: "Claude");
Context ctx = Agent.CreateContext("List the .cs files under ./src", tools);

// 4. Drive the loop. RunAsync is internal; ChatAsync is the public per-turn entry point.
await foreach (AgentEvent ev in agent.ChatAsync("List the .cs files under ./src", ctx))
{
    switch (ev)
    {
        case ToolInvokedEvent t:
            Console.WriteLine($"[tool] {t.ToolName}");
            break;
        case AgentResultEvent r:
            Console.WriteLine($"{r.Status}: {r.FinalText}");
            break;
    }
}
```

For multi-turn conversations, prefer `ChatSession`, which owns the `Context` and preserves history across calls:

```csharp
var session = new ChatSession(agent, new AgentOptions { TurnTimeoutSeconds = 120 }, tools);

await foreach (AgentEvent ev in session.SendAsync("What changed in the last commit?"))
{
    // render events...
}
// History is retained; the next SendAsync continues the same conversation.
await foreach (AgentEvent ev in session.SendAsync("Now summarize it in one line."))
{
    // ...
}
```

### 4. Add grounding from retrieval

`Context` is assembled from typed sub-contexts rather than a raw system-prompt string. Retrieved documents and domain facts go into `KnowledgeContext.Facts`, which `SystemPromptBuilder` re-injects into the system prompt on **every** iteration:

```csharp
using Agency.Harness.Contexts;
using Agency.RagFormatter;

var question = "How do I configure the SQLite vector store?";
var hits     = await store.SearchAsync(await embedder.EmbedAsync(question), topK: 5);
string grounded = hits.ToMarkdownTable();

Context ctx = Agent.CreateContext(question, tools) with
{
    Knowledge = new KnowledgeContext { Facts = [grounded] },
};
```

### 5. Govern tool use with hooks

`OnPreToolUse` can allow, block, or rewrite a tool call before it runs. Ship-ready hooks cover the common cases, and `Compose` chains them (most-restrictive-wins for the pre-tool decision):

```csharp
using Agency.Harness.Hooks;

// Block known-dangerous shell patterns and log every tool call.
AgentHooks hooks = BlockListHooks.Dangerous.Compose(AuditHooks.ForLogger(logger));

var agent = new Agent(chat, model: "claude-sonnet-4-5", clientType: "Claude", hooks: hooks);
```

> `BlockListHooks.Dangerous` is a simple case-insensitive substring denylist (`rm -rf`, `drop table`, `format c:`, `del /f /s`) scoped to shell tools — a convenience guardrail, not a hardened security boundary. Treat it as defense-in-depth, not a sandbox.

### 6. Connect external MCP servers

The harness is itself an MCP client. `McpClientPool` connects to one or more external MCP servers and surfaces their tools as ordinary `ITool`s you can drop into the registry:

```csharp
using Agency.Harness.Tools;

await using McpClientPool pool = await McpClientPool.CreateAsync(new McpClientOptions
{
    Servers =
    [
        new McpServerConfig
        {
            Name      = "filesystem",
            Transport = McpTransportKind.Stdio,
            Command   = "npx",
            Arguments = ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"],
        },
    ],
});

var registry = new ToolRegistry([new ReadFileTool(), .. pool.Tools]);
```

### 7. Try the REPL

```bash
dotnet run --project src/Harness/Agency.Harness.Console
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
   Agency.Harness                     (agent loop, hooks, stop conditions,
                                       structured Context, tool registry,
                                       MCP client pool)
   Agency.Harness.Console             (interactive REPL)
   Agency.Console                     (one-shot RAG demo)
          │
          ▼
   Agency.Mcp.Memory                  (MCP server: Memorize / Recall /
                                       Forget / ListGlobalKeys)
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
| `Agency.Harness` | Agent loop, structured `Context`, `StopConditions`, lifecycle `AgentHooks`, `ToolRegistry` + built-in tools, `McpClientPool` (MCP client), `AgentEvent` stream |
| `Agency.Harness.Console` | Multi-turn interactive REPL |
| `Agency.Console` | One-shot RAG demo |
| `Agency.Mcp.Memory` | MCP server: scoped `Memorize` / `Recall` / `Forget` / `ListGlobalKeys` |

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

The agent loop (`ActivitySource`/`Meter` named `Agency.Harness.Agent`) emits these instruments, tagged with `agent.model` and `agent.client_type`:

| Instrument | Name |
| --- | --- |
| Counter | `agent.turns` |
| Counter | `agent.errors` |
| Counter | `agent.tool.calls` (adds `agent.tool.name`, `agent.tool.error`) |
| Counter | `agent.tokens` (adds `agent.token.type` = `input`/`output`) |
| Histogram | `agent.turn.duration` (ms) |

## Testing

```bash
# Unit tests — fast, no external dependencies
dotnet test src/Agency.slnx --filter "Category!=Functional"

# Functional tests — require a running LLM endpoint (e.g. LM Studio on localhost)
dotnet test --filter "Category=Functional"
```

Functional tests are tagged `[Trait("Category", "Functional")]` so CI excludes them by default and they run on demand.

## Using the MCP memory server

`Agency.Mcp.Memory` is a stdio MCP server. Point any MCP client at it to get four memory tools backed by an `IKVStore` (SQLite or PostgreSQL):

| Tool | Purpose |
| --- | --- |
| `Memorize` | Store a value under a composite `{domain}\|{key}`, scoped to a user/session, with optional tags. |
| `Recall` | Retrieve entries filtered by scope, domain, key, and/or tags. |
| `Forget` | Delete the entry identified by `{domain}\|{key}` within a scope. |
| `ListGlobalKeys` | Index distinct keys and tags grouped by domain for a user's global (session-wide) scope. |

Memory is not a flat key-value bag: every entry is partitioned by a `MemoryScope(UserId, SessionId)`, grouped by `Domain`, identified by `Key`, and filterable by `Tags`. A `null` `SessionId` denotes a user-wide (global) scope, which `ListGlobalKeys` targets so an agent can discover what's persisted before issuing a targeted `Recall`.

Example client config (Claude Desktop / Cline format):

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

The agent harness can also **consume** any MCP server (including this one) via `McpClientPool` — see step 6 of the Quick start.

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
