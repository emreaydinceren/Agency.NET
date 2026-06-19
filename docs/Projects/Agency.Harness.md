# Agency.Harness

#agent #loop #tools #events #orchestration #mcp #skills #permissions #hooks #observability

## What It Is

`Agency.Harness` is the autonomous agent loop library that drives a think → act → observe cycle until a `StopCondition` fires. It accepts an `IChatClient` (via `Microsoft.Extensions.AI`), a `Context` object aggregating the user query, conversation history, tools, skills, memory, and grounding data, and yields a stream of typed `AgentEvent` records so callers can react to each stage without polling. On top of the bare loop it layers four cross-cutting subsystems: an MCP tool pool (`McpClientPool`) that exposes Model Context Protocol server tools as `ITool` instances, a **progressive tool-discovery** decorator that withholds verbose schemas until requested, a **permission model** that parks tool calls for user approval (park/resume), a **skills** subsystem implementing SKILL.md progressive disclosure, and a **config-driven hooks** pipeline that runs operator-defined command/HTTP handlers at lifecycle points.

**Namespace:** `Agency.Harness`

## API Surface

### Core Loop

#### `Agent`

```csharp
// File: src/Harness/Agency.Harness/Agent.cs
using Agency.Harness.Contexts;
using Agency.Harness.Hooks;
using Agency.Harness.Permissions;
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
        StopCondition? stopWhen = null,             // default: Any(NoToolCalls, StepCountIs(20))
        AgentHooks? hooks = null,                   // default: AgentHooks.None
        IPermissionEvaluator? permissions = null,   // when set, tool calls are evaluated; unresolved calls park the turn
        ILogger<Agent>? logger = null,
        TimeProvider? timeProvider = null,          // default: TimeProvider.System (tests inject a pinned clock)
        bool logToolPayloads = false);              // verbose, opt-in tool-payload logging

    public string Model      { get; }
    public string ClientType { get; }

    /// <summary>Fires the OnSessionEnd hook for the given context, if one is set. Called by ChatSession.DisposeAsync.</summary>
    public Task RaiseSessionEndAsync(Context ctx, CancellationToken ct = default);

    /// <summary>Creates a Context pre-populated with temporal context, the initial prompt, and an optional skill catalog.</summary>
    public static Context CreateContext(
        string initialPrompt,
        ToolContext? tools = null,
        EnvironmentalContext? environment = null,
        UserSpecificContext? user = null,
        TimeProvider? timeProvider = null,
        SkillContext? skills = null);

    /// <summary>
    /// Executes one user turn. Fires OnUserPromptSubmit before entering the loop, clears active-skill
    /// state, and applies TurnTimeoutSeconds when configured.
    /// </summary>
    public IAsyncEnumerable<AgentEvent> ChatAsync(
        string userMessage,
        Context ctx,
        AgentOptions? options = null,
        CancellationToken ct = default);
}
```

#### `ChatSession`

Higher-level stateful wrapper — the preferred surface for REPL hosts and HTTP endpoints. Implements `IAsyncDisposable`; dispose fires the `OnSessionEnd` hook once. It also drives the permission park/resume protocol.

```csharp
// File: src/Harness/Agency.Harness/ChatSession.cs
using Agency.Harness.Contexts;
using Agency.Harness.Permissions;

namespace Agency.Harness;

public sealed class ChatSession : IAsyncDisposable
{
    public ChatSession(
        Agent agent,
        AgentOptions options,
        ToolContext? toolContext = null,
        UserSpecificContext? user = null,
        SkillContext? skills = null);

    public LlmTokenUsage TotalUsage   { get; }
    public decimal       TotalCostUsd { get; }
    public int           TurnCount    { get; }
    public bool          IsStarted    { get; }

    /// <summary>Switches the agent used for subsequent turns. Conversation history is preserved.</summary>
    public void SetAgent(Agent agent);

    /// <summary>
    /// Sends a message and streams back AgentEvents. Context is created lazily on the first call.
    /// If a turn is parked awaiting permission and a new message arrives, all pending calls are
    /// implicitly denied (abandonment) before the new message is processed.
    /// </summary>
    public IAsyncEnumerable<AgentEvent> SendAsync(string userMessage, CancellationToken ct = default);

    /// <summary>
    /// Resumes a turn parked with AwaitingPermission. Requires exactly one PermissionResponse per
    /// pending PermissionRequestedEvent.RequestId; streams the remainder of the turn.
    /// </summary>
    public IAsyncEnumerable<AgentEvent> ResumeWithPermissionsAsync(
        IReadOnlyList<PermissionResponse> responses,
        CancellationToken ct = default);

    /// <summary>Resets the session by clearing conversation history and turn count.</summary>
    public void Reset();

    /// <summary>Fires the agent's OnSessionEnd hook (once) then marks the session disposed. Safe to call multiple times.</summary>
    public ValueTask DisposeAsync();
}
```

#### Agent Events

```csharp
// File: src/Harness/Agency.Harness/AgentEvents.cs
using System.Text.Json;
using Agency.Harness.Permissions;

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

/// <summary>Emitted once per tool call that needs user permission; the turn then ends with AwaitingPermission.</summary>
public sealed record PermissionRequestedEvent(
    Guid                    RequestId,
    string                  ToolName,
    JsonElement             Input,
    string?                 KeyValue,
    string                  ProposedRule,
    PermissionRequestSource Source,
    string?                 Reason) : AgentEvent;

public enum PermissionRequestSource { UnresolvedRule, Hook }

/// <summary>Terminal event — always the last event emitted by the agent loop (or turn).</summary>
public sealed record AgentResultEvent(
    AgentResultStatus Status,
    string?           FinalText,
    LlmTokenUsage     TotalUsage,
    decimal           TotalCostUsd) : AgentEvent;

public enum AgentResultStatus { Success, MaxStepsReached, BudgetExceeded, Error, AwaitingPermission }

public sealed record LlmTokenUsage(long InputTokens, long OutputTokens)
{
    public long TotalTokens => InputTokens + OutputTokens;
}
```

#### Stop Conditions

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

#### `AgentOptions`

```csharp
// File: src/Harness/Agency.Harness/AgentOptions.cs
using Agency.Harness.Hooks;

namespace Agency.Harness;

public sealed class AgentOptions
{
    public string             DefaultClientName    { get; set; }
    public string?            DefaultModel         { get; set; }
    public int?               TurnTimeoutSeconds   { get; set; }
    public LlmClientOptions[] LLmClients           { get; set; } = [];
    public int?               ContextWindowSize    { get; set; }
    public bool               ProgressiveDiscovery { get; set; }  // opt-in schema withholding (host wraps registry)
    public bool               LogToolPayloads      { get; set; }  // opt-in verbose tool-payload logging
    public string?            UserId               { get; set; }  // → UserSpecificContext.Id (memory partition key)
    public AgentHooks?        BaselineHooks        { get; set; }  // memory pipeline; PostConfigure; runs first
    public AgentHooks?        ConfiguredHooks      { get; set; }  // operator appsettings.json policy; runs middle
    public AgentHooks?        UserHooks            { get; set; }  // caller-supplied; runs last
}
```

#### `IConversationManager`

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

#### `Models`

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
    public Task<IEnumerable<IGrouping<LlmClientOptions, Model>>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates an IChatClient for the named provider; returns the client and its display type.</summary>
    public (IChatClient Client, string ClientType) CreateChatClient(string clientName);
}
```

#### `SystemPromptBuilder`

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

`Build` assembles: persona + ReAct reasoning instruction; (when `ctx.Tools.Registry is IProgressiveDiscovery`) a withheld-schema directive telling the model to call `tool_help(name)` before invoking a tool advertised with only a `{"type":"object"}` schema; a `## Skills` catalog listing only model-invocable skills; knowledge facts; long/short-term memory and retrieved records; temporal, environmental (incl. context-window budget), and user grounding.

### `Context` and Sub-Contexts

```csharp
// File: src/Harness/Agency.Harness/Contexts/Context.cs
namespace Agency.Harness.Contexts;

public sealed record Context
{
    public required QueryContext    Query        { get; init; }
    public KnowledgeContext         Knowledge    { get; set; }  = KnowledgeContext.Empty;   // settable: hooks may refresh facts
    public MemoryContext            Memory       { get; set; }  = MemoryContext.Empty;       // settable: retrieval injects records
    public ToolContext              Tools        { get; init; } = ToolContext.Empty;
    public SkillContext             Skills       { get; init; } = SkillContext.Empty;        // live skill catalog for this session
    public FocusContext             Focus        { get; set; }  = FocusContext.Empty;
    public SessionContext           Session      { get; set; }  = SessionContext.Empty;      // set once by the loop; stable for life
    public UserSpecificContext      User         { get; init; } = UserSpecificContext.Empty;
    public TemporalContext          Temporal     { get; init; } = TemporalContext.Empty;
    public EnvironmentalContext     Environment  { get; init; } = EnvironmentalContext.Empty;
    public IConversationManager     Conversation { get; init; }  // default: InMemoryConversationManager
    public int                      IterationCount        { get; internal set; }  // loop-owned
    public decimal                  TotalCostUsd          { get; internal set; }  // loop-owned
    public LlmTokenUsage            TotalUsage            { get; internal set; }  // loop-owned
    public DateTimeOffset?          MemoryLastRetrievedAt { get; set; }           // written by retrieval engine
    // internal: PendingToolBatch (park state, serialization target), ActiveSkillState (skill pre-approval)
}
```

| Sub-Context | Key Members |
|---|---|
| `QueryContext` | `Prompt` — the initial user message |
| `KnowledgeContext` | `Facts: IReadOnlyList<string>`, `Records: IReadOnlyList<MemoryRecord>` — re-injected into the system prompt each iteration |
| `MemoryContext` | `ShortTermMemory`, `LongTermMemory`, `Records` — injected into the system prompt |
| `ToolContext` | `Registry: IToolRegistry` |
| `SkillContext` (public record; members `internal`) | `Catalog: ISkillCatalog`, `List()`, `Find(name)` — wraps a live catalog reference |
| `FocusContext` | `Title?`, `Domain?`, `Tags` — narrows retrieval toward a task domain |
| `SessionContext` | `Id: string?` — stable session identifier; assigned once by the loop |
| `UserSpecificContext` | `Id: string?` (memory partition key), `Name: string?` |
| `TemporalContext` | `CurrentDateUtc: DateTimeOffset?` |
| `EnvironmentalContext` | `OperatingSystem: string?`, `ContextWindowSize: int?` |

### Hooks

```csharp
// File: src/Harness/Agency.Harness/Hooks/PreToolUseDecision.cs
using System.Text.Json;

namespace Agency.Harness.Hooks;

public abstract record PreToolUseDecision
{
    public sealed record Allow : PreToolUseDecision;
    public sealed record Deny(string Reason) : PreToolUseDecision;
    public sealed record Rewrite(JsonElement NewInput) : PreToolUseDecision;
    /// <summary>Flag the call for user confirmation. Aggregation priority: Deny &gt; Ask &gt; Rewrite &gt; Allow.</summary>
    public sealed record Ask(string? Reason) : PreToolUseDecision;
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
    public Func<SessionStartedHookContext, CancellationToken, Task>?                 OnSessionStarted   { get; init; }
    public Func<Context, CancellationToken, Task>?                                   OnUserPromptSubmit { get; init; }
    public Func<Context, CancellationToken, Task>?                                   OnPreIteration     { get; init; }
    public Func<PreToolUseHookContext, CancellationToken, Task<PreToolUseDecision>>? OnPreToolUse       { get; init; }
    public Func<PostToolUseHookContext, CancellationToken, Task>?                    OnPostToolUse      { get; init; }
    public Func<IReadOnlyList<ToolInvokedEvent>, Context, CancellationToken, Task>?  OnPostToolBatch    { get; init; }
    public Func<AssistantTurnHookContext, CancellationToken, Task>?                  OnAssistantTurn    { get; init; }
    public Func<StopHookContext, CancellationToken, Task>?                           OnStop             { get; init; }
    public Func<SessionEndedHookContext, CancellationToken, Task>?                   OnSessionEnd       { get; init; }

    public static AgentHooks None { get; }
}
```

```csharp
// File: src/Harness/Agency.Harness/Hooks/AgentHooksExtensions.cs
namespace Agency.Harness.Hooks;

public static class AgentHooksExtensions
{
    /// <summary>second runs after first. For OnPreToolUse the most restrictive decision wins (Deny &gt; Ask &gt; Rewrite &gt; Allow).</summary>
    public static AgentHooks Compose(this AgentHooks first, AgentHooks second);

    /// <summary>first runs before self (the baseline). Escape hatch for observing the un-enriched Context.</summary>
    public static AgentHooks ComposeBefore(this AgentHooks self, AgentHooks first);

    /// <summary>Folds baseline ▸ configured ▸ user into one composed AgentHooks; null sources skipped.</summary>
    internal static AgentHooks? Fold(AgentHooks? baseline, AgentHooks? configured, AgentHooks? user);
}
```

**Built-in hooks:**

- `BlockListHooks.Dangerous` — static `AgentHooks` with `OnPreToolUse` that denies shell commands containing patterns like `rm -rf`, `DROP TABLE`, `format c:`, `del /f /s`.
- `AuditHooks.ForLogger(ILogger logger)` — returns `AgentHooks` with `OnPreToolUse`/`OnPostToolUse` that log at `Information` level; `OnPreToolUse` always returns `Allow`.

### Permissions

```csharp
// File: src/Harness/Agency.Harness/Permissions/IPermissionEvaluator.cs
using System.Text.Json;

namespace Agency.Harness.Permissions;

/// <summary>Evaluates tool calls against configured allow/deny rules and session grants.</summary>
public interface IPermissionEvaluator
{
    /// <summary>Pure decision — never blocks, never renders, never talks to the user.</summary>
    PermissionDecision Evaluate(string toolName, JsonElement input);

    /// <summary>Records an "always" answer: adds a session grant and appends to the local rules file.</summary>
    Task RecordAlwaysAsync(string proposedRule, bool deny, CancellationToken ct);
}
```

```csharp
// File: src/Harness/Agency.Harness/Permissions/PermissionDecision.cs
namespace Agency.Harness.Permissions;

public abstract record PermissionDecision
{
    public sealed record Allow : PermissionDecision;
    public sealed record Deny(string Reason) : PermissionDecision;
    /// <param name="KeyValue">Extracted key field (command/path) for concise display; null when none.</param>
    /// <param name="ProposedRule">Rule string persisted if the user answers "always".</param>
    public sealed record Ask(string? KeyValue, string ProposedRule) : PermissionDecision;
    public static PermissionDecision Allowed { get; }
}
```

```csharp
// File: src/Harness/Agency.Harness/Permissions/PermissionResponse.cs
namespace Agency.Harness.Permissions;

/// <summary>The host's answer to a single PermissionRequestedEvent.</summary>
public sealed record PermissionResponse(Guid RequestId, PermissionResponseKind Kind, string? Message = null);

public enum PermissionResponseKind { AllowOnce, AllowAlways, DenyOnce, DenyAlways }
```

Internal supporting types: `PermissionEvaluator` (the `IPermissionEvaluator` impl), `PermissionRule` (parses `Tool` / `Tool(glob*)` rule strings into anchored case-insensitive regexes, `\`→`/` normalized, 250 ms match timeout), `PermissionsOptions` (bound from the `Permissions` config section: `Enabled`, `Allow[]`, `Deny[]`, `OnUnresolved` ∈ {`Ask`, `Deny`}, `ToolInputKeys`, `LocalRulesPath`), `PermissionsOptionsValidator` (fail-fast rule parse at startup), and `PermissionsFileStore` (tolerant load + retry/backoff append of `permissions.local.json`).

### Skills

The `Agency.Harness.Skills` subsystem implements SKILL.md progressive disclosure. Its types are `internal` (the contract is consumed by the Console host and tests via `InternalsVisibleTo`); the only externally visible surface is `Contexts.SkillContext` (a public record whose members are internal) and the `skill` agent tool.

```csharp
// File: src/Harness/Agency.Harness/Skills/ISkillCatalog.cs
namespace Agency.Harness.Skills;

internal interface ISkillCatalog
{
    IReadOnlyList<Skill> List();
    Skill? Find(string name);   // canonical name = directory name; null when absent
}
```

```csharp
// File: src/Harness/Agency.Harness/Skills/ISkillShellRunner.cs
namespace Agency.Harness.Skills;

internal interface ISkillShellRunner
{
    Task<string> RunAsync(string command, CancellationToken ct = default);
}
// PowerShellSkillShellRunner : ISkillShellRunner — executes via a PowerShell runspace.
```

```csharp
// File: src/Harness/Agency.Harness/Skills/Skill.cs
namespace Agency.Harness.Skills;

internal sealed record Skill
{
    public required string Name { get; init; }          // canonical invocation key = directory name
    public required string Description { get; init; }
    public string? WhenToUse { get; init; }
    public required string Body { get; init; }
    public required string SkillDir { get; init; }
    public bool DisableModelInvocation { get; init; }   // excluded from model catalog + refused by SkillTool
    public bool UserInvocable { get; init; } = true;    // appears in the Console "/" menu
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? ArgumentHint { get; init; }
    public string? Shell { get; init; }
    public IReadOnlyList<string> AllowedTools { get; init; } = [];  // pre-approved while this skill is active
    public string? Context { get; init; }               // "fork" → subagent delegation; null → inline body return
    public string? Agent { get; init; }                 // agent-type hint when Context == "fork"
}
```

| Internal type | Role |
|---|---|
| `SkillCatalog` | Immutable in-memory `ISkillCatalog`; `Empty` singleton; name lookup is `OrdinalIgnoreCase`. |
| `ReloadableSkillCatalog` | `ISkillCatalog` wrapper whose inner catalog can be atomically swapped via `Reload()` (volatile field). A failed re-scan keeps the prior catalog intact. |
| `SkillLoader` | Scans ordered roots; first-occurrence-wins precedence; each root holds subdirectories with a `SKILL.md`. Missing roots / malformed files are skipped. |
| `SkillParser` | Parses shallow YAML frontmatter (scalars, bools, three list syntaxes) + markdown body; no third-party YAML dependency. |
| `SkillRenderer` | Pure body renderer: substitutes `$ARGUMENTS`, `$N`, `$name`, `${CLAUDE_SKILL_DIR}`, `${CLAUDE_SESSION_ID}`; separate `ExpandShellAsync` step expands `` !`cmd` `` and fenced ```` ```! ```` directives (single-pass, never re-scanned). |
| `SkillWatcher : IDisposable` | `FileSystemWatcher` over each root (filter `SKILL.md`), debounced (default 300 ms) into one `Reload()` callback. |
| `ActiveSkillState` | Tracks the tools pre-approved by the most recently invoked skill (`Set`/`Clear`/`IsAllowed`); cleared at the start of each user turn. |
| `SkillForkRunner` (delegate) | `Task<string>(string prompt, string? agentType, CancellationToken ct)` — spawns a subagent for `context: fork` skills. |

### Progressive Tool Discovery

```csharp
// File: src/Harness/Agency.Harness/Tools/IProgressiveDiscovery.cs
namespace Agency.Harness.Tools;

/// <summary>Marker: lets callers (e.g. SystemPromptBuilder) detect progressive disclosure is active.</summary>
public interface IProgressiveDiscovery { }
```

```csharp
// File: src/Harness/Agency.Harness/Tools/ProgressiveDiscoveryToolRegistry.cs
namespace Agency.Harness.Tools;

/// <summary>
/// Decorator over IToolRegistry. MCP tools (names supplied at construction) are advertised with their
/// schema withheld behind {"type":"object"} and their description summarized to one line + an inline
/// tool_help directive; native/internal tools are revealed in full. ListDefinitions() also appends
/// tool_help. InvokeAsync routes "tool_help" to the help tool, folds the full schema into an error
/// when a call arrives missing required arguments (self-healing), and delegates everything else to inner.
/// All other IToolRegistry members delegate to inner.
/// </summary>
public sealed class ProgressiveDiscoveryToolRegistry : IToolRegistry, IProgressiveDiscovery
{
    public ProgressiveDiscoveryToolRegistry(IToolRegistry inner, IReadOnlySet<string> mcpToolNames);
}
// ToolHelpTool (internal) — tool_help(name) → the named tool's FULL description + indented schema,
//   read from the UNDECORATED inner registry. The escape hatch for deferred detail.
```

The decorator is told which tools are MCP-originated via the constructor's `mcpToolNames` set (the Console collects these from `McpClientPool.ToolNamesByServer`). Description summarization keeps the first non-empty line including any leading `Vendor |` provenance prefix.

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

    public void Register(ITool tool);                                   // throws if the tool needs async definition resolution
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
    public Dictionary<string, string?>? EnvironmentVariables { get; set; }  // Stdio only
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
    /// <summary>All tools discovered from every connected MCP server (flat across servers).</summary>
    public IReadOnlyList<ITool> Tools { get; }

    /// <summary>Tool names discovered from each MCP server, keyed by server name in configured order.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ToolNamesByServer { get; }

    /// <summary>Connects to each server, lists its tools, and returns an initialized pool.</summary>
    public static Task<McpClientPool> CreateAsync(McpClientOptions options, CancellationToken ct = default);

    public ValueTask DisposeAsync();
}
```

### Configuration-Driven Hooks

Types in `Agency.Harness.Hooks.Configuration` support operator-defined hooks loaded from `appsettings.json` (or any `IConfiguration` source). They are wired into the three-source fold as the middle `ConfiguredHooks` layer. All types are `internal`; the only public surface is the registration extension method (below).

```csharp
// File: src/Harness/Agency.Harness/Hooks/Configuration/HookEventName.cs
namespace Agency.Harness.Hooks.Configuration;

internal enum HookEventName
{
    SessionStart, UserPromptSubmit, PreIteration, PreToolUse,
    PostToolUse, PostToolBatch, AssistantTurn, Stop, SessionEnd
}
```

```csharp
// File: src/Harness/Agency.Harness/Hooks/Configuration/HookHandlerKind.cs
namespace Agency.Harness.Hooks.Configuration;

internal enum HookHandlerKind
{
    Command,   // spawn an external process; payload delivered via stdin
    Http,      // POST to a URL; payload delivered as JSON body
    McpTool, Prompt, Agent   // reserved — V2
}
```

```csharp
// File: src/Harness/Agency.Harness/Hooks/Configuration/HookHandlerConfig.cs
namespace Agency.Harness.Hooks.Configuration;

internal sealed class HookHandlerConfig
{
    public HookHandlerKind            Type    { get; set; } = HookHandlerKind.Command;
    public string?                    Command { get; set; }           // Command only
    public string[]                   Args    { get; set; } = [];     // Command only
    public int?                       Timeout { get; set; }           // seconds; default 30
    public string?                    Url     { get; set; }           // Http only
    public Dictionary<string, string>? Headers { get; set; }          // Http only
}
```

```csharp
// File: src/Harness/Agency.Harness/Hooks/Configuration/HookMatcherGroupConfig.cs
namespace Agency.Harness.Hooks.Configuration;

internal sealed class HookMatcherGroupConfig
{
    /// <summary>Tool-name filter. Null / "*" matches all; "a|b" is an exact set; anything else is a Regex (250 ms timeout).</summary>
    public string?             Matcher { get; set; }
    public HookHandlerConfig[] Hooks   { get; set; } = [];
}
```

```csharp
// File: src/Harness/Agency.Harness/Hooks/Configuration/HooksOptions.cs
namespace Agency.Harness.Hooks.Configuration;

/// <summary>Root object. Subclasses Dictionary so IConfiguration.Bind populates it from a JSON object keyed by HookEventName.</summary>
internal sealed class HooksOptions : Dictionary<HookEventName, HookMatcherGroupConfig[]>
{
    public Dictionary<HookEventName, HookMatcherGroupConfig[]> Hooks => this;
}
```

```csharp
// File: src/Harness/Agency.Harness/Hooks/Configuration/Handlers/IHookHandler.cs
using Agency.Harness.Hooks.Configuration;

namespace Agency.Harness.Hooks.Configuration.Handlers;

internal interface IHookHandler
{
    Task<HookHandlerOutput> InvokeAsync(HookPayload payload, CancellationToken ct);
}

internal interface IHookHandlerFactory
{
    IHookHandler Create(HookHandlerConfig config);
}
```

```csharp
// File: src/Harness/Agency.Harness/Hooks/Configuration/Handlers/HookHandlerOutput.cs
namespace Agency.Harness.Hooks.Configuration.Handlers;

internal sealed record HookHandlerOutput(int ExitCode, JsonElement? Json, string? RawStdout, string? RawStderr);

internal static class HookExitCodes
{
    internal const int Ok               = 0;
    internal const int NonBlockingError = 1;
    internal const int BlockingDeny     = 2;   // causes PreToolUse → Deny
    internal const int Timeout          = -1;
}
```

Concrete handlers (internal): `CommandHookHandler` (spawns a process, writes the JSON payload to stdin, drains stdout/stderr, kills the tree on timeout) and `HttpHookHandler` (POSTs the payload, mapping a non-success status to a non-blocking error). `HookHandlerFactory` selects the concrete handler from `HookHandlerKind`, resolving `HttpClient` from `IHttpClientFactory`.

```csharp
// File: src/Harness/Agency.Harness/Hooks/Configuration/HookRegistry.cs
using Agency.Harness.Hooks;
using Agency.Harness.Hooks.Configuration.Handlers;

namespace Agency.Harness.Hooks.Configuration;

internal sealed class HookRegistry
{
    internal static readonly HookRegistry Empty;  // no-op registry; used when config hooks are absent

    internal HookRegistry(HooksOptions options, IHookHandlerFactory factory, ILogger? logger);

    /// <summary>Converts the compiled registry into an AgentHooks instance for AgentOptions.ConfiguredHooks.</summary>
    internal AgentHooks ToAgentHooks();

    /// <summary>Aggregates handler outputs into one PreToolUseDecision: BlockingDeny &gt; Ask &gt; Rewrite &gt; Allow (fail-open).</summary>
    internal static PreToolUseDecision AggregateDecision(IEnumerable<HookHandlerOutput> outputs, JsonElement _);
}
```

`HookMatcher` (internal) compiles a `Matcher` string into one of three modes (`MatchAll`, `ExactSet`, `Regex`) via `HookMatcher.Create` and exposes `IsMatch`. `HookPayload` (internal record, snake_case_lower serialization, null fields omitted) carries `SessionId`, `HookEventName`, `Cwd`, `IterationCount`, `TotalCostUsd`, `ToolName`, `ToolInput`, `ToolResponse`, `Prompt`, `Message`; `HookPayloadFactory` builds it per event.

## Registration

Two public DI extension methods register the optional cross-cutting subsystems. (Permissions, skills, MCP, and progressive-discovery wiring on the `Agent`/`ChatSession` side are done by the host — typically [[Agency.Harness.Console]].)

```csharp
// File: src/Harness/Agency.Harness/Permissions/PermissionServiceCollectionExtensions.cs
// File: src/Harness/Agency.Harness/Hooks/Configuration/HookServiceCollectionExtensions.cs
using Agency.Harness.Hooks.Configuration;
using Agency.Harness.Permissions;
using Microsoft.Extensions.DependencyInjection;

builder.Services.AddAgencyPermissions(builder.Configuration);       // section "Permissions"
builder.Services.AddAgencyConfiguredHooks(builder.Configuration);   // section "Hooks"
```

- `AddAgencyPermissions(services, config, sectionName = "Permissions")` binds `PermissionsOptions` and registers `IPermissionEvaluator` → `PermissionEvaluator` as a **singleton**. Malformed rules fail fast (`InvalidOperationException`) at first resolution via `PermissionsOptionsValidator`.
- `AddAgencyConfiguredHooks(services, config, sectionName = "Hooks")` binds `HooksOptions`, calls `AddHttpClient()`, registers `IHookHandlerFactory` → `HookHandlerFactory` and a singleton `HookRegistry`, and sets `AgentOptions.ConfiguredHooks` via an `IPostConfigureOptions<AgentOptions>`. Unknown event names fail fast (`HooksOptionsValidator`).

**Matching `appsettings.json` shape (hooks):**

```json
{
  "Hooks": {
    "PreToolUse": [
      {
        "Matcher": "bash|execute_powershell",
        "Hooks": [ { "Type": "Command", "Command": "python", "Args": ["hooks/audit.py"], "Timeout": 10 } ]
      }
    ],
    "PostToolUse": [
      { "Hooks": [ { "Type": "Http", "Url": "http://localhost:9090/hook", "Timeout": 5 } ] }
    ]
  }
}
```

## How It Works

The loop implemented internally in `Agent` follows this pattern:

```
1. Yield SessionStartedEvent → OnSessionStarted hook
2. Seed conversation with the user prompt (first iteration only); clear ActiveSkillState; OnUserPromptSubmit hook
3. Each iteration:
   a. OnPreIteration hook
   b. Build system prompt via SystemPromptBuilder.Build(ctx)
   c. Call IChatClient.GetResponseAsync; append assistant message; yield AssistantTurnEvent → OnAssistantTurn hook
   d. Yield IterationCompletedEvent
   e. If finish_reason == Length → emit Error AgentResultEvent and stop
   f. Evaluate StopCondition → OnStop hook → AgentResultEvent → break
   g. For each FunctionCallContent (in parallel):
      - OnPreToolUse → Allow / Deny (block) / Rewrite (replace input) / Ask (flag for permission)
      - Permission gate (post-rewrite): rule Deny > active-skill pre-approval > hook Ask > rule Allow > unresolved
        · pended calls accumulate; non-pended execute and OnPostToolUse fires
      - if the "skill" meta-tool succeeded, record its allowed-tools into ActiveSkillState
   h. If any calls pended → yield completed siblings, one PermissionRequestedEvent per pended call,
      park the batch (Context.PendingToolBatch) and emit AwaitingPermission; the turn ends here.
   i. Otherwise OnPostToolBatch hook; append tool-result messages; repeat
```

**Permission park/resume.** When an unresolved or hook-Ask call pends, the loop stores a `PendingToolBatch` on the `Context` (completed siblings' results + the calls awaiting approval), emits `PermissionRequestedEvent`s, and ends the turn with `AwaitingPermission`. The host answers via `ChatSession.ResumeWithPermissionsAsync` with one `PermissionResponse` per `RequestId`; the agent records any `*Always` grants, executes or `[Blocked]`-denies the pended calls, fires `OnPostToolBatch` over the full reconstructed batch, appends all results, and continues the loop. Sending a new message while parked implicitly denies all pending calls (abandonment).

On session disposal (`ChatSession.DisposeAsync`), `Agent.RaiseSessionEndAsync` fires `OnSessionEnd` exactly once.

### Practical Usage

```csharp
using Agency.Harness;
using Agency.Harness.Contexts;
using Agency.Harness.Permissions;
using Agency.Harness.Tools;
using Microsoft.Extensions.AI;

// Build a tool registry
var registry = new ToolRegistry([new ReadFileTool(), new ExecutePowershellTool()]);
var toolCtx  = new ToolContext { Registry = registry };

// Create the agent (IChatClient from Microsoft.Extensions.AI), optionally with a permission evaluator
IChatClient chatClient = /* e.g. claudeClient.CreateChatClient() */;
var agent = new Agent(chatClient, "claude-opus-4-8", clientType: "Claude", permissions: evaluator);

await using var session = new ChatSession(agent, new AgentOptions(), toolCtx);

await foreach (AgentEvent evt in session.SendAsync("List files in /tmp", ct))
{
    switch (evt)
    {
        case ToolInvokedEvent t:
            Console.WriteLine($"[tool: {t.ToolName}]");
            break;
        case PermissionRequestedEvent p:
            // Ask the user, then resume:
            var resp = new PermissionResponse(p.RequestId, PermissionResponseKind.AllowOnce);
            await foreach (var more in session.ResumeWithPermissionsAsync([resp], ct)) { /* … */ }
            break;
        case AgentResultEvent r:
            Console.WriteLine($"Done ({r.Status}). Tokens: {r.TotalUsage.TotalTokens}");
            break;
    }
}
```

### Hook Composition and Baseline Hooks

`AgentOptions` supports three hook slots that compose in a fixed order via `AgentHooksExtensions.Fold(BaselineHooks, ConfiguredHooks, UserHooks)`:

- `BaselineHooks` — memory pipeline (retrieval, timer restart) via `PostConfigure`. Runs first.
- `ConfiguredHooks` — operator `appsettings.json` policy, set by `AddAgencyConfiguredHooks`. Runs middle.
- `UserHooks` — caller-supplied. Runs last.

```csharp
using Agency.Harness.Hooks;

// earlyHooks run before the baseline (retrieval) hooks
AgentHooks effective = options.BaselineHooks!.ComposeBefore(earlyHooks);
```

### Connecting MCP Servers

```csharp
using Agency.Harness.Tools;

await using var pool = await McpClientPool.CreateAsync(new McpClientOptions
{
    Servers =
    [
        new McpServerConfig { Name = "filesystem", Transport = McpTransportKind.Stdio,
            Command = "npx", Arguments = ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"] },
        new McpServerConfig { Name = "github", Transport = McpTransportKind.Http,
            Url = "http://localhost:3001" },
    ]
}, ct);

var registry = new ToolRegistry(pool.Tools);
// Opt into progressive discovery, telling the decorator which tools are MCP-originated:
var mcpNames = pool.ToolNamesByServer.SelectMany(kv => kv.Value).ToHashSet();
var progressive = new ProgressiveDiscoveryToolRegistry(registry, mcpNames);
var toolCtx = new ToolContext { Registry = progressive };
```

## Agent Tools

| Tool Name (exact string) | Description shown to LLM | Key Parameters |
|---|---|---|
| `subagent_tool` (`AgentTool`) | Delegates a focused task to a specialized subagent with its own `ToolRegistry`. Sub-agents auto-deny any permission requests (cannot prompt). | `prompt` (required), `clientName`, `model` |
| `execute_powershell` (`ExecutePowershellTool`) | Executes a PowerShell command in a fresh runspace; formats multi-object output as Markdown tables. Description carries OS/cwd/path-separator (override-able for cache replay); accepts a single string under any key (Postel's law). | `command` (required) |
| `read_file` (`ReadFileTool`) | Reads and returns the contents of a file at a given path. | `path` (required) |
| `write_file` (`WriteFileTool`) | Writes content to a file at a given path. | `path`, `content` (required) |
| `skill` (`SkillTool`, internal) | Invokes a skill by name; loads its rendered instructions into the conversation. On success records the skill's `allowed-tools` into `ActiveSkillState`; `context: fork` skills delegate to a subagent runner when wired. Refuses skills with `disable-model-invocation`. | `name` (required), `arguments` |
| `tool_help` (`ToolHelpTool`, internal) | Added only when progressive discovery is active. Reveals a named tool's full description + schema, read from the undecorated inner registry. | `name` (required) |
| *(per-tool name from server)* (`McpProxyTool`, internal) | Adapts a discovered `McpClientTool` to `ITool`; one instance per tool per MCP server. Name and schema come from the server at connection time. | Varies by MCP server |

## Observability

### `Agent`

| Signal | Name | Tags |
|---|---|---|
| `ActivitySource` | `Agency.Harness.Agent` | — |
| `Meter` | `Agency.Harness.Agent` | — |
| Counter | `agent.turns` | `agent.model`, `agent.client_type` |
| Counter | `agent.errors` | same (+ `agent.error` = `truncated` on length-finish) |
| Counter | `agent.tool.calls` | same + `agent.tool.name`, `agent.tool.error` |
| Counter | `agent.tokens` | `agent.model`, `agent.client_type`, `agent.token.type` |
| Histogram | `agent.turn.duration` (ms) | `agent.model`, `agent.client_type` |

### `Models`

| Signal | Name | Tags |
|---|---|---|
| `ActivitySource` | `Agency.Harness.Models` | — |
| `Meter` | `Agency.Harness.Models` | — |
| Counter | `models.requests` | `agentic.models.operation` |
| Counter | `models.errors` | same |
| Counter | `models.returned` | same |
| Histogram | `models.duration` (ms) | same |

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Llm.Common]] | `Agent` depends on `IToolRegistry`, `ITool`, `ToolDefinition`, `ToolResult`, `Model`, `IModelProvider`, and `LlmClientOptions` from this project |
| [[Agency.Llm.Claude]] | Concrete `IChatClient`/`IModelProvider` adapter for the Anthropic API; resolved by `Models.CreateChatClient` |
| [[Agency.Llm.OpenAI]] | Concrete `IChatClient`/`IModelProvider` adapter for the OpenAI-compatible API |
| [[Agency.Harness.Console]] | REPL harness that creates `ChatSession`, wires MCP/skills/permissions/progressive-discovery, renders `AgentEvent` streams, and answers `PermissionRequestedEvent`s |

## Design Notes

- **Permissions are park/resume events, not callbacks** — an unresolved or hook-`Ask` tool call does not block on a host callback; the loop **parks** the whole batch onto `Context.PendingToolBatch`, emits `PermissionRequestedEvent`s, ends the turn with `AwaitingPermission`, and waits for `ResumeWithPermissionsAsync`. This keeps the agent loop a pure `IAsyncEnumerable` producer (no re-entrant host calls), makes a parked turn trivially serializable (`PendingToolBatch` is the explicit checkpoint target for the harness state-persistence work), and lets multiple parallel calls in one batch each get their own request/response rather than serializing prompts.
- **`IPermissionEvaluator.Evaluate` is pure; the agent owns park/resume** — the evaluator never blocks, renders, or talks to the user; it just returns `Allow`/`Deny`/`Ask`. Park/resume is agent machinery, so a hook `Ask` still parks the turn even when no evaluator is supplied. The combined per-call precedence is deny-rule > active-skill pre-approval > hook-Ask > allow-rule > unresolved, with `OnUnresolved=Deny` failing closed for headless/CI runs.
- **Progressive discovery is a registry decorator, not an `Agent` change** — `ProgressiveDiscoveryToolRegistry` overrides only `ListDefinitions()` (withhold MCP schemas, summarize their descriptions, reveal native tools in full, append `tool_help`) and `InvokeAsync` (route `tool_help`, delegate the rest). Because `Agent` only calls those two members, the feature drops in via the `ToolContext` registry with zero loop changes. Every real tool keeps its own name as the call name, so permissions, hooks, the `agent.tool.calls` metric, and `ToolInvokedEvent` are unaffected — a generic dispatcher would have masked the real name and broken all four.
- **Schema withholding is split by origin, and self-heals** — only MCP tools (large, numerous, operationId-named) drop to the bare `{"type":"object"}` placeholder + one-line summary; native tools stay inline so they need no `tool_help` round-trip. The summary keeps the leading `Vendor |` prefix because for prefix-less server names it is the model's only inline attribution cue. `tool_help` reads the *undecorated* inner registry, so detail is hidden from the catalog but always recoverable; and when a withheld tool is called with missing required arguments, `InvokeAsync` folds the full schema into the error so the retry self-heals without a separate round-trip.
- **Skills use a reloadable catalog + file watcher** — `ReloadableSkillCatalog` swaps its inner catalog atomically (volatile reference) and `SkillWatcher` debounces `FileSystemWatcher` bursts into a single `Reload()`. Because `SkillContext` holds the *live* catalog reference, edits to `SKILL.md` files are picked up on the next iteration without rebuilding `Context` — the system prompt's `## Skills` listing and the `skill` tool always reflect the current set. A failed re-scan keeps the prior catalog intact rather than corrupting state.
- **Active-skill pre-approval is bounded to one turn** — invoking the `skill` tool records the skill's `allowed-tools` into `ActiveSkillState`, which the permission gate treats as a user grant (clearing even a hook-`Ask`) — but only deny rules still win. The window is cleared at the start of the next user turn, so "load skill X, which is allowed to run these tools" never silently persists pre-approval across turns.
- **Skill rendering separates substitution from shell execution** — `SkillRenderer.Render` is a pure string transform (placeholder substitution only), unit-testable with no I/O; `ExpandShellAsync` is a separate, single-pass step that runs `` !`cmd` `` / fenced ```` ```! ```` directives via an injected `ISkillShellRunner` and never re-scans command output (preventing injection escalation). Shell expansion is suppressed entirely when no runner is wired or `DisableShellExecution` is set, leaving directives visible but inert.
- **Config-driven hooks fail open** — a `CommandHookHandler`/`HttpHookHandler` that exits non-zero for any reason other than `2` (e.g. `1` non-blocking error, `-1` timeout) is treated as `Allow`. Only exit code `2` or `{ "hookSpecificOutput": { "permissionDecision": "deny" } }` blocks. Transient script/network failures and timeouts therefore never silently block tool execution; operators must be explicit to deny.
- **Compile-once `HookRegistry` singleton** — `HookRegistry` resolves all `HookMatcher`s and creates all `IHookHandler`s in its constructor, so per-invocation work is just `IsMatch(toolName)` calls; `ToAgentHooks()` projects it to a standard `AgentHooks` once at startup. `HooksOptions` subclasses `Dictionary<HookEventName, …>` because `IConfiguration.Bind` cannot populate a class property of dictionary type from a flat JSON object with dynamic keys.
- **Three-source hook fold: Baseline ▸ Configured ▸ User** — `Fold` composes the three `AgentOptions` slots in one pass, skipping nulls. This guarantees retrieval (baseline) runs before operator policy (configured) runs before application observation (user). `Compose`'s `OnPreToolUse` aggregation is most-restrictive-wins (Deny > Ask > Rewrite > Allow), evaluated via `Task.WhenAll`, so ordering of the decision is deterministic regardless of which source produced it.
- **`Context` is caller-owned, loop-mutates only counters** — `IterationCount`, `TotalCostUsd`, and `TotalUsage` are the only `internal set` counters. `Knowledge`/`Memory`/`Focus`/`Session` are `set` so lifecycle hooks (and the loop's first-turn `Session.Id` assignment) can mutate them mid-session; everything else is `init`-only, keeping session state predictable and snapshot-friendly.
- **`SystemPromptBuilder` is a pure function** — rebuilt from `Context` every iteration so knowledge facts, retrieved records, the skills catalog, and the context-window budget are always fresh; being a static pure function makes it unit-testable in isolation from the loop.
- **MCP tools and native tools share one flat namespace** — both register into the same name-keyed `ToolRegistry`; once registered the loop dispatches purely by tool name. `Register` is last-write-wins with no collision guard, so a server-supplied name that collides with a native tool (e.g. another `read_file`) silently overwrites it — name MCP servers' tools defensively. `McpProxyTool` keeps only text content blocks, dropping images/embedded resources. `McpClientPool` is `IAsyncDisposable`; use `await using` to close all server connections even on cancellation.
- **Tool-payload logging is opt-in** — by default tool calls and failures are logged by name with payloads redacted; `AgentOptions.LogToolPayloads` (wired to `Agent`'s `logToolPayloads`) logs full inputs and error-result content verbatim. It is off by default because payloads may contain file contents, commands, and ids.
