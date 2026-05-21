# Agency.Agentic

#agent #loop #tools #events #orchestration #mcp #observability

## What It Is

Agency.Agentic is the autonomous agent loop library that drives a think → act → observe cycle until a `StopCondition` fires. It accepts an `IChatClient` (via `Microsoft.Extensions.AI`), a `Context` object aggregating the user query, conversation history, tools, memory, and grounding data, and yields a stream of typed `AgentEvent` records so callers can react to each stage without polling. MCP (Model Context Protocol) server tools are supported via `McpClientPool`, which connects to one or more MCP servers and exposes their tools as `ITool` instances compatible with `ToolRegistry`.

**Namespace:** `Agency.Agentic`

## API Surface

### `Agent`

```csharp
// File: src/Agentic/Agency.Agentic/Agent.cs
using Agency.Agentic.Contexts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agency.Agentic;

public sealed class Agent
{
    public const string ActivitySourceName = "Agency.Agentic.Agent";
    public const string MeterName          = "Agency.Agentic.Agent";

    public Agent(
        IChatClient llm,
        string model,
        string? clientType = null,
        StopCondition? stopWhen = null,  // default: Any(NoToolCalls, StepCountIs(20))
        bool stream = true,
        ILogger<Agent>? logger = null);

    public string Model      { get; }
    public string ClientType { get; }

    /// <summary>Creates a Context pre-populated with temporal context and the initial prompt.</summary>
    public static Context CreateContext(string initialPrompt, ToolContext? tools = null);

    /// <summary>
    /// Executes one user turn. On the first call, delegates to the internal loop; on subsequent calls,
    /// appends the message then runs the loop. Applies TurnTimeoutSeconds when configured.
    /// </summary>
    public IAsyncEnumerable<AgentEvent> ChatAsync(
        string userMessage,
        Context ctx,
        AgentOptions? options = null,
        CancellationToken ct = default);
}
```

### `ChatSession`

Higher-level stateful wrapper — the preferred surface for REPL hosts and HTTP endpoints.

```csharp
// File: src/Agentic/Agency.Agentic/ChatSession.cs
using Agency.Agentic.Contexts;

namespace Agency.Agentic;

public sealed class ChatSession
{
    public ChatSession(Agent agent, AgentOptions options, ToolContext? toolContext = null);

    public LlmTokenUsage TotalUsage   { get; }
    public decimal       TotalCostUsd { get; }
    public int           TurnCount    { get; }
    public bool          IsStarted    { get; }

    /// <summary>
    /// Sends a message and streams back AgentEvents. Context is created lazily on the first call
    /// and reused for subsequent turns so conversation history is preserved.
    /// </summary>
    public IAsyncEnumerable<AgentEvent> SendAsync(string userMessage, CancellationToken ct = default);

    /// <summary>
    /// Resets the session by clearing conversation history and turn count.
    /// The next SendAsync call starts a fresh conversation.
    /// </summary>
    public void Reset();
}
```

### Agent Events

```csharp
// File: src/Agentic/Agency.Agentic/AgentEvents.cs
using System.Text.Json;

namespace Agency.Agentic;

public abstract record AgentEvent;

/// <summary>First event — emitted before any LLM call.</summary>
public sealed record SessionStartedEvent(string SessionId) : AgentEvent;

/// <summary>Emitted after each LLM response is appended to the conversation.</summary>
public sealed record AssistantTurnEvent(ChatMessage Message) : AgentEvent;

/// <summary>Emitted when a tool call is received from the stream, before execution begins.</summary>
public sealed record ToolUseReceivedEvent(string ToolName, string ToolUseId) : AgentEvent;

/// <summary>Emitted after a tool has been invoked and its result is ready.</summary>
public sealed record ToolInvokedEvent(string ToolName, JsonElement Input, ToolResult Result) : AgentEvent;

/// <summary>Emitted after each complete iteration (LLM call + optional tool calls).</summary>
public sealed record IterationCompletedEvent(int Iteration, LlmTokenUsage TurnUsage) : AgentEvent;

/// <summary>Streaming path only — one event per text token chunk.</summary>
public sealed record TextDeltaEvent(string Delta) : AgentEvent;

/// <summary>Terminal event — always the last event emitted by the agent loop.</summary>
public sealed record AgentResultEvent(
    AgentResultStatus Status,
    string?           FinalText,
    LlmTokenUsage     TotalUsage,
    decimal           TotalCostUsd) : AgentEvent;

public enum AgentResultStatus { Success, MaxStepsReached, BudgetExceeded, Error }

public sealed record LlmTokenUsage(long InputTokens, long OutputTokens)
{
    public long TotalTokens => InputTokens + OutputTokens;
}
```

### Stop Conditions

```csharp
// File: src/Agentic/Agency.Agentic/StopConditions.cs
using Agency.Agentic.Contexts;

namespace Agency.Agentic;

/// <summary>Predicate evaluated after each assistant turn to decide whether the loop should halt.</summary>
public delegate bool StopCondition(Context ctx, ChatMessage lastResponse);

public static class StopConditions
{
    public static StopCondition StepCountIs(int n);
    public static readonly StopCondition NoToolCalls;
    public static StopCondition BudgetExceeded(decimal usd);
    public static StopCondition TokensExceeded(long total);
    public static StopCondition Any(params StopCondition[] conditions);
}
```

### `Context` and Sub-Contexts

```csharp
// File: src/Agentic/Agency.Agentic/Contexts/Context.cs
namespace Agency.Agentic.Contexts;

public sealed record Context
{
    public required QueryContext    Query        { get; init; }
    public KnowledgeContext         Knowledge    { get; init; } = KnowledgeContext.Empty;
    public MemoryContext            Memory       { get; init; } = MemoryContext.Empty;
    public ToolContext              Tools        { get; init; } = ToolContext.Empty;
    public UserSpecificContext      User         { get; init; } = UserSpecificContext.Empty;
    public TemporalContext          Temporal     { get; init; } = TemporalContext.Empty;
    public EnvironmentalContext     Environment  { get; init; } = EnvironmentalContext.Empty;
    public IConversationManager     Conversation { get; init; }  // default: InMemoryConversationManager
    public int                      IterationCount { get; }      // loop-owned
    public decimal                  TotalCostUsd   { get; }      // loop-owned
    public LlmTokenUsage            TotalUsage     { get; }      // loop-owned
}
```

| Sub-Context | Key Members |
|---|---|
| `QueryContext` | `Prompt` — the initial user message |
| `KnowledgeContext` | `Facts: IReadOnlyList<string>` — re-injected into the system prompt each iteration |
| `MemoryContext` | `ShortTermMemory` (seeded as prior turns), `LongTermMemory` (injected into system prompt) |
| `ToolContext` | `Registry: IToolRegistry` |
| `UserSpecificContext` | `Name: string?` |
| `TemporalContext` | `CurrentDateUtc: DateTimeOffset?` |
| `EnvironmentalContext` | `OperatingSystem: string?` |

### `IConversationManager`

```csharp
// File: src/Agentic/Agency.Agentic/IConversationManager.cs
namespace Agency.Agentic;

public interface IConversationManager
{
    IReadOnlyList<ChatMessage> Messages { get; }
    void Append(ChatMessage message);
}
```

`InMemoryConversationManager` is the default implementation.

### `AgentOptions`

```csharp
// File: src/Agentic/Agency.Agentic/AgentOptions.cs
namespace Agency.Agentic;

public sealed class AgentOptions
{
    public string             DefaultClientName  { get; set; }
    public string?            DefaultModel       { get; set; }
    public bool               Stream             { get; set; } = true;
    public int?               TurnTimeoutSeconds { get; set; }
    public LlmClientOptions[] LLmClients         { get; set; } = [];
}
```

### `Models`

Discovers available models from all configured LLM providers.

```csharp
// File: src/Agentic/Agency.Agentic/Models.cs
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agency.Agentic;

public sealed class Models
{
    public const string ActivitySourceName = "Agency.Agentic.Models";
    public const string MeterName          = "Agency.Agentic.Models";

    public Models(IOptions<AgentOptions> agentOptions, ILogger<Models>? logger = null);

    /// <summary>Queries each configured provider and returns models grouped by their LlmClientOptions.</summary>
    public Task<IEnumerable<IGrouping<LlmClientOptions, Model>>> GetAllAsync(
        CancellationToken cancellationToken = default);
}
```

### `SystemPromptBuilder`

```csharp
// File: src/Agentic/Agency.Agentic/SystemPromptBuilder.cs
using Agency.Agentic.Contexts;

namespace Agency.Agentic;

/// <summary>Pure function that assembles the system prompt from the current Context.</summary>
public static class SystemPromptBuilder
{
    public static string Build(Context ctx);
}
```

### Tool Registry

```csharp
// File: src/Agentic/Agency.Agentic/Tools/ToolRegistry.cs
using Agency.Llm.Common.Tools;

namespace Agency.Agentic.Tools;

public sealed class ToolRegistry : IToolRegistry
{
    public static readonly IToolRegistry Empty; // no-op; returns error on any invocation

    public ToolRegistry(IEnumerable<ITool> tools);
    public ToolRegistry();

    public void Register(ITool tool);
    public IReadOnlyList<ToolDefinition> ListDefinitions();
    public IReadOnlyList<(bool Enabled, ToolDefinition Definition)> ListAllDefinitions();
    public Task<ToolResult> InvokeAsync(string name, JsonElement input, CancellationToken ct);
    public void DisableToolByUser(string name);
    public void EnableToolByUser(string name);
    public void DisabledToolBySystem(string name);
    public void EnableToolBySystem(string name);
}
```

### MCP Types

```csharp
// File: src/Agentic/Agency.Agentic/Tools/McpClientOptions.cs
namespace Agency.Agentic.Tools;

public enum McpTransportKind { Stdio, Http }

public sealed class McpServerConfig
{
    public string                       Name                 { get; set; }
    public McpTransportKind             Transport            { get; set; }
    public string?                      Command              { get; set; }  // Stdio only
    public string[]?                    Arguments            { get; set; }  // Stdio only
    public Dictionary<string, string?>? EnvironmentVariables { get; set; } // Stdio only
    public string?                      Url                  { get; set; }  // Http only
}

public sealed class McpClientOptions
{
    public McpServerConfig[] Servers { get; set; } = [];
}
```

```csharp
// File: src/Agentic/Agency.Agentic/Tools/McpClientPool.cs
namespace Agency.Agentic.Tools;

public sealed class McpClientPool : IAsyncDisposable
{
    /// <summary>Gets all tools discovered from every connected MCP server.</summary>
    public IReadOnlyList<ITool> Tools { get; }

    /// <summary>
    /// Connects to each server in options, lists its tools, and returns an initialized pool.
    /// </summary>
    public static Task<McpClientPool> CreateAsync(McpClientOptions options, CancellationToken ct = default);

    public ValueTask DisposeAsync();
}
```

## How It Works

The loop implemented internally in `Agent` follows this pattern on every iteration:

```
1. Seed conversation with the user prompt (first iteration only)
2. Build system prompt via SystemPromptBuilder.Build(ctx)
3. Call IChatClient:
   • stream=true  → GetStreamingResponseAsync → yield TextDeltaEvent per token chunk
   • stream=false → GetResponseAsync          → single batch response
4. Emit AssistantTurnEvent + IterationCompletedEvent
5. Evaluate StopCondition(ctx, lastMessage)
   └── true → AgentResultEvent → break
6. Extract FunctionCallContent items from the assistant message
7. Execute all tool calls in parallel → ToolInvokedEvent (one per tool)
8. Append tool results to conversation as Tool-role messages (one per call)
9. Increment IterationCount → repeat
```

### Practical Usage

```csharp
using Agency.Agentic;
using Agency.Agentic.Contexts;
using Agency.Agentic.Tools;
using Microsoft.Extensions.AI;

// Build a tool registry
var registry = new ToolRegistry([new ReadFileTool(), new ExecutePowershellTool()]);
var toolCtx  = new ToolContext { Registry = registry };

// Create the agent (IChatClient from Microsoft.Extensions.AI)
IChatClient chatClient = /* e.g. claudeClient.AsChatClient() */;
var agent = new Agent(chatClient, "claude-opus-4-6", clientType: "Claude");

// Use ChatSession for multi-turn convenience
var session = new ChatSession(agent, new AgentOptions(), toolCtx);

await foreach (AgentEvent evt in session.SendAsync("List files in /tmp", ct))
{
    switch (evt)
    {
        case TextDeltaEvent d:
            Console.Write(d.Delta);
            break;
        case ToolInvokedEvent t:
            Console.WriteLine($"\n[tool: {t.ToolName}]");
            break;
        case AgentResultEvent r:
            Console.WriteLine($"\nDone ({r.Status}). Tokens: {r.TotalUsage.TotalTokens}");
            break;
    }
}
```

### Stop Conditions

```csharp
using Agency.Agentic;

// Default (applied automatically when stopWhen is null)
StopConditions.Any(StopConditions.NoToolCalls, StopConditions.StepCountIs(20));

// Custom budget + iteration guard
StopConditions.Any(
    StopConditions.NoToolCalls,
    StopConditions.BudgetExceeded(0.50m),
    StopConditions.TokensExceeded(100_000));
```

### Connecting MCP Servers

```csharp
using Agency.Agentic.Tools;

await using var pool = await McpClientPool.CreateAsync(new McpClientOptions
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
        new McpServerConfig
        {
            Name      = "github",
            Transport = McpTransportKind.Http,
            Url       = "http://localhost:3001",
        },
    ]
}, ct);

var registry = new ToolRegistry(pool.Tools);
var toolCtx  = new ToolContext { Registry = registry };
```

## Agent Tools

| Tool class | Tool name | Description | Key Parameters |
|---|---|---|---|
| `AgentTool` | `subagent_tool` | Delegates a focused task to a child agent with its own `ToolRegistry`. | `prompt` (required), `clientName` (optional), `model` (optional) |
| `ExecutePowershellTool` | `execute_powershell` | Executes PowerShell commands in a fresh `Runspace`; formats multi-object output as Markdown tables. | `command` (required) |
| `ReadFileTool` | `read_file` | Reads and returns the contents of a file at a given path. | `path` (required) |
| `WriteFileTool` | `write_file` | Writes content to a file at a given path. | `path`, `content` (required) |
| `McpProxyTool` | *(per-tool name from server)* | Adapts a discovered `McpClientTool` to the `ITool` interface; one instance per tool per MCP server. Name and schema are sourced directly from the MCP server at connection time. | Varies by MCP server |

## Observability

### `Agent`

| Signal | Name | Tags |
|---|---|---|
| `ActivitySource` | `Agency.Agentic.Agent` | — |
| `Meter` | `Agency.Agentic.Agent` | — |
| Counter | `agent.turns` | `agent.model`, `agent.client_type`, `agent.stream` |
| Counter | `agent.errors` | same |
| Counter | `agent.tool.calls` | same + `agent.tool.name`, `agent.tool.error` |
| Counter | `agent.tokens` | `agent.model`, `agent.client_type`, `agent.token.type` |
| Histogram | `agent.turn.duration` (ms) | `agent.model`, `agent.client_type`, `agent.stream` |

### `Models`

| Signal | Name |
|---|---|
| `ActivitySource` | `Agency.Agentic.Models` |
| `Meter` | `Agency.Agentic.Models` |
| Counter | `models.requests` |
| Counter | `models.errors` |
| Counter | `models.returned` |
| Histogram | `models.duration` (ms) |

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Llm.Common]] | `Agent` depends on `IToolRegistry`, `ITool`, `ToolDefinition`, `ToolResult`, and `LlmClientOptions` from this project |
| [[Agency.Llm.Claude]] | Concrete `IChatClient` adapter for the Anthropic API |
| [[Agency.Llm.OpenAI]] | Concrete `IChatClient` adapter for the OpenAI-compatible API |
| [[Agency.Agentic.Console]] | REPL harness that creates `ChatSession` and renders `AgentEvent` streams to the terminal |

## Design Notes

- **`IChatClient` over `ILlmClient`** — `Agent` depends on `Microsoft.Extensions.AI.IChatClient` rather than the project's own `ILlmClient`. This allows any MEA-compatible client (including the built-in `OpenAIChatClient`) to drive the loop without an adapter layer; `ILlmClient` implementations expose `.AsChatClient()` bridges where needed.
- **`Context` is caller-owned, loop-mutates only counters** — `IterationCount`, `TotalCostUsd`, and `TotalUsage` are the only properties mutated by the loop (`internal set`). All other context properties are `init`-only, making session state predictable and easy to snapshot for testing.
- **`SystemPromptBuilder` is a pure function** — The system prompt is rebuilt from `Context` on every iteration so `KnowledgeContext` facts are always fresh. Being a `static` pure function makes it unit-testable in complete isolation from the agent loop.
- **Two-tier tool disabling** — `ToolRegistry` distinguishes user-initiated disables (`DisableToolByUser`) from system-initiated disables (`DisabledToolBySystem`). `ListAllDefinitions()` hides system-disabled tools entirely; user-disabled tools remain visible with `Enabled = false`, enabling UI toggle flows.
- **`McpProxyTool` is `internal`** — callers never instantiate `McpProxyTool` directly; `McpClientPool.CreateAsync` wraps each discovered `McpClientTool` and exposes the results through the `Tools` property. This keeps the MCP SDK types contained behind the pool boundary.
- **`McpClientPool` is `IAsyncDisposable`** — each `McpClient` holds an open connection to its server process or HTTP endpoint. Disposing the pool closes all connections; callers should use `await using` to ensure cleanup even on cancellation.
- **`ChatSession` is not thread-safe** — create one instance per user or logical connection; at most one `SendAsync` call should be in flight at a time. Call `Reset()` to clear conversation history and start a fresh session without creating a new instance.
