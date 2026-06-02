# Agency.Harness

#agent #loop #tools #events #orchestration #mcp #observability

## What It Is

Agency.Harness is the autonomous agent loop library that drives a think → act → observe cycle until a `StopCondition` fires. It accepts an `IChatClient` (via `Microsoft.Extensions.AI`), a `Context` object aggregating the user query, conversation history, tools, memory, and grounding data, and yields a stream of typed `AgentEvent` records so callers can react to each stage without polling. MCP (Model Context Protocol) server tools are supported via `McpClientPool`, which connects to one or more MCP servers and exposes their tools as `ITool` instances compatible with `ToolRegistry`.

**Namespace:** `Agency.Harness`

## API Surface

### `Agent`

```csharp
// File: src/Harness/Agency.Harness/Agent.cs
using Agency.Harness.Contexts;
using Agency.Harness.Hooks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agency.Harness;

public sealed class Agent
{
    public const string ActivitySourceName = "Agency.Harness.Agent";
    public const string MeterName          = "Agency.Harness.Agent";

    public Agent(
        IChatClient llm,
        string model,
        string? clientType = null,
        StopCondition? stopWhen = null,  // default: Any(NoToolCalls, StepCountIs(20))
        AgentHooks? hooks = null,        // default: AgentHooks.None
        ILogger<Agent>? logger = null);

    public string Model      { get; }
    public string ClientType { get; }

    /// <summary>Creates a Context pre-populated with temporal context and the initial prompt.</summary>
    public static Context CreateContext(
        string initialPrompt,
        ToolContext? tools = null,
        EnvironmentalContext? environment = null);

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
// File: src/Harness/Agency.Harness/ChatSession.cs
using Agency.Harness.Contexts;

namespace Agency.Harness;

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
// File: src/Harness/Agency.Harness/AgentEvents.cs
using System.Text.Json;

namespace Agency.Harness;

public abstract record AgentEvent;

/// <summary>First event — emitted before any LLM call.</summary>
public sealed record SessionStartedEvent(string SessionId) : AgentEvent;

/// <summary>Emitted after each LLM response is appended to the conversation.</summary>
public sealed record AssistantTurnEvent(ChatMessage Message) : AgentEvent;

/// <summary>Emitted after a tool has been invoked and its result is ready.</summary>
public sealed record ToolInvokedEvent(string ToolName, JsonElement Input, ToolResult Result) : AgentEvent;

/// <summary>Emitted after each complete iteration (LLM call + optional tool calls).</summary>
public sealed record IterationCompletedEvent(int Iteration, LlmTokenUsage TurnUsage, TimeSpan LlmDuration) : AgentEvent;

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

### Hooks

```csharp
// File: src/Harness/Agency.Harness/Hooks/PreToolUseDecision.cs
namespace Agency.Harness.Hooks;

public abstract record PreToolUseDecision
{
    public sealed record Allow : PreToolUseDecision;
    public sealed record Deny(string Reason) : PreToolUseDecision;
    public sealed record Rewrite(JsonElement NewInput) : PreToolUseDecision;
    public static PreToolUseDecision Allowed { get; }
}
```

Hook context records passed to each delegate:

| Record | Constructor Parameters |
|--------|----------------------|
| `SessionStartedHookContext` | `string SessionId, Context AgentContext` |
| `PreToolUseHookContext` | `string ToolName, JsonElement Input, Context AgentContext` |
| `PostToolUseHookContext` | `string ToolName, JsonElement Input, ToolResult Result, Context AgentContext` |
| `AssistantTurnHookContext` | `ChatMessage Message, Context AgentContext` |
| `StopHookContext` | `AgentResultEvent Result, Context AgentContext` |

```csharp
// File: src/Harness/Agency.Harness/Hooks/AgentHooks.cs
namespace Agency.Harness.Hooks;

public sealed record AgentHooks
{
    public Func<SessionStartedHookContext, CancellationToken, Task>?                         OnSessionStarted { get; init; }
    public Func<PreToolUseHookContext, CancellationToken, Task<PreToolUseDecision>>?         OnPreToolUse     { get; init; }
    public Func<PostToolUseHookContext, CancellationToken, Task>?                            OnPostToolUse    { get; init; }
    public Func<AssistantTurnHookContext, CancellationToken, Task>?                          OnAssistantTurn  { get; init; }
    public Func<StopHookContext, CancellationToken, Task>?                                   OnStop           { get; init; }
    public static AgentHooks None { get; }
}
```

```csharp
// File: src/Harness/Agency.Harness/Hooks/AgentHooksExtensions.cs
namespace Agency.Harness.Hooks;

public static class AgentHooksExtensions
{
    public static AgentHooks Compose(this AgentHooks first, AgentHooks second);
}
```

**Built-in hooks:**

- `BlockListHooks.Dangerous` — static `AgentHooks` with `OnPreToolUse` that denies shell commands (targeting `Bash`, `ExecutePowershell`, `ExecutePowershellTool`) containing patterns like `rm -rf`, `DROP TABLE`, `format c:`, `del /f /s`.
- `AuditHooks.ForLogger(ILogger logger)` — returns `AgentHooks` with `OnPreToolUse` and `OnPostToolUse` that log at `Information` level; `OnPreToolUse` always returns `Allow`.

### Stop Conditions

```csharp
// File: src/Harness/Agency.Harness/StopConditions.cs
using Agency.Harness.Contexts;

namespace Agency.Harness;

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
// File: src/Harness/Agency.Harness/Contexts/Context.cs
namespace Agency.Harness.Contexts;

public sealed record Context
{
    public required QueryContext    Query        { get; init; }
    public KnowledgeContext         Knowledge    { get; set; }  = KnowledgeContext.Empty;  // settable: hooks may refresh facts mid-session
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
| `EnvironmentalContext` | `OperatingSystem: string?`, `ContextWindowSize: int?` |

### `IConversationManager`

```csharp
// File: src/Harness/Agency.Harness/IConversationManager.cs
namespace Agency.Harness;

public interface IConversationManager
{
    IReadOnlyList<ChatMessage> Messages { get; }
    void Append(ChatMessage message);
}
```

`InMemoryConversationManager` is the default implementation.

### `AgentOptions`

```csharp
// File: src/Harness/Agency.Harness/AgentOptions.cs
namespace Agency.Harness;

public sealed class AgentOptions
{
    public string             DefaultClientName  { get; set; }
    public string?            DefaultModel       { get; set; }
    public int?               TurnTimeoutSeconds { get; set; }
    public LlmClientOptions[] LLmClients         { get; set; } = [];
    public int?               ContextWindowSize  { get; set; }
}
```

### `Models`

Discovers available models from all configured LLM providers.

```csharp
// File: src/Harness/Agency.Harness/Models.cs
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agency.Harness;

public sealed class Models
{
    public const string ActivitySourceName = "Agency.Harness.Models";
    public const string MeterName          = "Agency.Harness.Models";

    public Models(IOptions<AgentOptions> agentOptions, ILogger<Models>? logger = null);

    /// <summary>Queries each configured provider and returns models grouped by their LlmClientOptions.</summary>
    public Task<IEnumerable<IGrouping<LlmClientOptions, Model>>> GetAllAsync(
        CancellationToken cancellationToken = default);
}
```

### `SystemPromptBuilder`

```csharp
// File: src/Harness/Agency.Harness/SystemPromptBuilder.cs
using Agency.Harness.Contexts;

namespace Agency.Harness;

/// <summary>Pure function that assembles the system prompt from the current Context.</summary>
public static class SystemPromptBuilder
{
    public static string Build(Context ctx);
}
```

### Tool Registry

```csharp
// File: src/Harness/Agency.Harness/Tools/ToolRegistry.cs
using Agency.Llm.Common.Tools;

namespace Agency.Harness.Tools;

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
// File: src/Harness/Agency.Harness/Tools/McpClientOptions.cs
namespace Agency.Harness.Tools;

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
// File: src/Harness/Agency.Harness/Tools/McpClientPool.cs
namespace Agency.Harness.Tools;

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
1. Yield SessionStartedEvent
2. → OnSessionStarted hook (if set)
3. Seed conversation with the user prompt (first iteration only)
4. Build system prompt via SystemPromptBuilder.Build(ctx) [every iteration]
5. Call IChatClient.GetResponseAsync (single batch response)
6. Append assistant message to conversation
7. Yield AssistantTurnEvent
8. → OnAssistantTurn hook (if set)
9. Yield IterationCompletedEvent
10. Evaluate StopCondition(ctx, lastMessage)
    └── true → OnStop hook → yield AgentResultEvent → break
11. Extract FunctionCallContent items from the assistant message
12. For each tool call (in parallel):
    a. → OnPreToolUse hook → Allow / Deny (skip InvokeAsync) / Rewrite (replace input)
    b. InvokeAsync(name, input, ct)
    c. → OnPostToolUse hook (fires even on error)
    d. Yield ToolInvokedEvent
13. Append tool results to conversation as Tool-role messages
14. Repeat from step 4
```

### Practical Usage

```csharp
using Agency.Harness;
using Agency.Harness.Contexts;
using Agency.Harness.Tools;
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
        case ToolInvokedEvent t:
            Console.WriteLine($"[tool: {t.ToolName}]");
            break;
        case AgentResultEvent r:
            Console.WriteLine($"Done ({r.Status}). Tokens: {r.TotalUsage.TotalTokens}");
            break;
    }
}
```

### Using Hooks

```csharp
using Agency.Harness;
using Agency.Harness.Hooks;
using Microsoft.Extensions.Logging;

// Block dangerous commands and audit all tool calls
AgentHooks hooks = BlockListHooks.Dangerous
    .Compose(AuditHooks.ForLogger(logger));

var agent = new Agent(chatClient, "claude-opus-4-6", hooks: hooks);

// Custom hook: inject context when the session starts
var customHooks = new AgentHooks
{
    OnSessionStarted = (ctx, _) =>
    {
        ctx.AgentContext.Knowledge = ctx.AgentContext.Knowledge with
        {
            Facts = [.. ctx.AgentContext.Knowledge.Facts, "Today's sprint: auth hardening"],
        };
        return Task.CompletedTask;
    },
    OnPreToolUse = (ctx, _) =>
    {
        // Rewrite relative paths to absolute before the tool sees them
        return Task.FromResult(PreToolUseDecision.Allowed);
    },
};
```

### Stop Conditions

```csharp
using Agency.Harness;

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
using Agency.Harness.Tools;

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
| `ActivitySource` | `Agency.Harness.Agent` | — |
| `Meter` | `Agency.Harness.Agent` | — |
| Counter | `agent.turns` | `agent.model`, `agent.client_type` |
| Counter | `agent.errors` | same |
| Counter | `agent.tool.calls` | same + `agent.tool.name`, `agent.tool.error` |
| Counter | `agent.tokens` | `agent.model`, `agent.client_type`, `agent.token.type` |
| Histogram | `agent.turn.duration` (ms) | `agent.model`, `agent.client_type` |

### `Models`

| Signal | Name |
|---|---|
| `ActivitySource` | `Agency.Harness.Models` |
| `Meter` | `Agency.Harness.Models` |
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
| [[Agency.Harness.Console]] | REPL harness that creates `ChatSession` and renders `AgentEvent` streams to the terminal |

## Design Notes

- **`IChatClient` over `ILlmClient`** — `Agent` depends on `Microsoft.Extensions.AI.IChatClient` rather than the project's own `ILlmClient`. This allows any MEA-compatible client (including the built-in `OpenAIChatClient`) to drive the loop without an adapter layer; `ILlmClient` implementations expose `.AsChatClient()` bridges where needed.
- **`Context` is caller-owned, loop-mutates only counters** — `IterationCount`, `TotalCostUsd`, and `TotalUsage` are the only properties mutated by the loop (`internal set`). `Knowledge` is `set` (not `init`) so lifecycle hooks such as `OnSessionStarted` can refresh domain facts mid-session — the next iteration's `SystemPromptBuilder.Build` picks them up. All remaining context properties are `init`-only, keeping session state predictable and easy to snapshot for testing.
- **`SystemPromptBuilder` is a pure function** — The system prompt is rebuilt from `Context` on every iteration so `KnowledgeContext` facts are always fresh. Being a `static` pure function makes it unit-testable in complete isolation from the agent loop.
- **Two-tier tool disabling** — `ToolRegistry` distinguishes user-initiated disables (`DisableToolByUser`) from system-initiated disables (`DisabledToolBySystem`). `ListAllDefinitions()` hides system-disabled tools entirely; user-disabled tools remain visible with `Enabled = false`, enabling UI toggle flows.
- **`McpProxyTool` is `internal`** — callers never instantiate `McpProxyTool` directly; `McpClientPool.CreateAsync` wraps each discovered `McpClientTool` and exposes the results through the `Tools` property. This keeps the MCP SDK types contained behind the pool boundary.
- **`McpClientPool` is `IAsyncDisposable`** — each `McpClient` holds an open connection to its server process or HTTP endpoint. Disposing the pool closes all connections; callers should use `await using` to ensure cleanup even on cancellation.
- **`ChatSession` is not thread-safe** — create one instance per user or logical connection; at most one `SendAsync` call should be in flight at a time. Call `Reset()` to clear conversation history and start a fresh session without creating a new instance.
- **Hooks are interceptors, events are observers** — `IAsyncEnumerable<AgentEvent>` lets callers observe what happened after the fact; `AgentHooks` lets callers block, rewrite, or react *before* the action completes. `OnPreToolUse` is the only hook that can alter agent behaviour (via `Deny`/`Rewrite`); all others are fire-and-forget.
- **`Compose()` uses most-restrictive-wins for `OnPreToolUse`** — when composing two hooks that both have `OnPreToolUse`, they run concurrently via `Task.WhenAll`; the result priority is Deny > Rewrite > Allow. All other delegate slots run sequentially (first then second) so ordering is deterministic.
