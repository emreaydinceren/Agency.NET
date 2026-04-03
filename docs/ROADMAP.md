# Agency — Roadmap & Spec

> A .NET 10 AI toolkit: embeddings → vector search → RAG → tool-calling agents → interactive UIs.

---

## Overview

**Agency** is a modular, open-source .NET 10 toolkit for building AI-powered applications — from embedding generation and vector search all the way up to autonomous agents with tool calling, graph-based memory, and rich user interfaces.

### What Problem Does This Solve?

The .NET ecosystem has plenty of thin wrappers around OpenAI's API. What it lacks is a **composable, production-grade toolkit** that covers the full AI application stack:

- Generate embeddings from any OpenAI-compatible provider (cloud or local)
- Store and search vectors in PostgreSQL with pgvector — no managed vector DB needed
- Build knowledge graphs in Neo4j with native vector indexes for hybrid retrieval
- Define tools in C#, connect to external MCP servers, and let LLMs use them autonomously
- Run multi-turn agent loops with conversation context, middleware, and token budgets
- Expose everything as MCP servers so other AI tools (Claude Code, Cursor, VS Code Copilot) can consume it
- Ship interactive experiences via a terminal REPL or a cross-platform MAUI Blazor app

Each layer is an independent NuGet package. Use one, use all — no forced coupling.

### Design Principles

| Principle | What It Means |
|-----------|--------------|
| **Composable** | Each project is a standalone package. `Agency.Tools` doesn't force you into `Agency.Agent`. Pick what you need. |
| **Provider-agnostic** | Abstractions (`ILlmClient`, `IKVStore`, `IGraphStore`, `IEmbeddingGenerator`) with swappable backends. Claude, OpenAI, local models — same interface. |
| **Infrastructure-light** | PostgreSQL and Neo4j — both run in Docker. No proprietary cloud services required. Works fully offline with local LLMs via LM Studio. |
| **Observable by default** | Every component ships with OpenTelemetry `ActivitySource` + `Meter`. Traces, metrics, and token accounting out of the box. |
| **MCP-native** | The agent consumes external tools via MCP. The toolkit exposes its own capabilities as MCP servers. Both directions, first-class. |

### Who Is This For?

- **.NET developers** building RAG pipelines, chatbots, or AI agents who want library-level control instead of framework-level opinions
- **Teams** that need to run AI workloads on-premises or with local models alongside cloud providers
- **Developers exploring AI** who want a well-structured codebase to learn from — every component is documented, tested, and instrumented

### Architecture at a Glance

```
┌─────────────────────────────────────────────────────────────────┐
│  User Interfaces                                                │
│  ┌──────────────┐  ┌────────────────────────┐                   │
│  │ Console REPL │  │ MAUI Blazor Hybrid App │                   │
│  └──────┬───────┘  └───────────┬────────────┘                   │
├─────────┴──────────────────────┴────────────────────────────────┤
│  Agent Layer                                                    │
│  ┌────────────┐  ┌───────────┐  ┌────────────┐                 │
│  │ Agent Loop │  │ MCP Client│  │ MCP Server │                 │
│  └─────┬──────┘  └─────┬─────┘  └─────┬──────┘                 │
├────────┴────────────────┴──────────────┴────────────────────────┤
│  Core Capabilities                                              │
│  ┌──────────────┐ ┌───────┐ ┌─────────────────┐ ┌───────────┐  │
│  │ Conversation │ │ Tools │ │ RAG + Memory    │ │ Embeddings│  │
│  └──────────────┘ └───────┘ │ (Vector + Graph)│ └───────────┘  │
│                              └────────┬────────┘                │
├───────────────────────────────────────┴─────────────────────────┤
│  Storage Backends                                               │
│  ┌──────────────────────┐  ┌───────────────────┐                │
│  │ PostgreSQL + pgvector│  │ Neo4j (graph +    │                │
│  │ (vector store, SQL)  │  │  vector indexes)  │                │
│  └──────────────────────┘  └───────────────────┘                │
├─────────────────────────────────────────────────────────────────┤
│  LLM Providers                                                  │
│  ┌─────────┐  ┌────────┐  ┌─────────────────────┐              │
│  │ Claude  │  │ OpenAI │  │ Local (LM Studio)   │              │
│  └─────────┘  └────────┘  └─────────────────────┘              │
└─────────────────────────────────────────────────────────────────┘
```

---

## What Exists Today

| Layer | Project | Status |
|-------|---------|--------|
| Embeddings | `Agency.Embeddings.OpenAI` | Done — OpenAI-compatible (LM Studio) |
| Vector Store | `Agency.VectorStore.Sql.Postgre` | Done — pgvector, HNSW, metadata filtering |
| SQL + Vectorize | `Agency.Sql.Postgre` | Done — `vectorize('…')` placeholder replacement |
| RAG Formatting | `Agency.RagFormatter` | Done — Dataset → Markdown tables |
| LLM Clients | `Agency.Llm.Claude`, `Agency.Llm.OpenAI` | Done — Send + Stream, full telemetry |
| Observability | All projects | Done — ActivitySource + Meter everywhere |

---

## Phase 1 — Conversation & Context

**Goal:** Multi-turn conversations with persistent message history.

### New Project: `Agency.Conversation`

```
Agency.Llm.Common (ILlmClient, LlmResponse)
    ↓
Agency.Conversation (message history, context window management)
```

### Components

| Component | Purpose |
|-----------|---------|
| `ChatMessage` | Role (System / User / Assistant / Tool), Content, ToolCalls[], Metadata |
| `Conversation` | Ordered list of `ChatMessage`, add/trim/serialize |
| `IContextStrategy` | Controls what fits in the context window |
| `SlidingWindowStrategy` | Keep last N tokens, always retain system prompt |
| `SummarizationStrategy` | Compress older messages via LLM (advanced, Phase 3) |

### Why This Comes First

Every subsequent feature (tool calling, agents, UI) needs multi-turn message history. Without it, you'd be duplicating conversation tracking in every consumer.

### New Dependencies

None — this is pure C# over your existing `LlmResponse` / `StopReason` types.

---

## Phase 2 — Tool Calling

**Goal:** Define tools, detect when the LLM requests one, execute it, feed results back.

### New Project: `Agency.Tools`

```
Agency.Llm.Common
    ↓
Agency.Tools (tool definitions, registry, execution)
```

### Components

| Component | Purpose |
|-----------|---------|
| `ToolDefinition` | Name, Description, JSON Schema for parameters |
| `[Tool]` attribute | Decorate C# methods → auto-generate `ToolDefinition` |
| `IToolRegistry` | Register/lookup tools by name |
| `ToolResult` | Success/Error + string content returned to the LLM |
| `IToolExecutor` | Resolve tool name → invoke method → return `ToolResult` |

### How It Integrates With LLM Clients

Both Anthropic and OpenAI SDKs accept tool definitions in their request payloads. The changes needed:

1. **Extend `ILlmClient`** — add overloads that accept `IEnumerable<ToolDefinition>` and return structured tool-use responses (not just string content).
2. **Map `ToolDefinition` → provider format** — Anthropic uses `tools: [{ name, description, input_schema }]`, OpenAI uses `tools: [{ type: "function", function: { name, description, parameters } }]`. Each client maps internally.
3. **Parse tool-use responses** — when `StopReason == ToolUse`, deserialize the tool call name + arguments from the provider response.

### Built-in Tools (ship with the toolkit)

| Tool | Description |
|------|-------------|
| `SqlQueryTool` | Execute SQL via `PostgreSqlRunner`, return `Dataset` as markdown |
| `VectorSearchTool` | Semantic search via `IKVStore`, return top-K results |
| `FileReadTool` | Read file contents (for console agent) |
| `WebFetchTool` | HTTP GET, return body (useful for RAG over live content) |

### New Dependencies

| Package | Purpose |
|---------|---------|
| `System.Text.Json` | Already available — JSON Schema generation for tool params |
| `NJsonSchema` (optional) | Richer JSON Schema generation from C# types |

---

## Phase 2b — MCP Integration

**Goal:** Make the agent extensible via MCP (Model Context Protocol) — both as a **host** that consumes external MCP servers and as a **server** that exposes Agency capabilities to other agents.

### Why MCP Matters for This Toolkit

MCP is becoming the standard protocol for agent ↔ tool communication. Supporting it means:
- Users can plug *any* MCP server into an Agency agent without writing C# tool wrappers
- Other AI tools (Claude Code, Cursor, VS Code Copilot) can use Agency's vector store and RAG as an MCP server
- It's a discovery mechanism — developers find Agency by adding it as an MCP server before ever importing the NuGet package

### Part A: MCP Host (Agent consumes external tools)

#### New Project: `Agency.Mcp.Client`

```
Agency.Tools (IToolRegistry, ToolDefinition)
    ↓
Agency.Mcp.Client (connects to MCP servers, registers their tools)
```

| Component | Purpose |
|-----------|---------|
| `McpConnection` | Manages stdio/SSE/Streamable HTTP transport to an MCP server |
| `McpToolProvider` | Discovers tools from an MCP server → registers as `ToolDefinition` entries |
| `McpResourceProvider` | Exposes MCP resources as context the agent can read |
| `McpServerConfig` | Configuration: server command, args, env vars, transport type |

#### How It Plugs Into the Agent

The agent's `IToolRegistry` accepts tools from two sources:
1. Native C# tools (decorated with `[Tool]`)
2. MCP-discovered tools (via `McpToolProvider`)

Both produce `ToolDefinition` — the agent loop doesn't know or care where a tool came from. When the LLM calls an MCP-sourced tool, the executor routes the call back through the `McpConnection` to the external server.

```
AgentBuilder
    .WithTools(nativeTools)              // C# [Tool] methods
    .WithMcpServer("memory", config)     // external MCP server
    .WithMcpServer("filesystem", config) // another MCP server
    .Build()
```

### Part B: MCP Server (Agency exposes its capabilities)

#### New Project: `Agency.Mcp.Server`

```
Agency.VectorStore.Common + Agency.Rag + Agency.Sql
    ↓
Agency.Mcp.Server (exposes Agency features as MCP tools/resources)
```

| Component | Purpose |
|-----------|---------|
| `AgencyMcpServer` | Hosts the MCP server (stdio + Streamable HTTP transports) |
| `KVMemoryTool` | MCP tool wrapping `IKVStore` — store/search/delete memories |
| `VectorSearchTool` | MCP tool wrapping vector similarity search |
| `SqlQueryTool` | MCP tool wrapping `PostgreSqlRunner` (with vectorize support) |
| `RagQueryTool` | MCP tool wrapping the hybrid retriever (Phase 4) |
| `KVMemoryResource` | MCP resource exposing stored memories as readable context |

#### KVMemory MCP Server — First Deliverable

This is the most immediately useful MCP server to ship. It wraps your existing `IKVStore` (PostgreSQL + pgvector) as a persistent semantic memory that any MCP-compatible agent can use:

| MCP Tool | Maps To | Description |
|----------|---------|-------------|
| `memory_store` | `IKVStore.UpsertAsync` | Store a memory with key, value, metadata |
| `memory_search` | `IKVStore.SearchAsync` | Semantic search over stored memories |
| `memory_delete` | (new method on `IKVStore`) | Delete a memory by key |
| `memory_list` | (new method on `IKVStore`) | List recent memories with optional filter |

| MCP Resource | Description |
|--------------|-------------|
| `memory://recent` | Last N stored memories as context |
| `memory://{key}` | A specific memory by key |

#### Example: Using Agency as an MCP Server in Claude Code

```json
// Claude Code settings
{
  "mcpServers": {
    "agency-memory": {
      "command": "dotnet",
      "args": ["run", "--project", "Agency.Mcp.Server"],
      "env": { "CONNECTION_STRING": "Host=localhost;..." }
    }
  }
}
```

Now Claude Code can call `memory_store` and `memory_search` — backed by PostgreSQL + pgvector.

### New Dependencies

| Package | Purpose |
|---------|---------|
| `ModelContextProtocol` | Official .NET MCP SDK (Microsoft + Anthropic) |
| `Microsoft.Extensions.Hosting` | For hosting the MCP server process |

### Transport Support

| Transport | Use Case | Priority |
|-----------|----------|----------|
| **stdio** | Local agents, Claude Code, Cursor | Ship first |
| **Streamable HTTP** | Remote/networked agents, web UIs | Ship second |

---

## Phase 3 — Agent Loop

**Goal:** Autonomous agent that loops: prompt → LLM → tool calls → results → LLM → … → final answer.

### New Project: `Agency.Agent`

```
Agency.Conversation + Agency.Tools
    ↓
Agency.Agent (orchestration loop, policies)
```

### Components

| Component | Purpose |
|-----------|---------|
| `IAgent` | `RunAsync(string userMessage) → AgentResponse` |
| `AgentBuilder` | Fluent API: `new AgentBuilder().WithModel("claude-sonnet-4-20250514").WithTools(…).WithSystemPrompt(…).Build()` |
| `AgentLoop` | Core loop: send → check stop reason → if tool_use, execute tools, append results, loop → if end_turn, return |
| `AgentOptions` | MaxIterations, MaxTokens, Temperature, AllowedTools |
| `IAgentMiddleware` | Pre/post processing hooks (logging, guardrails, token budgets) |
| `AgentResponse` | Final message + full conversation trace + token usage summary |

### Loop Pseudocode

```
while (iterations < max)
    response = llmClient.SendAsync(conversation, tools)
    conversation.Add(response)
    
    if response.StopReason is EndTurn or Stop → return response
    if response.StopReason is ToolUse →
        for each toolCall in response.ToolCalls
            result = toolExecutor.Execute(toolCall)
            conversation.Add(ToolMessage(toolCall.Id, result))
        continue
    
    if response.StopReason is MaxTokens → continue (allow LLM to finish)
    break
```

### Middleware Examples

- **TokenBudgetMiddleware** — abort if cumulative tokens exceed threshold
- **ToolApprovalMiddleware** — require human confirmation for dangerous tools
- **LoggingMiddleware** — structured log every iteration

### New Dependencies

None beyond what Phase 1–2 introduce.

---

## Phase 4 — Graph + Vector RAG Memory (Neo4j)

**Goal:** A hybrid RAG memory system that combines graph relationships with vector similarity, backed by Neo4j — which natively supports both Cypher traversals and vector indexes in a single database.

### Why Neo4j Over PostgreSQL for Graph

| | PostgreSQL (adjacency tables) | Neo4j |
|--|-------------------------------|-------|
| Graph traversal | Recursive CTEs — verbose, slow at depth > 3 | Cypher — expressive, optimized for traversal |
| Vector search | pgvector (separate from graph) | Native vector indexes (since 5.11) |
| Hybrid query | Two separate queries, merge in app code | Single Cypher query: traverse + vector filter |
| Schema flexibility | Rigid tables | Property graph — add properties/labels freely |
| Visualization | None built-in | Neo4j Browser — free graph exploration |

**The key insight:** Neo4j can do *both* graph and vector in a single query. No cross-database merging. A Cypher query can traverse relationships AND filter by vector similarity in one pass.

### New Projects

| Project | Purpose |
|---------|---------|
| `Agency.GraphStore.Common` | `IGraphStore` abstraction (backend-agnostic) |
| `Agency.GraphStore.Neo4j` | Neo4j implementation — Cypher traversals + native vector indexes |
| `Agency.Rag` | Orchestrates hybrid retrieval, re-ranking, context assembly |
| `Agency.Rag.Memory` | High-level memory component — entities, relationships, episodes |

### Architecture

```
Agency.Embeddings.Common
Agency.VectorStore.Common
Agency.GraphStore.Common
    ↓
Agency.GraphStore.Neo4j (Cypher + vector indexes)
    ↓
Agency.Rag (hybrid retriever, re-ranking, context assembly)
    ↓
Agency.Rag.Memory (structured memory: entities + relationships + episodes)
```

### Graph Store Abstraction: `Agency.GraphStore.Common`

| Component | Purpose |
|-----------|---------|
| `IGraphStore` | `AddNodeAsync`, `AddEdgeAsync`, `GetNeighborsAsync`, `TraverseAsync(startNode, depth, filter)`, `QueryAsync(cypherOrQuery)` |
| `GraphNode` | Id, Labels[], Properties (dict), Embedding (optional) |
| `GraphEdge` | Id, Source, Target, Type (relationship name), Properties, Weight |
| `GraphQuery` | Structured query: start node(s), relationship filter, depth, direction |
| `GraphTraversalResult` | Subgraph of nodes + edges matching the traversal |

### Neo4j Implementation: `Agency.GraphStore.Neo4j`

| Component | Purpose |
|-----------|---------|
| `Neo4jGraphStore` | Implements `IGraphStore` via Neo4j .NET Driver |
| `Neo4jVectorIndex` | Create/manage Neo4j vector indexes on node properties |
| `CypherBuilder` | Fluent Cypher query builder (avoids raw string Cypher) |
| `Neo4jOptions` | Configuration: URI, auth, database name, vector index settings |

#### Neo4j Vector Index Setup

Neo4j vector indexes live on node properties — you create a vector index on a `:Chunk {embedding}` property, then query with `db.index.vector.queryNodes()`:

```cypher
// Create vector index
CREATE VECTOR INDEX chunk_embeddings FOR (c:Chunk)
ON (c.embedding) OPTIONS {indexConfig: {
  `vector.dimensions`: 1536,
  `vector.similarity_function`: 'cosine'
}}

// Hybrid query: vector search + graph traversal in one pass
CALL db.index.vector.queryNodes('chunk_embeddings', 10, $queryVector)
YIELD node AS chunk, score
MATCH (chunk)-[:PART_OF]->(doc:Document)-[:RELATES_TO]->(related:Document)
RETURN chunk, score, doc, related
ORDER BY score DESC
```

### RAG Hybrid Retriever: `Agency.Rag`

| Component | Purpose |
|-----------|---------|
| `HybridRetriever` | Queries vector store + graph store, merges results |
| `IRetrievalStrategy` | Pluggable strategy: vector-first, graph-first, parallel merge |
| `IReRanker` | Re-score merged results (reciprocal rank fusion, cross-encoder) |
| `ContextAssembler` | Ranked results → formatted context for LLM window |

#### Retrieval Strategies

| Strategy | How It Works | Best For |
|----------|-------------|----------|
| `VectorFirstStrategy` | Vector top-K → expand each via graph (1-2 hops) → re-rank | General RAG queries |
| `GraphFirstStrategy` | Entity extraction → graph traversal → vector re-rank within subgraph | "Tell me about X's relationship with Y" |
| `ParallelMergeStrategy` | Vector + graph in parallel → reciprocal rank fusion | Latency-sensitive, broad queries |

### RAG Memory Component: `Agency.Rag.Memory`

This is the high-level memory abstraction that agents and UIs consume. It models memory as a knowledge graph with three node types:

| Node Type | Label | Description |
|-----------|-------|-------------|
| **Entity** | `:Entity` | Named things — people, projects, concepts, files |
| **Fact** | `:Fact` | A piece of knowledge, stored with embedding for semantic search |
| **Episode** | `:Episode` | A timestamped interaction or event (conversation turn, observation) |

| Relationship | Pattern | Description |
|-------------|---------|-------------|
| `MENTIONED_IN` | `(Entity)-[:MENTIONED_IN]->(Episode)` | Entity appeared in this episode |
| `RELATES_TO` | `(Entity)-[:RELATES_TO {type}]->(Entity)` | Typed relationship between entities |
| `DERIVED_FROM` | `(Fact)-[:DERIVED_FROM]->(Episode)` | Fact was learned from this episode |
| `ABOUT` | `(Fact)-[:ABOUT]->(Entity)` | Fact describes this entity |
| `SUPERSEDES` | `(Fact)-[:SUPERSEDES]->(Fact)` | Newer fact replaces older one |

| Component | Purpose |
|-----------|---------|
| `IMemoryStore` | High-level API: `RememberAsync`, `RecallAsync`, `ForgetAsync`, `RelateAsync` |
| `MemoryStore` | Orchestrates entity extraction, embedding, graph storage, deduplication |
| `IEntityExtractor` | Extract entities from text (LLM-based or NER) |
| `MemoryQuery` | "What do I know about X?", "What happened between X and Y?", "Recent context" |
| `MemoryRecall` | Returned context: relevant facts + entity subgraph + episode timeline |

#### Memory API

```csharp
// Store a memory (agent or user interaction)
await memory.RememberAsync(new Episode
{
    Content = "User mentioned they prefer Claude over GPT for coding tasks",
    Timestamp = DateTimeOffset.UtcNow,
    Source = "conversation"
});
// Internally: extract entities ["User", "Claude", "GPT"] → create/update nodes
//             embed content → store as :Fact with vector index
//             create relationships: (User)-[:PREFERS]->(Claude), etc.

// Recall memories
MemoryRecall recall = await memory.RecallAsync("What does the user prefer for coding?");
// Returns: relevant facts + entity graph + source episodes

// Explicit relationship
await memory.RelateAsync("Agency", "Neo4j", "USES", new { since = "Phase 4" });

// Forget (GDPR, outdated info)
await memory.ForgetAsync(entityName: "old-api-key");
```

#### How the Agent Uses Memory

```
User: "Remember that our deployment target is AKS"

Agent:
  1. memory.RememberAsync(episode with content)
  2. Entity extraction → ["deployment target", "AKS"]
  3. Graph: (deployment_target)-[:IS]->(AKS)
  4. Vector: embed + index the fact

--- later, different conversation ---

User: "Where should we deploy this service?"

Agent:
  1. memory.RecallAsync("deploy service")
  2. Vector similarity finds the AKS fact
  3. Graph traversal finds related entities (AKS → Azure → deployment_target)
  4. Context assembled: "Previously noted: deployment target is AKS"
```

### Graph-Enhanced RAG Flow (Full)

```
User query
    → Generate embedding
    → Vector search (Neo4j vector index) → top-K facts/chunks
    → For each result, Cypher traversal for related entities (1-2 hops)
    → Merge results from both paths
    → Re-rank (reciprocal rank fusion)
    → Assemble context → inject into LLM system/user prompt
```

### MCP Integration (extends Phase 2b)

The memory component naturally extends the MCP server with richer tools:

| MCP Tool | Maps To | Description |
|----------|---------|-------------|
| `memory_remember` | `IMemoryStore.RememberAsync` | Store an episode with auto entity extraction |
| `memory_recall` | `IMemoryStore.RecallAsync` | Semantic + graph recall |
| `memory_relate` | `IMemoryStore.RelateAsync` | Create explicit entity relationship |
| `memory_forget` | `IMemoryStore.ForgetAsync` | Remove entity/fact from memory |
| `memory_graph` | `IGraphStore.TraverseAsync` | Raw graph traversal for exploration |

### New Dependencies

| Package | Purpose |
|---------|---------|
| `Neo4j.Driver` | Official Neo4j .NET async driver |

### Infrastructure

Add to `docker-compose.yml`:

```yaml
neo4j:
  image: neo4j:5-community
  ports:
    - "7474:7474"   # Browser UI
    - "7687:7687"   # Bolt protocol
  environment:
    NEO4J_AUTH: neo4j/dev_password
    NEO4J_PLUGINS: '[]'  # vector indexes are built-in since 5.11
  volumes:
    - neo4j_data:/data
```

---

## Phase 5 — Console Experience

**Goal:** Interactive CLI agent similar to Claude Code — streaming output, tool-use display, conversation persistence.

### New: Enhance `Agency.Console`

```
Agency.Agent + Agency.Tools
    ↓
Agency.Console (interactive REPL, Spectre.Console rendering)
```

### Features

| Feature | Description |
|---------|-------------|
| Streaming output | Token-by-token rendering via `StreamAsync` |
| Tool-use display | Show tool name + args + result inline (like Claude Code) |
| Markdown rendering | Render LLM markdown in terminal |
| Conversation history | Persist conversations to local JSON files |
| `/slash` commands | `/clear`, `/model`, `/tools`, `/save`, `/load` |
| Multi-line input | Detect and handle multi-line pastes |
| Syntax highlighting | Highlight code blocks in responses |

### New Dependencies

| Package | Purpose |
|---------|---------|
| **Spectre.Console** | Rich terminal rendering — panels, tables, markdown, progress |
| **Spectre.Console.Cli** | Command-line argument parsing |

---

## Phase 6 — MAUI Blazor Hybrid Chat

**Goal:** Cross-platform desktop/mobile chat UI with streaming, conversation management, and RAG visualization.

### New Projects

| Project | Purpose |
|---------|---------|
| `Agency.Chat.Components` | Shared Razor components (chat bubble, tool display, settings) |
| `Agency.Chat.Maui` | MAUI Blazor Hybrid host (Windows, macOS, Android, iOS) |

```
Agency.Agent
    ↓
Agency.Chat.Components (Razor Class Library — shared UI)
    ↓
Agency.Chat.Maui (MAUI Blazor Hybrid shell)
```

### UI Components

| Component | Purpose |
|-----------|---------|
| `ChatView` | Scrollable message list, auto-scroll, streaming text |
| `MessageBubble` | Renders user/assistant/tool messages with appropriate styling |
| `ToolCallCard` | Expandable card showing tool name, params, result |
| `ModelSelector` | Switch between Claude / OpenAI / local models |
| `ConversationList` | Sidebar with saved conversations |
| `SettingsPanel` | API keys, endpoints, temperature, system prompt |
| `RagContextViewer` | Collapsible panel showing retrieved chunks + relevance scores |

### New Dependencies

| Package | Purpose |
|---------|---------|
| `Microsoft.Maui.Controls` | .NET MAUI framework |
| `Microsoft.AspNetCore.Components.WebView.Maui` | Blazor Hybrid WebView |
| `Markdig` | Markdown → HTML rendering in chat |
| `Blazored.LocalStorage` | Persist settings and conversations client-side |

---

## Dependency Summary

### NuGet Packages to Add

| Phase | Package | Version (as of 2026) | Purpose |
|-------|---------|---------------------|---------|
| 2 | `NJsonSchema` | 11.x | JSON Schema from C# types (optional) |
| 2b | `ModelContextProtocol` | 0.1+ | Official .NET MCP SDK |
| 2b | `Microsoft.Extensions.Hosting` | 10.x | MCP server process hosting |
| 5 | `Neo4j.Driver` | 5.x | Official Neo4j .NET async driver |
| 7 | `Microsoft.CodeAnalysis.CSharp.Workspaces` | 4.x | Roslyn — full semantic analysis of C# code |
| 7 | `Microsoft.Build.Locator` | 1.x | Locates .NET SDK for MSBuildWorkspace |
| 5 | `Spectre.Console` | 0.49+ | Rich terminal UI |
| 5 | `Spectre.Console.Cli` | 0.49+ | CLI argument parsing |
| 6 | `Microsoft.Maui.Controls` | 10.x | MAUI framework |
| 6 | `Microsoft.AspNetCore.Components.WebView.Maui` | 10.x | Blazor in MAUI |
| 6 | `Markdig` | 0.38+ | Markdown rendering |
| 6 | `Blazored.LocalStorage` | 4.x | Client-side storage |

### Infrastructure Additions

| What | When | How |
|------|------|-----|
| Neo4j 5 Community | Phase 5 | Add to docker-compose.yml (ports 7474/7687) |

---

## Project Dependency Graph (Final State)

```
Agency.Common
Agency.Embeddings.Common ─── Agency.Embeddings.OpenAI
Agency.VectorStore.Common ── Agency.VectorStore.Sql.Postgre
Agency.GraphStore.Common ─── Agency.GraphStore.Neo4j
Agency.Llm.Common ────────── Agency.Llm.Claude
                           ── Agency.Llm.OpenAI

Agency.Conversation ← (Agency.Llm.Common)
Agency.Tools ← (Agency.Llm.Common)
Agency.Rag ← (Agency.VectorStore.Common, Agency.GraphStore.Common,
               Agency.Embeddings.Common, Agency.RagFormatter)
Agency.Rag.Memory ← (Agency.Rag, Agency.GraphStore.Neo4j)

Agency.Mcp.Client ← (Agency.Tools, ModelContextProtocol)
Agency.Mcp.Server ← (Agency.VectorStore.Common, Agency.Rag, Agency.Rag.Memory, ModelContextProtocol)

Agency.Agent ← (Agency.Conversation, Agency.Tools, Agency.Mcp.Client)

Agency.Console ← (Agency.Agent, Agency.Rag, Spectre.Console)
Agency.Chat.Components ← (Agency.Agent, Agency.Rag)
Agency.Chat.Maui ← (Agency.Chat.Components)

Agency.Examples.CodeMap ← (Agency.GraphStore.Neo4j, Agency.Embeddings.Common,
                           Agency.Agent, Microsoft.CodeAnalysis.CSharp.Workspaces)
```

---

## Implementation Order & Milestones

| # | Milestone | Projects | Est. Complexity |
|---|-----------|----------|----------------|
| 1 | **Multi-turn chat works** | `Agency.Conversation` | Low |
| 2 | **LLM can call tools** | `Agency.Tools`, extend `ILlmClient` | Medium |
| 2b | **MCP host + server** | `Agency.Mcp.Client`, `Agency.Mcp.Server` | Medium |
| 3 | **Autonomous agent loop** | `Agency.Agent` | Medium |
| 4 | **CLI agent you can talk to** | `Agency.Console` | Medium |
| 5 | **Graph + vector RAG memory (Neo4j)** | `Agency.GraphStore.*`, `Agency.Rag`, `Agency.Rag.Memory` | High |
| 6 | **Desktop/mobile chat app** | `Agency.Chat.*` | High |
| 7 | **CodeMap example app** | `Agency.Examples.CodeMap` | High |

### Suggested Approach Per Milestone

- **Milestone 1–3:** These are the core framework. Build them test-first. They're what makes Agency a *toolkit* (other devs will consume these as libraries).
- **Milestone 2b:** The MCP server is a **growth hack** — ship it early. Developers add `agency-memory` as an MCP server in Claude Code or Cursor, discover it works well, then explore the full toolkit. The MCP client makes the agent loop infinitely extensible without recompilation.
- **Milestone 4:** The console app is both a dogfood vehicle and a demo. Build it alongside Milestone 3 so you have a tangible way to test the agent loop.
- **Milestone 5:** Graph+vector RAG memory on Neo4j is the differentiator. Most C# AI toolkits stop at flat vector search. A knowledge graph with entity relationships, temporal episodes, and fact supersession — queryable through a clean `IMemoryStore` API — is what makes Agency worth starring. The `SUPERSEDES` relationship pattern alone (newer facts invalidate older ones) solves a real problem nobody else addresses in C#.
- **Milestone 6:** MAUI Blazor is the showcase. Build it last so the underlying APIs are stable.
- **Milestone 7:** CodeMap is the "wow" demo. It dogfoods every layer (Roslyn → embeddings → Neo4j graph → agent tools → console UI) and solves a real problem — onboarding to unfamiliar codebases. Ship it alongside or right after the console experience so people can try it immediately. Consider building it in parallel with Phase 5 since it exercises the graph store heavily and will surface API gaps early.

---

## Key Design Decisions to Make Early

| Decision | Options | Recommendation |
|----------|---------|----------------|
| **Tool schema format** | Manual JSON, `[Tool]` attribute + reflection, source generators | Start with `[Tool]` attribute; add source generator later for AOT |
| **Conversation persistence** | JSON files, SQLite, PostgreSQL | JSON files for console, PostgreSQL for shared/production |
| **Graph storage** | PostgreSQL adjacency tables vs. Neo4j | Neo4j — native Cypher + built-in vector indexes = single-query hybrid retrieval |
| **Entity extraction** | LLM-based vs. NER library vs. regex | LLM-based first (most flexible); add spaCy/NER for cost reduction later |
| **Agent streaming** | Buffer full response vs. stream chunks during tool loops | Stream chunks — users expect real-time feedback |
| **Shared UI components** | Razor Class Library vs. duplicated UI | Razor Class Library (`Agency.Chat.Components`) — reusable across Blazor WASM, Server, and MAUI |
| **MCP transport** | stdio only vs. stdio + Streamable HTTP | stdio first (local tools); add Streamable HTTP for remote/web scenarios |
| **MCP server scope** | KVMemory only vs. full Agency surface | Start with KVMemory (highest standalone value); add RAG/SQL tools as those layers stabilize |

---

## Example App — CodeMap: Agent-Accessible Code Intelligence

**Goal:** Scan a C# repository with Roslyn, index all public/internal types and members into Neo4j as a code graph, and expose it as agent tools so an LLM can navigate, explain, and reason about code structure.

### Why This Example Matters

1. **Dogfoods the entire stack** — Roslyn → embeddings → Neo4j graph store → agent tools → console/chat UI
2. **Immediately useful** — developers point it at their repo and get an AI that *understands* the codebase structure, not just text-searches it
3. **Showcase for the toolkit** — a single compelling demo is worth more than 10 library docs pages
4. **Unique in .NET** — no existing .NET tool combines Roslyn semantic analysis + graph database + agent-accessible querying

### New Project: `Agency.Examples.CodeMap`

```
Microsoft.CodeAnalysis.CSharp (Roslyn)
    ↓
Agency.Examples.CodeMap (indexer + agent tools)
    ↓
Agency.GraphStore.Neo4j + Agency.Embeddings.Common + Agency.Agent
```

### Phase A — Roslyn Analysis & Graph Schema

#### What Gets Indexed

| Symbol Type | Accessibility | Indexed Properties |
|-------------|---------------|-------------------|
| Namespace | all | Name, FilePath |
| Class / Struct / Record | public, internal | Name, Modifiers (abstract, sealed, static, partial), BaseType, Interfaces, FilePath, LineSpan, DocComment |
| Interface | public, internal | Name, Members, FilePath, LineSpan, DocComment |
| Enum | public, internal | Name, Members, FilePath, LineSpan |
| Method | public, internal | Name, ReturnType, Parameters[], Modifiers (async, static, virtual, override), FilePath, LineSpan, DocComment, CyclomaticComplexity |
| Property | public, internal | Name, Type, HasGetter, HasSetter, Modifiers |
| Field | public, internal | Name, Type, Modifiers (readonly, const, static) |
| Constructor | public, internal | Parameters[], FilePath, LineSpan |
| Event | public, internal | Name, HandlerType |
| Delegate | public, internal | Name, ReturnType, Parameters[] |

#### Neo4j Graph Schema

**Node Labels**

| Label | Key Properties | Embedding |
|-------|---------------|-----------|
| `:Solution` | Name | — |
| `:Project` | Name, TargetFramework, OutputType | — |
| `:Namespace` | FullName | — |
| `:Class` | Name, FullName, Modifiers, IsAbstract, IsSealed, IsStatic, IsPartial | DocComment summary |
| `:Struct` | Name, FullName, Modifiers | DocComment summary |
| `:Record` | Name, FullName, Modifiers | DocComment summary |
| `:Interface` | Name, FullName | DocComment summary |
| `:Enum` | Name, FullName | — |
| `:Method` | Name, FullName, Signature, ReturnType, IsAsync, IsStatic, IsVirtual, IsOverride, CyclomaticComplexity | DocComment + signature summary |
| `:Property` | Name, FullName, Type, HasGetter, HasSetter | — |
| `:Constructor` | FullName, Signature | — |
| `:Parameter` | Name, Type, IsOptional, DefaultValue | — |
| `:Field` | Name, FullName, Type, IsReadonly, IsConst | — |
| `:Event` | Name, FullName, HandlerType | — |
| `:EnumMember` | Name, Value | — |

**Relationships**

| Relationship | Pattern | Description |
|-------------|---------|-------------|
| `CONTAINS_PROJECT` | `(Solution)-[:CONTAINS_PROJECT]->(Project)` | Solution has projects |
| `CONTAINS_NAMESPACE` | `(Project)-[:CONTAINS_NAMESPACE]->(Namespace)` | Project defines namespaces |
| `DECLARED_IN` | `(Class)-[:DECLARED_IN]->(Namespace)` | Type belongs to namespace |
| `DEFINED_IN` | `(Method)-[:DEFINED_IN {file, startLine, endLine}]->(Class)` | Member location in source |
| `INHERITS` | `(Class)-[:INHERITS]->(Class)` | Class inheritance |
| `IMPLEMENTS` | `(Class)-[:IMPLEMENTS]->(Interface)` | Interface implementation |
| `CALLS` | `(Method)-[:CALLS {count}]->(Method)` | Method invocation (with call count) |
| `CREATES` | `(Method)-[:CREATES]->(Class)` | `new T()` instantiation |
| `RETURNS_TYPE` | `(Method)-[:RETURNS_TYPE]->(Class\|Interface\|Struct)` | Return type reference |
| `HAS_PARAMETER` | `(Method)-[:HAS_PARAMETER {ordinal}]->(Parameter)` | Method parameters (ordered) |
| `PARAMETER_TYPE` | `(Parameter)-[:PARAMETER_TYPE]->(Class\|Interface\|Struct)` | Parameter type reference |
| `HAS_PROPERTY` | `(Class)-[:HAS_PROPERTY]->(Property)` | Type has property |
| `PROPERTY_TYPE` | `(Property)-[:PROPERTY_TYPE]->(Class\|Interface\|Struct)` | Property type reference |
| `HAS_FIELD` | `(Class)-[:HAS_FIELD]->(Field)` | Type has field |
| `THROWS` | `(Method)-[:THROWS]->(Class)` | Exception types thrown |
| `USES_TYPE` | `(Method)-[:USES_TYPE]->(Class\|Interface)` | Any type reference in method body |
| `OVERRIDES` | `(Method)-[:OVERRIDES]->(Method)` | Virtual/override chain |
| `DEPENDS_ON` | `(Project)-[:DEPENDS_ON]->(Project)` | Project reference |
| `REFERENCES_PACKAGE` | `(Project)-[:REFERENCES_PACKAGE {version}]->(Package)` | NuGet dependency |

#### How Roslyn Provides This

```csharp
// Open the solution/project
var workspace = MSBuildWorkspace.Create();
var solution = await workspace.OpenSolutionAsync("Agency.slnx");

foreach (var project in solution.Projects)
{
    var compilation = await project.GetCompilationAsync();

    foreach (var syntaxTree in compilation.SyntaxTrees)
    {
        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var root = await syntaxTree.GetRootAsync();

        // Walk all type declarations
        foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl);
            // → Create :Class/:Interface/:Struct node
            // → Create :INHERITS, :IMPLEMENTS edges from BaseList
            // → Create :DECLARED_IN edge to namespace

            foreach (var methodDecl in typeDecl.Members.OfType<MethodDeclarationSyntax>())
            {
                var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl);
                // → Create :Method node
                // → Create :DEFINED_IN edge with file + line span

                // Analyze method body for call relationships
                foreach (var invocation in methodDecl.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>())
                {
                    var calledSymbol = semanticModel.GetSymbolInfo(invocation).Symbol;
                    // → Create :CALLS edge (source method → target method)
                }

                // Analyze object creations
                foreach (var creation in methodDecl.DescendantNodes()
                    .OfType<ObjectCreationExpressionSyntax>())
                {
                    var createdType = semanticModel.GetSymbolInfo(creation).Symbol;
                    // → Create :CREATES edge
                }
            }
        }
    }
}
```

### Phase B — Embeddings for Semantic Code Search

Not everything can be found by graph traversal alone. Embeddings enable:
- "Find methods that handle authentication" (semantic, not keyword-based)
- "What's similar to this error handling pattern?" (similarity search)

| What Gets Embedded | Embedding Source Text |
|-------------------|---------------------|
| Class/Interface | `"{Name}: {DocComment}"` or `"{Name}: {public member signatures summary}"` |
| Method | `"{DeclaringType.Name}.{Name}({params}): {DocComment}"` or `"{signature}: {first N lines of body}"` |

Embeddings are stored as node properties on the Neo4j nodes and indexed with a Neo4j vector index. This enables hybrid queries:

```cypher
// "Find methods related to 'embedding generation' that are called by the agent loop"
CALL db.index.vector.queryNodes('method_embeddings', 20, $queryVector)
YIELD node AS method, score
WHERE score > 0.7
MATCH (caller:Method)-[:CALLS]->(method)
MATCH (caller)-[:DEFINED_IN]->(:Class {name: 'AgentLoop'})
RETURN method.fullName, method.signature, score
```

### Phase C — Agent Tools

These tools let the agent query the code graph conversationally:

| Tool | Description | Example Query |
|------|-------------|--------------|
| `codemap_find_type` | Find a class/interface by name or pattern | "Find the IKVStore interface" |
| `codemap_get_members` | List all members of a type | "What methods does ClaudeClient have?" |
| `codemap_call_graph` | Who calls this method / what does this method call | "What calls SendAsync?" |
| `codemap_inheritance` | Inheritance chain and interface implementations | "What implements ILlmClient?" |
| `codemap_dependencies` | Project dependency graph | "What depends on Agency.Common?" |
| `codemap_search` | Semantic search across all indexed symbols | "Find code related to vector similarity" |
| `codemap_explain` | Get a type/method with its full context (callers, callees, related types) | "Explain SQLQueryEmbedder" |
| `codemap_impact` | Transitive analysis: if I change X, what might break? | "What's the blast radius of changing IEmbeddingGenerator?" |
| `codemap_path` | Find relationship path between two symbols | "How does Agency.Console reach PostgreSqlRunner?" |

#### Example: `codemap_impact` Implementation

```cypher
// Find all types/methods that transitively depend on a given interface
MATCH (target:Interface {fullName: $targetFullName})
MATCH (dependent)-[:IMPLEMENTS|INHERITS|USES_TYPE|CALLS|RETURNS_TYPE|PARAMETER_TYPE*1..4]->(target)
RETURN DISTINCT dependent.fullName, labels(dependent)[0] AS kind,
       length(shortestPath((dependent)-[*]->(target))) AS distance
ORDER BY distance
```

#### Example: `codemap_explain` Output

When the agent calls `codemap_explain("SQLQueryEmbedder")`, it gets back:

```
## SQLQueryEmbedder (Class)
Namespace: Agency.Sql.Postgre
File: Agency.Sql.Postgre/SQLQueryEmbedder.cs (lines 12-85)
Modifiers: public, sealed

### Inherits: -
### Implements: -

### Constructor
- SQLQueryEmbedder(IEmbeddingGenerator embeddingGenerator)

### Methods
- ReplaceVectorPlaceholdersAsync(string sql) → string
  Calls: IEmbeddingGenerator.GenerateEmbeddingAsync, Regex.Matches
  Called by: PostgreSqlRunner.ExecuteQueryAsync (via manual integration)

### Used By (reverse dependencies)
- Agency.Sql.Postgre.Test → SQLQueryEmbedderTests
- Agency.Console (planned)

### Related Types
- IEmbeddingGenerator (injected dependency)
- PostgreSqlRunner (co-located, uses output)
```

### Phase D — Incremental Updates

Full re-indexing on every change is too slow. Use file watching + git diff:

| Strategy | When | How |
|----------|------|-----|
| **Full index** | First run, or `--force` flag | Scan entire solution |
| **Git diff index** | Subsequent runs | `git diff --name-only HEAD~1` → re-index only changed `.cs` files |
| **File watcher** | Running as background service | `FileSystemWatcher` → re-index on save |

For incremental updates, the indexer:
1. Deletes all nodes/edges originating from the changed file (`DEFINED_IN` edge has `file` property)
2. Re-parses only that file
3. Re-creates nodes/edges
4. Re-generates embeddings only for changed symbols

### New Dependencies

| Package | Purpose |
|---------|---------|
| `Microsoft.CodeAnalysis.CSharp.Workspaces` | Roslyn — parse C# code with full semantic model |
| `Microsoft.Build.Locator` | Required for MSBuildWorkspace to find .NET SDK |

### CLI Usage

```bash
# Full index of a solution
dotnet run --project Agency.Examples.CodeMap -- index ./src/Agency.slnx

# Incremental update (changed files since last commit)
dotnet run --project Agency.Examples.CodeMap -- update ./src/Agency.slnx

# Launch interactive agent with codemap tools
dotnet run --project Agency.Examples.CodeMap -- agent ./src/Agency.slnx

# Query from command line (non-interactive)
dotnet run --project Agency.Examples.CodeMap -- query "What implements ILlmClient?"
```

### Example Conversations with the Agent

**Understanding unfamiliar code:**
```
User: I'm new to this repo. How does a user query get from input to LLM response?

Agent: [calls codemap_path("Agency.Console", "ILlmClient")]
       [calls codemap_call_graph("ILlmClient.SendAsync", direction: "callers")]

Let me trace the path for you:
1. Agency.Console → AgentLoop.RunAsync()
2. AgentLoop → ILlmClient.SendAsync() (dispatched to ClaudeClient or OpenAIClient)
3. ClaudeClient → Anthropic SDK → Claude API

The agent loop in AgentLoop.cs:34 checks StopReason — if it's ToolUse,
it executes tools and loops back. If EndTurn, it returns the response.
```

**Impact analysis before refactoring:**
```
User: I want to rename IEmbeddingGenerator to IEmbedder. What breaks?

Agent: [calls codemap_impact("IEmbeddingGenerator")]

IEmbeddingGenerator is referenced in 8 locations across 4 projects:
- Agency.Embeddings.OpenAI/EmbeddingGenerator.cs — implements it (MUST update)
- Agency.Sql.Postgre/SQLQueryEmbedder.cs — constructor injection (MUST update)
- Agency.VectorStore.Sql.Postgre/PostgreKVStore.cs — constructor injection
- Agency.Rag/HybridRetriever.cs — constructor injection
- 4 test files — Moq setups

No transitive breaks beyond direct references. Safe to rename with a
solution-wide find-replace. Want me to do it?
```
