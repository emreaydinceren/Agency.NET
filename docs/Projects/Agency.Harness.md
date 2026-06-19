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

    /// <summary>
    /// Fires the OnSessionEnd hook for the given context, if one is set.
    /// Called by ChatSession.DisposeAsync at the end of a session.
    /// </summary>
    public Task RaiseSessionEndAsync(Context ctx, CancellationToken ct = default);

    /// <summary>Creates a Context pre-populated with temporal context and the initial prompt.</summary>
    public static Context CreateContext(
        string initialPrompt,
        ToolContext? tools = null,
        EnvironmentalContext? environment = null,
        UserSpecificContext? user = null);

    /// <summary>
    /// Executes one user turn. On the first call, delegates to the internal loop; on subsequent calls,
    /// appends the message then runs the loop. Fires OnUserPromptSubmit before entering the loop.
    /// Applies TurnTimeoutSeconds when configured.
    /// </summary>
    public IAsyncEnumerable<AgentEvent> ChatAsync(
        string userMessage,
        Context ctx,
        AgentOptions? options = null,
        CancellationToken ct = default);
}
```

### `ChatSession`

Higher-level stateful wrapper — the preferred surface for REPL hosts and HTTP endpoints. Implements `IAsyncDisposable`; dispose fires the `OnSessionEnd` hook once.

```csharp
// File: src/Harness/Agency.Harness/ChatSession.cs
using Agency.Harness.Contexts;

namespace Agency.Harness;

public sealed class ChatSession : IAsyncDisposable
{
    public ChatSession(
        Agent agent,
        AgentOptions options,
        ToolContext? toolContext = null,
        UserSpecificContext? user = null);

    public LlmTokenUsage TotalUsage   { get; }
    public decimal       TotalCostUsd { get; }
    public int           TurnCount    { get; }
    public bool          IsStarted    { get; }

    /// <summary>
    /// Switches the agent used for subsequent turns. Conversation history is preserved.
    /// </summary>
    public void SetAgent(Agent agent);

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

    /// <summary>
    /// Fires the agent's OnSessionEnd hook (once) then marks the session disposed.
    /// Safe to call multiple times.
    /// </summary>
    public ValueTask DisposeAsync();
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
| `SessionEndedHookContext` | `string SessionId, Context AgentContext` |
| `UserPromptSubmitHookContext` *(internal)* | `string Prompt, Context AgentContext` |
| `PreIterationHookContext` *(internal)* | `Context AgentContext` |
| `PostToolBatchHookContext` *(internal)* | `IReadOnlyList<ToolInvokedEvent> Events, Context AgentContext` |

```csharp
// File: src/Harness/Agency.Harness/Hooks/AgentHooks.cs
using Agency.Harness.Contexts;

namespace Agency.Harness.Hooks;

public sealed record AgentHooks
{
    /// <summary>Fires once before the first iteration of the agent loop.</summary>
    public Func<SessionStartedHookContext, CancellationToken, Task>?                         OnSessionStarted    { get; init; }

    /// <summary>
    /// Fires every time ChatAsync is called (i.e., on every user turn), before the agent loop starts.
    /// Receives the raw Context; intended for prompt-level side-effects such as recording turn timestamps.
    /// </summary>
    public Func<Context, CancellationToken, Task>?                                           OnUserPromptSubmit  { get; init; }

    /// <summary>
    /// Fires at the start of every agent loop iteration, before the system prompt is rebuilt.
    /// Intended for retrieval-engine injection (mutate Context.Knowledge and Context.Memory here).
    /// </summary>
    public Func<Context, CancellationToken, Task>?                                           OnPreIteration      { get; init; }

    public Func<PreToolUseHookContext, CancellationToken, Task<PreToolUseDecision>>?         OnPreToolUse        { get; init; }
    public Func<PostToolUseHookContext, CancellationToken, Task>?                            OnPostToolUse       { get; init; }

    /// <summary>
    /// Fires after Task.WhenAll of all parallel tool calls completes, before the next LLM call.
    /// Receives all tool events from the batch.
    /// </summary>
    public Func<IReadOnlyList<ToolInvokedEvent>, Context, CancellationToken, Task>?          OnPostToolBatch     { get; init; }

    public Func<AssistantTurnHookContext, CancellationToken, Task>?                          OnAssistantTurn     { get; init; }
    public Func<StopHookContext, CancellationToken, Task>?                                   OnStop              { get; init; }

    /// <summary>
    /// Fires once when the owning ChatSession is disposed, signalling end-of-session.
    /// Unlike OnStop (which fires every turn), this fires exactly once per session lifetime.
    /// </summary>
    public Func<SessionEndedHookContext, CancellationToken, Task>?                           OnSessionEnd        { get; init; }

    public static AgentHooks None { get; }
}
```

```csharp
// File: src/Harness/Agency.Harness/Hooks/AgentHooksExtensions.cs
namespace Agency.Harness.Hooks;

public static class AgentHooksExtensions
{
    /// <summary>
    /// Returns a new AgentHooks where second runs after first. For OnPreToolUse, the most
    /// restrictive decision wins (Deny > Rewrite > Allow). All other delegates run sequentially.
    /// </summary>
    public static AgentHooks Compose(this AgentHooks first, AgentHooks second);

    /// <summary>
    /// Returns a new AgentHooks where first runs before self (the baseline). Escape hatch for
    /// callers who need to observe the un-enriched Context before baseline hooks have run.
    /// </summary>
    public static AgentHooks ComposeBefore(this AgentHooks self, AgentHooks first);

    /// <summary>
    /// Folds three hook sources — baseline, configured, user — into a single composed AgentHooks.
    /// Null sources are skipped. Deny-wins across all three sources is guaranteed by Compose.
    /// </summary>
    internal static AgentHooks? Fold(AgentHooks? baseline, AgentHooks? configured, AgentHooks? user);
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
    public MemoryContext            Memory       { get; set; }  = MemoryContext.Empty;      // settable: retrieval engine injects records in OnPreIteration
    public ToolContext              Tools        { get; init; } = ToolContext.Empty;
    public FocusContext             Focus        { get; set; }  = FocusContext.Empty;       // settable: SetFocusTool biases retrieval
    public SessionContext           Session      { get; set; }  = SessionContext.Empty;     // set once by the loop; stable for session lifetime
    public UserSpecificContext      User         { get; init; } = UserSpecificContext.Empty;
    public TemporalContext          Temporal     { get; init; } = TemporalContext.Empty;
    public EnvironmentalContext     Environment  { get; init; } = EnvironmentalContext.Empty;
    public IConversationManager     Conversation { get; init; }  // default: InMemoryConversationManager
    public int                      IterationCount         { get; }      // loop-owned
    public decimal                  TotalCostUsd           { get; }      // loop-owned
    public LlmTokenUsage            TotalUsage             { get; }      // loop-owned
    public DateTimeOffset?          MemoryLastRetrievedAt  { get; set; } // written by retrieval engine in OnPreIteration
}
```

| Sub-Context | Key Members |
|---|---|
| `QueryContext` | `Prompt` — the initial user message |
| `KnowledgeContext` | `Facts: IReadOnlyList<string>` — re-injected into the system prompt each iteration |
| `MemoryContext` | `ShortTermMemory` (seeded as prior turns), `LongTermMemory` (injected into system prompt) |
| `ToolContext` | `Registry: IToolRegistry` |
| `FocusContext` | `Title: string?`, `Domain: string?`, `Tags: IReadOnlyList<string>` — narrows retrieval toward a task domain (Spec §6.7.1) |
| `SessionContext` | `Id: string?` — stable session identifier; assigned once by the loop on first turn |
| `UserSpecificContext` | `Id: string?` (memory partitioning key), `Name: string?` |
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
using Agency.Harness.Hooks;

namespace Agency.Harness;

public sealed class AgentOptions
{
    public string             DefaultClientName  { get; set; }
    public string?            DefaultModel       { get; set; }
    public int?               TurnTimeoutSeconds { get; set; }
    public LlmClientOptions[] LLmClients         { get; set; } = [];
    public int?               ContextWindowSize  { get; set; }

    /// <summary>
    /// When true, MCP tools are advertised with their parameter schemas withheld (placeholder) and
    /// descriptions reduced to one line, plus a tool_help meta-tool that reveals full detail on
    /// demand; native/internal tools are revealed in full. Reduces context size; tool-call behavior
    /// is unchanged. Default false (opt-in). The host wraps the ToolContext registry in a
    /// ProgressiveDiscoveryToolRegistry when set.
    /// </summary>
    public bool               ProgressiveDiscovery { get; set; }

    /// <summary>
    /// When true, the agent logs each tool call's full input arguments and a tool's error-result
    /// content verbatim. Verbose and potentially sensitive (file contents, commands, ids), so it is
    /// opt-in and meant for on-demand debugging. When false (default), tool calls and failures are
    /// still logged by name but their payloads are redacted.
    /// </summary>
    public bool               LogToolPayloads    { get; set; }

    /// <summary>
    /// Host-supplied user identity propagated into UserSpecificContext.Id for memory partitioning.
    /// </summary>
    public string?            UserId             { get; set; }

    /// <summary>
    /// Baseline hooks built by the memory pipeline (e.g. retrieval, timer restart).
    /// Set by AddAgencyMemory via PostConfigure; null when memory is disabled.
    /// Baseline hooks always run before UserHooks per spec §6.5.
    /// </summary>
    public AgentHooks?        BaselineHooks      { get; set; }

    /// <summary>
    /// Operator config hooks, composed between Baseline and User per §14.5.
    /// Set by AddAgencyConfiguredHooks via PostConfigure; null when config-driven hooks are disabled.
    /// </summary>
    public AgentHooks?        ConfiguredHooks    { get; set; }

    /// <summary>
    /// User-supplied hooks composed after the baseline hooks.
    /// When null, only BaselineHooks are used (if present).
    /// </summary>
    public AgentHooks?        UserHooks          { get; set; }
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

When `ctx.Tools.Registry` implements `IProgressiveDiscovery` (see below), `Build` appends one instruction
line telling the model that some tool schemas are withheld — a tool advertised with only a
`{"type":"object"}` schema is a deferred tool — and to call `tool_help(name)` to retrieve its full
schema before invoking it. With a plain registry, nothing is appended (current behavior).

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

    /// <summary>
    /// Synchronous registration path. Throws if the tool requires async definition resolution.
    /// Use RegisterAsync for tools whose GetDefinitionAsync is not immediately completed.
    /// </summary>
    public void Register(ITool tool);

    /// <summary>Async registration path for tools whose definition is not synchronously available.</summary>
    public ValueTask RegisterAsync(ITool tool, CancellationToken ct = default);

    public IReadOnlyList<ToolDefinition> ListDefinitions();
    public IReadOnlyList<(bool Enabled, ToolDefinition Definition)> ListAllDefinitions();
    public Task<ToolResult> InvokeAsync(string name, JsonElement input, CancellationToken ct);
    public void DisableToolByUser(string name);
    public void EnableToolByUser(string name);
    public void DisabledToolBySystem(string name);
    public void EnableToolBySystem(string name);
}
```

### Progressive Tool Discovery

An **opt-in** scheme that shrinks the per-iteration tool payload by progressive disclosure keyed on a
tool's **origin**: **MCP** tools have their description reduced to a one-line summary and their schema
fully withheld behind the bare `{"type":"object"}` placeholder — the system prompt instructs the model to
call `tool_help` to fetch the full schema before invoking them — because MCP catalogs are the large,
numerous, often-verbose tools where the savings matter most. **Native/internal** tools (the harness's own
`read_file`, `execute_powershell`, etc.) are advertised in **full** — complete schema and description — so
they invoke without a `tool_help` round-trip. The decorator is told which tools are MCP-originated via the
set of names passed to its constructor (the Console collects these from `McpClientPool.Tools`). It changes
only *what detail* is disclosed — **not how tools are called**: every real tool keeps its own name as the
call name, so permissions, hooks, the `agent.tool.calls` metric, and `ToolInvokedEvent` are all unaffected.
Implemented as a decorator over `IToolRegistry`, so `Agent` needs no changes (it still calls
`ListDefinitions()` and `InvokeAsync(name, …)`).

```csharp
// File: src/Harness/Agency.Harness/Tools/IProgressiveDiscovery.cs
namespace Agency.Harness.Tools;

/// <summary>Marker: lets callers (e.g. SystemPromptBuilder) detect progressive disclosure is active.</summary>
public interface IProgressiveDiscovery { }

// File: src/Harness/Agency.Harness/Tools/ProgressiveDiscoveryToolRegistry.cs
public sealed class ProgressiveDiscoveryToolRegistry : IToolRegistry, IProgressiveDiscovery
{
    public ProgressiveDiscoveryToolRegistry(IToolRegistry inner, IReadOnlySet<string> mcpToolNames);
    // ListDefinitions(): each inner tool is advertised according to its origin:
    //     • MCP tools (name ∈ mcpToolNames) → Description reduced to a one-line summary and
    //       InputSchema replaced by the bare {"type":"object"} placeholder (schema withheld
    //       behind tool_help).
    //     • native/internal tools → revealed in full (schema and description untouched).
    //   Then appends `tool_help` (which keeps its real schema). InvokeAsync routes "tool_help";
    //   everything else → inner. All other IToolRegistry members delegate to inner.
}

// File: src/Harness/Agency.Harness/Tools/ToolHelpTool.cs (internal)
//   tool_help(name) → returns the named tool's FULL description + indented schema as text,
//   read from the UNDECORATED inner registry. The escape hatch for deferred detail.
```

The summary is the description's first non-empty line, with any leading provenance prefix **preserved**
(e.g. `"Notion | Update a page…\nError Responses:…"` → `"Notion | Update a page…"`). The prefix is kept
because MCP tools whose names are operationId-derived (e.g. `API-get-self`) carry no server signal, so
the `Vendor |` prefix is the model's only inline cue to which server a tool belongs — dropping it lets
the model pick a wrong-server tool for an ambiguous request. The Console `dump-context` command also
groups tools under their server.

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
    /// <summary>Gets all tools discovered from every connected MCP server (flat across servers).</summary>
    public IReadOnlyList<ITool> Tools { get; }

    /// <summary>
    /// Gets the tool names discovered from each MCP server, keyed by server name in configured order.
    /// Retained so a tool can be attributed back to its originating server (the flat `Tools` list and
    /// `ToolDefinition` itself carry no provenance). Used by the Console `dump-context` grouping.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ToolNamesByServer { get; }

    /// <summary>
    /// Connects to each server in options, lists its tools, and returns an initialized pool.
    /// </summary>
    public static Task<McpClientPool> CreateAsync(McpClientOptions options, CancellationToken ct = default);

    public ValueTask DisposeAsync();
}
```

### Configuration-Driven Hooks

Types in `Agency.Harness.Hooks.Configuration` support operator-defined hooks loaded from `appsettings.json` (or any `IConfiguration` source). They are wired into the three-source fold as the middle `ConfiguredHooks` layer.

#### Enums

```csharp
// File: src/Harness/Agency.Harness/Hooks/Configuration/HookEventName.cs
namespace Agency.Harness.Hooks.Configuration;

// internal — used as dictionary key in HooksOptions and as the event router inside HookRegistry
internal enum HookEventName
{
    SessionStart,
    UserPromptSubmit,
    PreIteration,
    PreToolUse,
    PostToolUse,
    PostToolBatch,
    AssistantTurn,
    Stop,
    SessionEnd
}
```

```csharp
// File: src/Harness/Agency.Harness/Hooks/Configuration/HookHandlerKind.cs
namespace Agency.Harness.Hooks.Configuration;

// internal — selects the concrete IHookHandler implementation in HookHandlerFactory
internal enum HookHandlerKind
{
    Command,   // spawn an external process; payload delivered via stdin
    Http,      // POST to a URL; payload delivered as JSON body
    McpTool,   // reserved — V2
    Prompt,    // reserved — V2
    Agent      // reserved — V2
}
```

#### Configuration Model

```csharp
// File: src/Harness/Agency.Harness/Hooks/Configuration/HookHandlerConfig.cs
namespace Agency.Harness.Hooks.Configuration;

internal sealed class HookHandlerConfig
{
    public HookHandlerKind           Type    { get; set; } = HookHandlerKind.Command;
    public string?                   Command { get; set; }           // Command only
    public string[]                  Args    { get; set; } = [];     // Command only
    public int?                      Timeout { get; set; }           // seconds; default 30
    public string?                   Url     { get; set; }           // Http only
    public Dictionary<string, string>? Headers { get; set; }        // Http only
}
```

```csharp
// File: src/Harness/Agency.Harness/Hooks/Configuration/HookMatcherGroupConfig.cs
namespace Agency.Harness.Hooks.Configuration;

internal sealed class HookMatcherGroupConfig
{
    /// <summary>
    /// Tool-name filter applied before dispatching. Null / "*" matches all tools.
    /// Pipe-separated names (e.g. "bash|execute_powershell") match an exact set.
    /// Any other string is compiled as a Regex (250 ms timeout).
    /// </summary>
    public string?             Matcher { get; set; }
    public HookHandlerConfig[] Hooks   { get; set; } = [];
}
```

```csharp
// File: src/Harness/Agency.Harness/Hooks/Configuration/HooksOptions.cs
namespace Agency.Harness.Hooks.Configuration;

/// <summary>
/// Root configuration object. Extends Dictionary so IConfiguration.Bind can populate
/// it directly from a JSON object whose keys are HookEventName values.
/// </summary>
internal sealed class HooksOptions : Dictionary<HookEventName, HookMatcherGroupConfig[]>
{
    public Dictionary<HookEventName, HookMatcherGroupConfig[]> Hooks => this;
}
```

#### Handler Interfaces and Output

```csharp
// File: src/Harness/Agency.Harness/Hooks/Configuration/Handlers/IHookHandler.cs
using Agency.Harness.Hooks.Configuration;

namespace Agency.Harness.Hooks.Configuration.Handlers;

internal interface IHookHandler
{
    Task<HookHandlerOutput> InvokeAsync(HookPayload payload, CancellationToken ct);
}
```

```csharp
// File: src/Harness/Agency.Harness/Hooks/Configuration/Handlers/IHookHandlerFactory.cs
using Agency.Harness.Hooks.Configuration;

namespace Agency.Harness.Hooks.Configuration.Handlers;

internal interface IHookHandlerFactory
{
    IHookHandler Create(HookHandlerConfig config);
}
```

```csharp
// File: src/Harness/Agency.Harness/Hooks/Configuration/Handlers/HookHandlerOutput.cs
namespace Agency.Harness.Hooks.Configuration.Handlers;

internal sealed record HookHandlerOutput(
    int             ExitCode,
    JsonElement?    Json,       // leading JSON object parsed from stdout/body, if any
    string?         RawStdout,
    string?         RawStderr);

internal static class HookExitCodes
{
    internal const int Ok               = 0;
    internal const int NonBlockingError = 1;
    internal const int BlockingDeny     = 2;  // causes PreToolUse → Deny
    internal const int Timeout          = -1;
}
```

#### `HookRegistry` (internal)

`HookRegistry` is built once at DI startup. It compiles all matchers in its constructor so that per-invocation work is limited to `IsMatch` calls against the pre-built `HookMatcher` set.

```csharp
// File: src/Harness/Agency.Harness/Hooks/Configuration/HookRegistry.cs
using Agency.Harness.Hooks;
using Agency.Harness.Hooks.Configuration.Handlers;
using Microsoft.Extensions.Logging;

namespace Agency.Harness.Hooks.Configuration;

internal sealed class HookRegistry
{
    internal static readonly HookRegistry Empty;  // no-op registry; used when config hooks are absent

    internal HookRegistry(HooksOptions options, IHookHandlerFactory factory, ILogger? logger);

    /// <summary>
    /// Converts the compiled registry into an AgentHooks instance suitable for insertion as
    /// ConfiguredHooks in AgentOptions. Only hook slots that have at least one configured group
    /// are populated — the rest remain null so Compose short-circuits cleanly.
    /// </summary>
    internal AgentHooks ToAgentHooks();

    /// <summary>
    /// Aggregates multiple HookHandlerOutput values into a single PreToolUseDecision.
    /// Priority: BlockingDeny (exit 2 or JSON deny) > JSON Rewrite (tool_input field) > Allow.
    /// Non-zero / non-2 exit codes and handler errors are treated as non-blocking (fail-open).
    /// </summary>
    internal static PreToolUseDecision AggregateDecision(
        IEnumerable<HookHandlerOutput> outputs,
        JsonElement _);
}
```

#### `HookMatcher` (internal)

```csharp
// File: src/Harness/Agency.Harness/Hooks/Configuration/HookMatcher.cs
namespace Agency.Harness.Hooks.Configuration;

/// <summary>
/// Compiled matcher for a HookMatcherGroupConfig.Matcher string.
/// Supports three modes: MatchAll ("*" or null), ExactSet ("bash|powershell"), Regex (anything else).
/// Regex patterns are compiled with a 250 ms match timeout.
/// </summary>
internal sealed class HookMatcher
{
    internal static HookMatcher Create(string? matcher);
    internal bool IsMatch(string candidate);
}
```

#### `HookPayload` (internal)

```csharp
// File: src/Harness/Agency.Harness/Hooks/Configuration/HookPayload.cs
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agency.Harness.Hooks.Configuration;

/// <summary>
/// JSON payload sent to every hook handler (via stdin for Command, via POST body for Http).
/// Serialized with snake_case_lower naming; null fields are omitted.
/// </summary>
internal sealed record HookPayload
{
    public string?              SessionId      { get; init; }
    public string?              HookEventName  { get; init; }
    public string?              Cwd            { get; init; }
    public int                  IterationCount { get; init; }
    public double               TotalCostUsd   { get; init; }
    public string?              ToolName       { get; init; }
    public JsonElement?         ToolInput      { get; init; }
    public ToolResponsePayload? ToolResponse   { get; init; }
    public string?              Prompt         { get; init; }
    public string?              Message        { get; init; }

    public static readonly JsonSerializerOptions SerializerOptions; // snake_case_lower, WhenWritingNull
}

internal sealed record ToolResponsePayload(string Content, bool IsError);
```

## Registration

`AddAgencyConfiguredHooks` registers the configuration-driven hook pipeline and wires it into `AgentOptions.ConfiguredHooks` via `IPostConfigureOptions<AgentOptions>`.

```csharp
// File: src/Harness/Agency.Harness/Hooks/Configuration/HookServiceCollectionExtensions.cs
using Agency.Harness.Hooks.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agency.Harness.Hooks.Configuration;

public static class HookServiceCollectionExtensions
{
    /// <summary>
    /// Registers the configuration-driven hook pipeline. Reads hook definitions from
    /// <paramref name="config"/> under <paramref name="sectionName"/> (default: "Hooks"),
    /// builds a singleton HookRegistry, and sets AgentOptions.ConfiguredHooks via PostConfigure.
    /// </summary>
    public static IServiceCollection AddAgencyConfiguredHooks(
        this IServiceCollection services,
        IConfiguration config,
        string sectionName = "Hooks");
}
```

**Typical registration in `Program.cs`:**

```csharp
using Agency.Harness.Hooks.Configuration;
using Microsoft.Extensions.DependencyInjection;

builder.Services.AddAgencyConfiguredHooks(builder.Configuration);
```

**Matching `appsettings.json` shape:**

```json
{
  "Hooks": {
    "PreToolUse": [
      {
        "Matcher": "bash|execute_powershell",
        "Hooks": [
          {
            "Type": "Command",
            "Command": "python",
            "Args": ["hooks/audit.py"],
            "Timeout": 10
          }
        ]
      }
    ],
    "PostToolUse": [
      {
        "Hooks": [
          {
            "Type": "Http",
            "Url": "http://localhost:9090/hook",
            "Timeout": 5
          }
        ]
      }
    ]
  }
}
```

## How It Works

The loop implemented internally in `Agent` follows this pattern on every iteration:

```
1. Yield SessionStartedEvent
2. → OnSessionStarted hook (if set)
3. Seed conversation with the user prompt (first iteration only)
4. → OnUserPromptSubmit hook (fires on every ChatAsync call, before the loop)
5. → OnPreIteration hook (fires at the top of every loop iteration, before system prompt rebuild)
6. Build system prompt via SystemPromptBuilder.Build(ctx) [every iteration]
7. Call IChatClient.GetResponseAsync (single batch response)
8. Append assistant message to conversation
9. Yield AssistantTurnEvent
10. → OnAssistantTurn hook (if set)
11. Yield IterationCompletedEvent
12. Evaluate StopCondition(ctx, lastMessage)
    └── true → OnStop hook → yield AgentResultEvent → break
13. Extract FunctionCallContent items from the assistant message
14. For each tool call (in parallel):
    a. → OnPreToolUse hook → Allow / Deny (skip InvokeAsync) / Rewrite (replace input)
    b. InvokeAsync(name, input, ct)
    c. → OnPostToolUse hook (fires even on error)
    d. Yield ToolInvokedEvent
15. → OnPostToolBatch hook (fires after all parallel tool calls settle)
16. Append tool results to conversation as Tool-role messages
17. Repeat from step 5
```

On session disposal (`ChatSession.DisposeAsync`), `Agent.RaiseSessionEndAsync` fires the `OnSessionEnd` hook exactly once.

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
await using var session = new ChatSession(agent, new AgentOptions(), toolCtx);

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

### Hook Composition and Baseline Hooks

`AgentOptions` supports three hook slots that compose in a fixed order via `AgentHooksExtensions.Fold`:

- `BaselineHooks` — populated by the memory pipeline (e.g. retrieval, timer restart) via `PostConfigure`. Always runs first.
- `ConfiguredHooks` — populated by `AddAgencyConfiguredHooks` via `PostConfigure`. Runs after baseline, before user hooks. Represents operator policy expressed in `appsettings.json`.
- `UserHooks` — caller-supplied hooks. Always runs last.

The final effective hooks are resolved as `Fold(BaselineHooks, ConfiguredHooks, UserHooks)`, which is equivalent to `BaselineHooks.Compose(ConfiguredHooks).Compose(UserHooks)`. Advanced callers who need to observe the un-enriched `Context` before baseline hooks run can use `ComposeBefore` directly:

```csharp
using Agency.Harness.Hooks;

// earlyHooks run before the baseline (retrieval) hooks
AgentHooks effective = options.BaselineHooks!.ComposeBefore(earlyHooks);
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
| `ToolHelpTool` | `tool_help` | Added only when progressive discovery is active. Returns a named tool's full description + schema as text, read from the undecorated inner registry. | `name` (required) |

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
- **`Context` is caller-owned, loop-mutates only counters** — `IterationCount`, `TotalCostUsd`, and `TotalUsage` are the only properties mutated by the loop (`internal set`). `Knowledge` and `Memory` are `set` (not `init`) so lifecycle hooks such as `OnPreIteration` can inject retrieved records mid-session — the next iteration's `SystemPromptBuilder.Build` picks them up. `Session` is also `set` so the loop can assign a stable `Id` on first turn. All remaining context properties are `init`-only, keeping session state predictable and easy to snapshot for testing.
- **`SystemPromptBuilder` is a pure function** — The system prompt is rebuilt from `Context` on every iteration so `KnowledgeContext` facts and `MemoryContext` records are always fresh. Being a `static` pure function makes it unit-testable in complete isolation from the agent loop.
- **Two-tier tool disabling** — `ToolRegistry` distinguishes user-initiated disables (`DisableToolByUser`) from system-initiated disables (`DisabledToolBySystem`). `ListAllDefinitions()` hides system-disabled tools entirely; user-disabled tools remain visible with `Enabled = false`, enabling UI toggle flows.
- **Async tool registration** — `ToolRegistry.Register` is synchronous and throws if the tool's `GetDefinitionAsync` does not complete immediately. `RegisterAsync` is the correct path for tools whose schema is fetched asynchronously (e.g. MCP proxy tools resolving remote schemas).
- **`McpProxyTool` is `internal`** — callers never instantiate `McpProxyTool` directly; `McpClientPool.CreateAsync` wraps each discovered `McpClientTool` and exposes the results through the `Tools` property. This keeps the MCP SDK types contained behind the pool boundary.
- **`McpClientPool` is `IAsyncDisposable`** — each `McpClient` holds an open connection to its server process or HTTP endpoint. Disposing the pool closes all connections; callers should use `await using` to ensure cleanup even on cancellation.
- **MCP tools and native tools share one flat namespace** — both register into the same name-keyed `ToolRegistry` dictionary, and once registered the agent loop (`Agent.ListDefinitions` / `InvokeAsync`) cannot tell them apart — dispatch is purely by tool name. The Console host (`Program.cs`) registers native tools (`ExecutePowershellTool`, `ReadFileTool`, `WriteFileTool`, `AgentTool`) first, then loops over `mcpPool.Tools`. Because `Register` is last-write-wins (`_tools[def.Name] = ...`) with no collision guard, an MCP tool whose server-supplied name matches a native tool (e.g. another `read_file`) **silently overwrites** it. Name MCP servers' tools defensively if collisions are possible.
- **`McpProxyTool` keeps only text content** — `InvokeAsync` concatenates `result.Content.OfType<TextContentBlock>()` and discards every other block type. MCP results that are images or embedded resources are silently dropped, so the agent sees an empty string for an image-only response. Fine for text-returning servers; a gap to plan around if a server returns non-text content.
- **MCP wiring is opt-in and fail-fast in the Console host** — the pool is built only when an `"Mcp"` config section lists at least one server (`McpClientOptions` bound from configuration). If a configured server is unreachable, `McpClientPool.CreateAsync` throws and `Program.cs` rethrows, aborting startup rather than degrading silently — tool discovery is a live `ListToolsAsync` round-trip, so an unreachable server means an unknown (not empty) tool set.
- **Progressive discovery is a registry decorator, not an `Agent` change** — `ProgressiveDiscoveryToolRegistry` wraps the inner `IToolRegistry` and overrides only `ListDefinitions()` (for MCP tools: summarize description + withhold schema → `{"type":"object"}` placeholder; for native tools: reveal in full; then append `tool_help`) and `InvokeAsync` (route `tool_help`, delegate the rest). Because `Agent` only ever calls those two members, the entire feature drops in via DI with **zero** changes to the agent loop. The decisive constraint that shaped the design: the model still calls each real tool by its **own name**, so a generic dispatcher was rejected — a dispatcher would mask the real name behind `invoke_tool` and break permission rules, `OnPreToolUse`/audit hooks, the `agent.tool.calls` metric, and `ToolInvokedEvent`, all of which key off the call name.
- **`tool_help` reads the *undecorated* inner registry** — `ProgressiveDiscoveryToolRegistry` constructs `ToolHelpTool` with the inner registry, so `tool_help` returns the **full** schema and description even though the decorator's own `ListDefinitions()` advertises stripped/summarized copies. This is what makes deferral lossless: detail is hidden from the catalog but always recoverable on demand. It is also stateless — the revealed text simply lives in conversation history; there is no activation/registry mutation.
- **Schema withholding is split by origin: MCP tools withheld, native tools revealed in full** — every MCP tool drops to the bare `{"type":"object"}` placeholder (description summarized to one line), so the model *must* `tool_help` before calling it (the system prompt says so); native/internal tools are advertised with their complete schema and description, so they invoke without a round-trip. The split exists because the two populations differ in size and risk: MCP catalogs (e.g. Notion) carry dozens of verbose, operationId-named tools whose schemas dominate the payload — exactly where withholding pays off — whereas the handful of native tools are cheap to keep inline. Crucially, **the full schema (including parameter descriptions) resurfaces via `tool_help`**, so instruction-bearing MCP tools like the memory server — whose `Recall` parameter descriptions carry the must-follow `UserId = "{userId}"` rule — no longer lose that guidance the way the earlier slim-schema scheme did (it stripped parameter descriptions while still advertising the shape, which let the model call without `tool_help` and miss the rule). Because `ToolDefinition` carries no structural provenance, the decorator is told which tools are MCP-originated explicitly: the Console collects `McpClientPool.Tools` names and passes the set to the constructor (`new ProgressiveDiscoveryToolRegistry(inner, mcpToolNames)`) — replacing the earlier `"Vendor | "`-prefix heuristic, which misclassified prefix-less MCP servers (e.g. memory) as native. The self-healing error fold (full schema appended when a call arrives missing required args) backstops a withheld tool the model calls without `tool_help`.
- **Description summarization keeps the first line *and* its `Vendor |` provenance** — `Summarize` returns the first non-empty line verbatim, so `"Notion | Retrieve a page\nError Responses:…"` → `"Notion | Retrieve a page"`. This still trims the verbose body (multi-line error tables etc., recoverable via `tool_help`) but deliberately **retains** the leading `"X | "` prefix. An earlier version stripped that prefix to "lead with the action"; in practice it caused wrong-server tool selection — with operationId-derived MCP names like `API-get-self` that carry no server signal, the prefix is the model's only inline attribution hint, and without it the model answered "what do you know about me" by calling Notion's `API-get-self` (401) instead of the memory tools. The Console `dump-context` additionally groups tools by server using `McpClientPool.ToolNamesByServer`.
- **`ChatSession` is `IAsyncDisposable`** — disposing fires `Agent.RaiseSessionEndAsync` exactly once, which invokes the `OnSessionEnd` hook. This is the correct signal for end-of-session side-effects such as memory distillation. Create one instance per user or logical connection; at most one `SendAsync` call should be in flight at a time. Call `Reset()` to clear conversation history and start a fresh session without creating a new instance.
- **Hooks are interceptors, events are observers** — `IAsyncEnumerable<AgentEvent>` lets callers observe what happened after the fact; `AgentHooks` lets callers block, rewrite, or react *before* the action completes. `OnPreToolUse` is the only hook that can alter agent behaviour (via `Deny`/`Rewrite`); all others are fire-and-forget.
- **`Compose()` uses most-restrictive-wins for `OnPreToolUse`** — when composing two hooks that both have `OnPreToolUse`, they run concurrently via `Task.WhenAll`; the result priority is Deny > Rewrite > Allow. All other delegate slots run sequentially (first then second) so ordering is deterministic.
- **Three-source hook fold: Baseline ▸ Configured ▸ User** — `AgentOptions` now has three hook slots. `BaselineHooks` (memory pipeline) runs first, `ConfiguredHooks` (operator `appsettings.json` policy, set by `AddAgencyConfiguredHooks`) runs second, and `UserHooks` (caller-supplied) runs last. `AgentHooksExtensions.Fold` composes all three in one pass, skipping null slots. This ordering guarantees that retrieval runs before policy enforcement, and policy runs before application-layer observation (Spec §14.5).
- **`SessionContext` provides stable session identity** — the agent loop assigns a `Guid`-based `Id` to `Context.Session` on the very first `RunAsync` call and reuses it for all subsequent turns. Memory records and distillation triggers use this id to scope data to the correct session.
- **`UserSpecificContext.Id` is the memory partition key** — `AgentOptions.UserId` flows into `UserSpecificContext.Id` via `ChatSession`, ensuring retrieved and distilled memory records are scoped to the correct user even when multiple sessions share an infrastructure.
- **`HooksOptions` extends `Dictionary<HookEventName, HookMatcherGroupConfig[]>`** — `IConfiguration.Bind` cannot populate a plain class property of dictionary type when the source JSON is a flat object with dynamic keys. By making `HooksOptions` itself a `Dictionary` subtype, the binder maps each top-level JSON key directly to a dictionary entry without needing an intermediate property wrapper. The `Hooks` property is a self-referential convenience accessor that returns `this`.
- **Fail-open principle for config-driven hooks** — a hook handler that exits with a non-zero code other than `2` (e.g. `1` for a non-blocking error, or `-1` for timeout) is treated as `Allow`. Only exit code `2` or a JSON body containing `{ "hookSpecificOutput": { "permissionDecision": "deny" } }` causes a blocking `Deny`. This means transient script failures and timeouts never silently block tool execution; operators must be explicit to deny.
- **Compile-once `HookRegistry` pattern** — `HookRegistry` resolves all `HookMatcher` instances and creates all `IHookHandler` instances in its constructor. At invocation time only `IsMatch(toolName)` calls are made against the pre-compiled matcher set. This avoids repeated regex compilation on hot paths and makes the registry safe to register as a DI singleton. `ToAgentHooks()` converts the registry to a standard `AgentHooks` instance once at startup; the resulting delegates close over the pre-compiled state.
