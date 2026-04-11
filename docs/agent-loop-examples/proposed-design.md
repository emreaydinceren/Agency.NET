# Proposed Design: `Agency.Agentic` Agent Loop

> Companion to [`index.md`](./index.md). Translates the patterns surveyed
> in the 20 reference implementations into a single concrete design for
> `src/Agency.Agentic/`.

## 1. Goals & non-goals

### Goals (v1)
1. A working agent loop driven by `Agency.Llm.Claude` (Anthropic-first,
   since the existing `ClaudeClient` is the most complete provider).
2. Layered `Context` is the **canonical state**; the wire-format
   `messages[]` array is *derived* on every iteration.
3. Tool calls and tool results are first-class typed values, not strings.
4. Loop termination is pluggable via `stopWhen` predicates with sensible
   defaults (`NoToolCalls` + `StepCountIs(20)`).
5. Public surface is an `IAsyncEnumerable<AgentEvent>` so callers can
   stream turns as they happen (matches the existing `StreamAsync`
   pattern on `ILlmClient`).
6. Provider-agnostic *internally* — the same `Context` can be serialized
   to either Anthropic or OpenAI wire shape by a translator.

### Non-goals (v1)
- Multi-agent orchestration / sub-agents (out, deferred to v2).
- Auto-compaction (out — instead, hard-cap by `StopConditions` and surface
  a `CompactBoundary` event so a v2 hook can do it).
- Durable workflow state across process restarts (out — but the
  `Context` shape must be JSON-serializable so v2 can plug Temporal-style
  persistence in without redesigning).
- Code-execution agents (smolagents `CodeAgent` style) — out.

---

## 2. Architectural decisions

| # | Decision | Why | Reference |
|---|----------|-----|-----------|
| D1 | **Anthropic content-block shape** as the internal message model | `Agency.Llm.Claude` is the primary backend; the block shape is a strict superset of OpenAI's `tool_calls` array (easier to translate down than up) | #1, #14 |
| D2 | **`Context` is canonical, `messages[]` is derived** via a pure `MessageBuilder.Build(ctx)` called once per iteration | Matches the layered context skeleton already in `Class1.cs`; lets context layers evolve without touching the loop | #10 |
| D3 | **`KnowledgeContext` is re-injected into the system prompt every iteration**, never appended to history | Prevents stale snapshots; mirrors how Claude Code treats `CLAUDE.md` | #7 |
| D4 | **Pluggable `StopCondition` delegates**, default `[NoToolCalls, StepCountIs(20)]` | Cleanest extensibility seam; lets callers add `BudgetExceeded`, `WallClockExceeded`, custom predicates | #9 |
| D5 | **New `ILlmClient.SendAgentAsync`** taking `IReadOnlyList<AgentMessage>` + `IReadOnlyList<ToolDefinition>` | The current text-only signature can't carry `tool_use` blocks back. This is the single non-deferrable plumbing change. | — |
| D6 | **Public API yields `AgentEvent`s via `IAsyncEnumerable`** | Lets UIs stream turns as they happen; matches existing `StreamAsync` shape on `ILlmClient` | #7, #13 |
| D7 | **Tool execution dispatched via `IToolRegistry`** indexed by name | Clean DI seam; tools are registered, not hardcoded into the loop | #15 |
| D8 | **Stop-reason branching uses the existing `Agency.Llm.Common.StopReason` enum** (`EndTurn` / `ToolUse` / `MaxTokens` / `PauseTurn` / etc.) | Already exists with the right values — no parallel enum | repo |
| D9 | **Parallel tool execution** via `Task.WhenAll` for multi-tool turns | Tools within a single assistant turn are independent (generated from the same context snapshot); parallel execution is a significant latency win | #12 |
| D10 | **LLM call retry with backoff**, tool calls are NOT retried | LLM calls are idempotent; tool calls may have side effects. The LLM can retry a failed tool by issuing another `ToolUseBlock` in the next turn | #12 |
| D11 | **Explicit ReAct reasoning instruction** in the system prompt | Encourages chain-of-thought before tool use; improves tool selection accuracy and makes agent behavior more transparent/debuggable | #12 |
| D12 | **Token-level streaming via `StreamAgentAsync`** as the default code path, with `SendAgentAsync` as a non-streaming fallback | UX requires seeing text as it arrives; mirrors existing `StreamAsync`/`SendAsync` duality on `ILlmClient`; both paths produce identical turn-level events | §4.7 |

---

## 3. Type model

All new types live in `Agency.Agentic` unless noted.

### 3.1 Messages and content blocks (Anthropic shape)

```csharp
namespace Agency.Agentic.Messages;

public enum MessageRole { System, User, Assistant }

public abstract record ContentBlock;

public sealed record TextBlock(string Text) : ContentBlock;

public sealed record ToolUseBlock(
    string Id,                     // matches ToolResultBlock.ToolUseId
    string Name,
    JsonElement Input) : ContentBlock;

public sealed record ToolResultBlock(
    string ToolUseId,
    string Content,                // serialized result (JSON or plain text)
    bool IsError = false) : ContentBlock;

public sealed record ThinkingBlock(string Thinking) : ContentBlock;

public sealed record AgentMessage(
    MessageRole Role,
    IReadOnlyList<ContentBlock> Content);
```

**Pairing rule.** Every `ToolUseBlock` produced by the assistant must be
followed (eventually) by a `ToolResultBlock` with the same `Id`/`ToolUseId`
in a subsequent user message. The loop enforces this — a missing pair
throws before the next LLM call.

### 3.2 Tool definitions and registry

```csharp
namespace Agency.Agentic.Tools;

public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonElement InputSchema);      // JSON Schema for the tool's parameters

public interface ITool
{
    ToolDefinition Definition { get; }
    Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct);
}

public sealed record ToolResult(string Content, bool IsError = false);

public interface IToolRegistry
{
    IReadOnlyList<ToolDefinition> ListDefinitions();
    Task<ToolResult> InvokeAsync(string name, JsonElement input, CancellationToken ct);
}

internal sealed class ToolRegistry(IEnumerable<ITool> tools) : IToolRegistry { ... }
```

### 3.3 Context (revised from `Class1.cs`)

The existing layers stay; the loose ends are tightened. Key change:
**`MessageHistory` becomes `IReadOnlyList<AgentMessage>` and lives behind
an `IConversationManager`** so future strategies (sliding window,
summarization) can plug in.

```csharp
namespace Agency.Agentic;

public sealed class Context
{
    public required QueryContext Query { get; init; }
    public KnowledgeContext Knowledge { get; init; } = KnowledgeContext.Empty;
    public MemoryContext Memory { get; init; } = MemoryContext.Empty;
    public ToolContext Tools { get; init; } = ToolContext.Empty;
    public UserSpecificContext User { get; init; } = UserSpecificContext.Empty;
    public TemporalContext Temporal { get; init; } = TemporalContext.Empty;
    public EnvironmentalContext Environment { get; init; } = EnvironmentalContext.Empty;

    // Mutable, owned by the loop:
    public IConversationManager Conversation { get; init; } = new InMemoryConversationManager();
    public int IterationCount { get; internal set; }
    public decimal TotalCostUsd { get; internal set; }
    public LlmTokenUsage TotalUsage { get; internal set; } = new(0, 0);
}
```

`ToolContext` gains a `Registry` field so tools are carried alongside the
rest of the context:

```csharp
public sealed class ToolContext
{
    public static ToolContext Empty { get; } = new();
    public IToolRegistry Registry { get; init; } = EmptyToolRegistry.Instance;
}
```

### 3.4 Conversation manager

```csharp
public interface IConversationManager
{
    IReadOnlyList<AgentMessage> Messages { get; }
    void Append(AgentMessage message);
}

internal sealed class InMemoryConversationManager : IConversationManager
{
    private readonly List<AgentMessage> _messages = new();
    public IReadOnlyList<AgentMessage> Messages => _messages;
    public void Append(AgentMessage message) => _messages.Add(message);
}
```

V1 has only the in-memory implementation. V2 adds
`SlidingWindowConversationManager` and `SummarizingConversationManager`
without touching the loop.

### 3.5 Stop conditions

```csharp
namespace Agency.Agentic;

public delegate bool StopCondition(Context ctx, AgentMessage lastResponse);

public static class StopConditions
{
    public static StopCondition StepCountIs(int n) =>
        (ctx, _) => ctx.IterationCount >= n;

    public static readonly StopCondition NoToolCalls =
        (_, msg) => !msg.Content.OfType<ToolUseBlock>().Any();

    public static StopCondition BudgetExceeded(decimal usd) =>
        (ctx, _) => ctx.TotalCostUsd >= usd;

    public static StopCondition TokensExceeded(long total) =>
        (ctx, _) => ctx.TotalUsage.TotalTokens >= total;

    public static StopCondition Any(params StopCondition[] conditions) =>
        (ctx, msg) => conditions.Any(c => c(ctx, msg));
}
```

Default for v1: `StopConditions.Any(StopConditions.NoToolCalls, StopConditions.StepCountIs(20))`.

### 3.6 Agent events (public streaming surface)

Mirrors Anthropic SDK's five message types (#7), trimmed to what v1
emits:

```csharp
public abstract record AgentEvent;
public sealed record SessionStartedEvent(string SessionId) : AgentEvent;
public sealed record AssistantTurnEvent(AgentMessage Message) : AgentEvent;
public sealed record ToolInvokedEvent(string ToolName, JsonElement Input, ToolResult Result) : AgentEvent;
public sealed record IterationCompletedEvent(int Iteration, LlmTokenUsage TurnUsage) : AgentEvent;
public sealed record AgentResultEvent(
    AgentResultStatus Status,
    string? FinalText,
    LlmTokenUsage TotalUsage,
    decimal TotalCostUsd) : AgentEvent;

public enum AgentResultStatus { Success, MaxStepsReached, BudgetExceeded, Error }
```

**Token-level streaming events** (emitted when using the streaming code
path — see §4.7):

```csharp
// Emitted for each text token as it arrives from the LLM.
public sealed record TextDeltaEvent(string Delta) : AgentEvent;

// Emitted when a ToolUseBlock is fully received from the stream
// (before execution begins). Gives UIs a chance to show "calling tool X…".
public sealed record ToolUseReceivedEvent(string ToolName, string ToolUseId) : AgentEvent;
```

The streaming path emits `TextDeltaEvent`s as tokens arrive, **then**
emits `AssistantTurnEvent` with the fully-assembled message once the
stream completes. Non-streaming consumers can ignore `TextDeltaEvent`
entirely and only listen for `AssistantTurnEvent` — both paths produce
the same sequence of turn-level events, the streaming path just
interleaves deltas before each turn.

---

## 4. The loop

### 4.1 Public API

```csharp
namespace Agency.Agentic;

public sealed class Agent(
    ILlmClient llm,
    string model,
    StopCondition? stopWhen = null,
    ILogger<Agent>? logger = null)
{
    private readonly StopCondition _stop = stopWhen ?? StopConditions.Any(
        StopConditions.NoToolCalls,
        StopConditions.StepCountIs(20));

    public async IAsyncEnumerable<AgentEvent> RunAsync(
        Context ctx,
        [EnumeratorCancellation] CancellationToken ct = default)
    { ... }
}
```

### 4.2 Loop body (pseudocode, ~50 LOC target)

```csharp
public async IAsyncEnumerable<AgentEvent> RunAsync(
    Context ctx, [EnumeratorCancellation] CancellationToken ct = default)
{
    yield return new SessionStartedEvent(Guid.NewGuid().ToString("N"));

    // 1. Seed conversation with the user prompt if empty.
    if (ctx.Conversation.Messages.Count == 0)
    {
        ctx.Conversation.Append(new AgentMessage(
            MessageRole.User,
            [new TextBlock(ctx.Query.Prompt)]));
    }

    var tools = ctx.Tools.Registry.ListDefinitions();
    AgentMessage? lastAssistant = null;

    while (true)
    {
        ct.ThrowIfCancellationRequested();
        ctx.IterationCount++;

        // 2. Build system prompt fresh every iteration (D3).
        var systemPrompt = SystemPromptBuilder.Build(ctx);

        // 3. Call the LLM with the full message history + tool defs.
        //    Wrapped in a retry policy for transient failures (429, 503, timeouts).
        //    LLM calls are idempotent — safe to retry. See §4.5.
        var response = await retryPolicy.ExecuteAsync(() => llm.SendAgentAsync(
            model: model,
            systemPrompt: systemPrompt,
            messages: ctx.Conversation.Messages,
            tools: tools,
            ct: ct));

        ctx.TotalUsage = Add(ctx.TotalUsage, response.Usage);
        // (optional) ctx.TotalCostUsd += CostCalculator.Estimate(model, response.Usage);

        lastAssistant = response.Message;
        ctx.Conversation.Append(lastAssistant);
        yield return new AssistantTurnEvent(lastAssistant);
        yield return new IterationCompletedEvent(ctx.IterationCount, response.Usage);

        // 4. Stop?
        if (_stop(ctx, lastAssistant))
        {
            var status = DetermineStatus(ctx, lastAssistant);
            var finalText = ExtractText(lastAssistant);
            yield return new AgentResultEvent(status, finalText, ctx.TotalUsage, ctx.TotalCostUsd);
            yield break;
        }

        // 5. Execute tool calls and append results as a single user message.
        var toolUses = lastAssistant.Content.OfType<ToolUseBlock>().ToList();
        if (toolUses.Count == 0)
        {
            // Stop predicate disagreed with reality — defensive break.
            yield return new AgentResultEvent(
                AgentResultStatus.Success, ExtractText(lastAssistant),
                ctx.TotalUsage, ctx.TotalCostUsd);
            yield break;
        }

        // 5a. Execute tool calls in parallel — they are independent within
        //     a single assistant turn. See §4.6 for rationale.
        var resultBlocks = new ContentBlock[toolUses.Count];
        var toolTasks = toolUses.Select(async (use, index) =>
        {
            ct.ThrowIfCancellationRequested();
            ToolResult result;
            try
            {
                result = await ctx.Tools.Registry.InvokeAsync(use.Name, use.Input, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "Tool {Tool} failed", use.Name);
                result = new ToolResult($"Tool error: {ex.Message}", IsError: true);
            }

            resultBlocks[index] = new ToolResultBlock(use.Id, result.Content, result.IsError);
            return new ToolInvokedEvent(use.Name, use.Input, result);
        });

        var toolEvents = await Task.WhenAll(toolTasks);
        foreach (var evt in toolEvents)
            yield return evt;

        ctx.Conversation.Append(new AgentMessage(MessageRole.User, resultBlocks));
    }
}
```

This is intentionally close to the canonical Anthropic loop (#1) and the
MS Foundry .NET executor (#15), with the differences being: typed events
on the way out, `Context` carrying tools and stop conditions, and a
fresh-every-iteration system prompt assembly.

### 4.3 `SystemPromptBuilder` (D2 + D3)

The single seam between layered `Context` and the wire format. Pure
function, fully unit-testable:

```csharp
internal static class SystemPromptBuilder
{
    public static string Build(Context ctx)
    {
        var sb = new StringBuilder();

        // Stable identity / persona (could come from a config later).
        sb.AppendLine("You are an autonomous agent operating inside the Agency runtime.");
        sb.AppendLine();

        // ReAct reasoning instruction — encourage chain-of-thought before tool use.
        sb.AppendLine("When solving a task, always explain your reasoning before taking actions.");
        sb.AppendLine("Break complex problems into steps: Reason about what to do, Act using tools, then Observe the results before deciding next steps.");

        // KnowledgeContext re-injected on every iteration (D3).
        if (ctx.Knowledge is not { } empty || !ReferenceEquals(empty, KnowledgeContext.Empty))
            AppendKnowledge(sb, ctx.Knowledge);

        // LongTermMemory summarized into the system prompt.
        if (ctx.Memory.LongTermMemory.Count > 0)
        {
            sb.AppendLine("\n## Long-term memory");
            foreach (var item in ctx.Memory.LongTermMemory)
                sb.AppendLine($"- {item}");
        }

        // Temporal + Environmental as a preamble.
        AppendTemporal(sb, ctx.Temporal);
        AppendEnvironment(sb, ctx.Environment);
        AppendUser(sb, ctx.User);

        return sb.ToString();
    }
}
```

`MemoryContext.ShortTermMemory` is **not** part of the system prompt —
it's flushed into the conversation as recent user/assistant turns by a
helper invoked once at session start (or by a `SlidingWindowConversationManager`
in v2).

### 4.4 Context layer → wire mapping

| Context layer            | Where it lands                                                          |
|--------------------------|-------------------------------------------------------------------------|
| `QueryContext.Prompt`    | First `AgentMessage(User, [TextBlock])` appended at session start       |
| `KnowledgeContext`       | System prompt, **rebuilt every iteration**                              |
| `MemoryContext.ShortTerm`| Seeded into `Conversation` at session start as prior turns              |
| `MemoryContext.LongTerm` | Summarized into the system prompt                                       |
| `ToolContext.Registry`   | Passed alongside `messages` as `IReadOnlyList<ToolDefinition>` to `SendAgentAsync` |
| `TemporalContext`        | System prompt preamble (current date/time, timezone)                    |
| `EnvironmentalContext`   | System prompt preamble (OS, working dir, etc.)                          |
| `UserSpecificContext`    | System prompt preamble (user identity, preferences)                     |
| Tool calls / results     | `AgentMessage` entries with `ToolUseBlock` / `ToolResultBlock`          |

### 4.5 LLM call retry policy

LLM API calls are idempotent (same messages in → same-shaped response
out) but subject to transient failures: rate limits (429), server errors
(503), and network timeouts. The loop wraps `SendAgentAsync` in a
configurable retry policy.

```csharp
public sealed class Agent(
    ILlmClient llm,
    string model,
    StopCondition? stopWhen = null,
    IAsyncPolicy? retryPolicy = null,    // e.g., Polly policy
    ILogger<Agent>? logger = null)
{
    private readonly IAsyncPolicy _retry = retryPolicy
        ?? Policy.Handle<HttpRequestException>()
                 .Or<TaskCanceledException>(ex => !ex.CancellationToken.IsCancellationRequested)
                 .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)));
    ...
}
```

**Important distinction:** LLM calls are retried; **tool calls are not**
automatically retried. Tools may have side effects (send email, write to
database) and are therefore not idempotent. If a tool fails, the error
is captured as a `ToolResultBlock(IsError: true)` and the LLM decides
whether to retry by issuing another `ToolUseBlock` in the next turn.

### 4.6 Parallel tool execution

When the LLM returns multiple `ToolUseBlock`s in a single assistant
turn, they are independent of each other (the LLM generated them from
the same context snapshot). Executing them in parallel via
`Task.WhenAll` is both correct and a significant performance win for
multi-tool turns.

The loop uses indexed `Select` + `Task.WhenAll` (see §4.2 step 5a) to
preserve result ordering. Each tool invocation is independently
exception-guarded — a failure in one tool does not cancel the others.

**When to fall back to sequential:** If a future `ITool` implementation
declares itself as non-concurrent-safe (e.g., a tool that writes to a
shared file), the `IToolRegistry` can respect a `ConcurrencyMode`
attribute. This is a v2 concern; v1 assumes all tools are safe to run
concurrently.

### 4.7 Token-level streaming (UX-visible responses)

Interactive UIs need to show text as it arrives, not after the full LLM
turn completes. The agent loop supports this via a streaming code path
that replaces the `SendAgentAsync` call with `StreamAgentAsync`.

**Design principle:** the streaming path produces the **exact same
sequence of turn-level events** (`AssistantTurnEvent`,
`IterationCompletedEvent`, etc.) as the non-streaming path, but
interleaves `TextDeltaEvent`s before each `AssistantTurnEvent`. Callers
that only care about complete turns can ignore `TextDeltaEvent` entirely.

```csharp
// Inside RunAsync, step 3 becomes:
// 3. Stream the LLM response, forwarding deltas as events.
AgentMessage? assembledMessage = null;
LlmTokenUsage? turnUsage = null;

await foreach (var chunk in llm.StreamAgentAsync(
    model, systemPrompt, ctx.Conversation.Messages, tools, ct: ct))
{
    // Forward text deltas to the UI as they arrive.
    if (chunk.TextDelta is not null)
        yield return new TextDeltaEvent(chunk.TextDelta);

    // When a complete tool_use block arrives, notify the UI.
    if (chunk.CompletedBlock is ToolUseBlock toolBlock)
        yield return new ToolUseReceivedEvent(toolBlock.Name, toolBlock.Id);

    // Terminal chunk: the fully-assembled message + usage.
    if (chunk.Message is not null)
    {
        assembledMessage = chunk.Message;
        turnUsage = chunk.Usage;
    }
}

var response = new AgentLlmResponse(
    assembledMessage!, chunk.StopReason!.Value, turnUsage!);

// ... rest of the loop body (steps 3b onward) is identical.
```

**How streaming maps to the Anthropic SSE protocol:**

| Anthropic SSE event | `AgentLlmStreamChunk` field | `AgentEvent` emitted |
| ----- | ------ | ------ |
| `content_block_delta` (type `text_delta`) | `TextDelta` | `TextDeltaEvent` |
| `content_block_delta` (type `thinking_delta`) | `ThinkingDelta` | *(not surfaced in v1)* |
| `content_block_stop` (type `tool_use`) | `CompletedBlock = ToolUseBlock(...)` | `ToolUseReceivedEvent` |
| `message_stop` | `Message` + `Usage` + `StopReason` | `AssistantTurnEvent` (same as non-streaming) |

**Choosing the code path.** The `Agent` constructor accepts an optional
`bool stream = true` parameter. When `true` (the default), the loop
uses `StreamAgentAsync`; when `false`, it uses `SendAgentAsync`. This
lets test harnesses use the simpler non-streaming path while production
UIs get token-level output.

```csharp
public sealed class Agent(
    ILlmClient llm,
    string model,
    StopCondition? stopWhen = null,
    bool stream = true,            // default: stream for UX
    ILogger<Agent>? logger = null)
```

**Why both paths exist.** `SendAgentAsync` is simpler to implement for
LLM providers, simpler to test (no async enumeration), and required for
providers that don't support streaming. `StreamAgentAsync` is the
production default. The loop body is the same in both cases — only the
LLM call in step 3 differs.

---

## 5. Plumbing changes outside `Agency.Agentic`

### 5.1 `Agency.Llm.Common/ILlmClient.cs` — add `SendAgentAsync`

The current `ILlmClient` is text-only and **cannot** carry tool blocks
back. This is the one non-deferrable change in another project.

```csharp
namespace Agency.Llm.Common;

public interface ILlmClient
{
    // (existing SendAsync / StreamAsync stay)

    /// <summary>
    /// Sends a tool-aware completion request. The response message may
    /// contain TextBlock and/or ToolUseBlock content blocks.
    /// </summary>
    Task<AgentLlmResponse> SendAgentAsync(
        string model,
        string systemPrompt,
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        long? maxTokens = 4096,
        float? temperature = null,
        CancellationToken ct = default);

    /// <summary>
    /// Streams a tool-aware completion response as it is generated.
    /// Text deltas arrive as chunks; the terminal chunk carries the
    /// fully-assembled <see cref="AgentMessage"/>, usage, and stop reason.
    /// Mirrors the existing <see cref="StreamAsync"/> pattern but for
    /// agent-level content blocks.
    /// </summary>
    IAsyncEnumerable<AgentLlmStreamChunk> StreamAgentAsync(
        string model,
        string systemPrompt,
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        long? maxTokens = 4096,
        float? temperature = null,
        CancellationToken ct = default);
}

public sealed record AgentLlmResponse(
    AgentMessage Message,        // role = Assistant, content = blocks
    StopReason FinishReason,
    LlmTokenUsage Usage);

/// <summary>
/// A single chunk in a streaming agent LLM response.
/// Follows the same delta/terminal pattern as <see cref="LlmStreamChunk"/>:
/// <list type="bullet">
///   <item>Delta chunks: <see cref="TextDelta"/> or <see cref="ThinkingDelta"/> set,
///         everything else null.</item>
///   <item>Block-complete chunks: <see cref="CompletedBlock"/> set when a full
///         <see cref="ToolUseBlock"/> or <see cref="ThinkingBlock"/> has been
///         assembled from deltas.</item>
///   <item>Terminal chunk: <see cref="Message"/>, <see cref="Usage"/>, and
///         <see cref="StopReason"/> set — the fully-assembled response.</item>
/// </list>
/// </summary>
public sealed record AgentLlmStreamChunk(
    string? TextDelta,
    string? ThinkingDelta,
    ContentBlock? CompletedBlock,    // non-null when a full block is ready
    AgentMessage? Message,           // non-null only on terminal chunk
    StopReason? StopReason,
    LlmTokenUsage? Usage);
```

**Type ownership question.** `AgentMessage`, `ContentBlock`, and
`ToolDefinition` need to be visible from both `Agency.Llm.Common` and
`Agency.Agentic`. Two options:

- **(A, recommended)** Move them to `Agency.Llm.Common` under a
  `Agency.Llm.Common.Messages` namespace. They're transport-level types,
  not agent-loop-specific. `Agency.Agentic` re-exports/uses them.
- **(B)** Put them in a new `Agency.Agentic.Abstractions` project that
  `Agency.Llm.Common` depends on. Reverses the existing dependency
  direction (today `Agency.Agentic` would depend on `Agency.Llm.Common`)
  and feels backwards.

Go with **(A)**. It also lets future non-agent callers send tool-using
prompts without taking a dep on the whole agent loop.

### 5.2 `Agency.Llm.Claude/ClaudeClient.cs` — implement `SendAgentAsync` + `StreamAgentAsync`

**`SendAgentAsync`:** Translate `IReadOnlyList<AgentMessage>` directly
to the Anthropic SDK's `MessageParam` shape (it's a 1:1 mapping by
design — D1). On the way back, fold the response's content blocks into a
single `AgentMessage` and map `stop_reason` to
`Agency.Llm.Common.StopReason` (the enum already has `ToolUse`,
`EndTurn`, `MaxTokens`, `PauseTurn`).

**`StreamAgentAsync`:** Uses Anthropic's server-sent events (SSE)
streaming endpoint. The implementation:

1. Subscribes to the SSE stream via the Anthropic SDK's
   `CreateMessageStreamAsync`.
2. On `content_block_delta` with type `text_delta` → yield
   `AgentLlmStreamChunk(TextDelta: delta.Text)`.
3. On `content_block_delta` with type `thinking_delta` → yield
   `AgentLlmStreamChunk(ThinkingDelta: delta.Thinking)`.
4. On `content_block_stop` → if the completed block is a `tool_use`,
   yield `AgentLlmStreamChunk(CompletedBlock: ToolUseBlock(...))`.
5. On `message_stop` → assemble all accumulated content blocks into
   an `AgentMessage`, yield the terminal chunk with `Message`, `Usage`,
   and `StopReason`.

This mirrors the existing `StreamAsync` implementation in `ClaudeClient`
(which already handles SSE for text-only streaming) but adds content
block accumulation.

### 5.3 `Agency.Llm.OpenAI/OpenAIClient.cs` — implement `SendAgentAsync` + `StreamAgentAsync`

**`SendAgentAsync`** translator does the work:

- `ToolUseBlock` inside an assistant `AgentMessage` → entries in
  OpenAI's `tool_calls` array on the assistant `ChatMessage`
- `ToolResultBlock` inside a user `AgentMessage` → a separate
  `ToolChatMessage(toolCallId, content)` row (#15)

This is straightforward because Anthropic's shape is the strict
superset.

**`StreamAgentAsync`:** Uses OpenAI's streaming completions API. The
mapping is slightly more complex because OpenAI streams tool call
arguments as incremental JSON chunks (field `tool_calls[i].function
.arguments` delta). The implementation accumulates argument JSON per
tool-call index and yields a `CompletedBlock` once the tool call is
fully received.

### 5.4 `Agency.Agentic.csproj`

Add `ProjectReference` to `Agency.Llm.Common` (and nothing else). The
agent loop must not take a hard dep on `Agency.Llm.Claude` or
`Agency.Llm.OpenAI` — those are wired in by the host application.

### 5.5 Delete `Class1.cs`

Replace with one file per type, namespaced under `Agency.Agentic` and
`Agency.Agentic.Messages` / `Agency.Agentic.Tools` as appropriate.
Preserve the header comment about persistent goals + stateful handoffs by
moving it into `Agent.cs` as an XML doc on the class describing the v2
roadmap.

---

## 6. File layout

```
src/Agency.Agentic/
├── Agency.Agentic.csproj
├── Agent.cs                          // public Agent class + RunAsync loop
├── AgentEvents.cs                    // AgentEvent record hierarchy
├── Context.cs                        // Context + per-layer context classes
├── StopConditions.cs                 // StopCondition delegate + helpers
├── SystemPromptBuilder.cs            // internal pure function
├── Conversation/
│   ├── IConversationManager.cs
│   └── InMemoryConversationManager.cs
└── Tools/
    ├── ITool.cs
    ├── IToolRegistry.cs
    └── ToolRegistry.cs

src/Agency.Llm.Common/
├── ILlmClient.cs                     // + SendAgentAsync + StreamAgentAsync
├── AgentLlmResponse.cs               // response record for non-streaming path
├── AgentLlmStreamChunk.cs            // chunk record for streaming path
└── Messages/
    ├── AgentMessage.cs
    ├── ContentBlock.cs
    └── ToolDefinition.cs
```

---

## 7. Verification plan

1. **Build green.** `dotnet build src/Agency.slnx` passes warning-free
   (project enforces `TreatWarningsAsErrors=true` and `Nullable=enable`).
2. **Unit tests** (`Agency.Agentic.Test`, new project):
   - `SystemPromptBuilder_Build_RebuildsKnowledgeEachCall` — same
     `Context` mutated between calls produces different prompts.
   - `Loop_StopsOnNoToolCalls` — fake `ILlmClient` returns text-only;
     loop emits one `AssistantTurnEvent` then `AgentResultEvent(Success)`.
   - `Loop_ExecutesToolAndAppendsResult` — fake returns one
     `ToolUseBlock`; loop invokes a fake `ITool`, appends a
     `ToolResultBlock` with matching id, calls LLM again.
   - `Loop_HonorsStepCountIs` — `StopConditions.StepCountIs(2)` halts
     after 2 iterations even if tool calls continue; emits
     `AgentResultEvent(MaxStepsReached)`.
   - `Loop_ToolFailureBecomesErrorBlock` — tool throws; loop catches,
     appends `ToolResultBlock(IsError: true)`, continues.
   - `Loop_PairsToolUseIdsCorrectly` — the `Id` on the `ToolResultBlock`
     matches the originating `ToolUseBlock.Id`.
   - `Loop_ExecutesMultipleToolsInParallel` — fake returns 3
     `ToolUseBlock`s; each fake tool records a timestamp; assert all 3
     started within a tight window (not sequential).
   - `Loop_RetriesLlmOnTransientFailure` — fake `ILlmClient` throws
     `HttpRequestException` on first call, succeeds on second; loop
     completes successfully.
   - `SystemPromptBuilder_Build_IncludesReActReasoning` — output
     contains "explain your reasoning" instruction.
   - `Loop_StreamMode_EmitsTextDeltaEvents` — fake
     `StreamAgentAsync` yields 3 text delta chunks then terminal;
     loop emits 3 `TextDeltaEvent`s followed by `AssistantTurnEvent`.
   - `Loop_StreamMode_EmitsToolUseReceivedBeforeInvocation` — fake
     stream yields a completed `ToolUseBlock`; loop emits
     `ToolUseReceivedEvent` before `ToolInvokedEvent`.
   - `Loop_NonStreamMode_DoesNotEmitTextDeltaEvents` — with
     `stream: false`, no `TextDeltaEvent` is ever yielded.
3. **Integration test** (`Agency.Agentic.Test`, `Category=Functional`):
   - Against LM Studio at `http://llm-host.example:1234` (per existing
     functional-test convention in CLAUDE.md), drive a 2-tool agent
     (`get_current_time`, `add`) through one full ToolUse → result →
     final-text cycle. Assert `AgentResultStatus.Success`.
4. **Manual smoke test**: a tiny console host that wires
   `ClaudeClient` + a hand-rolled `ITool` and prints the
   `IAsyncEnumerable<AgentEvent>` stream.

---

## 8. Out of scope — explicit deferrals

These are real, but **not v1**. Listed so they're not forgotten:

| Feature | Defer to | Hook left in v1 |
|---------|----------|-----------------|
| Auto-compaction | v2 | `IConversationManager` is the seam; v2 ships `SummarizingConversationManager` |
| Sub-agents / handoffs (#17) | v2 | `Agent.RunAsync` is reentrant on a fresh `Context`; sub-agents are just nested calls |
| Durable state across processes (#8) | v2 | `Context` and `AgentMessage` are JSON-serializable by design (records + sealed hierarchies) |
| `BacklogContext` for persistent goals | v2 | New sibling of `MemoryContext`; doesn't change the loop, only `SystemPromptBuilder` |
| Cost tracking (`TotalCostUsd`) | v1.1 | Field exists; calculator is a separate concern wired in later |
| Hooks (PreToolUse / PostToolUse, #7) | v2 | `ToolRegistry` is the obvious place to wrap tools with decorators |
| Human-in-the-loop / tool approval | v2 | Insert `IToolApprovalPolicy` check before tool dispatch in §4.2 step 5a. The parallel tool-execution block is the natural interrupt point — approval gates each `InvokeAsync` call |
| Thinking-delta streaming (`ThinkingDeltaEvent`) | v1.1 | `ThinkingDelta` field exists on `AgentLlmStreamChunk` (§5.1); not surfaced as an `AgentEvent` in v1 — add when extended-thinking UX is needed |
| Tool concurrency control | v2 | v1 runs all tools in parallel via `Task.WhenAll` (§4.6); v2 adds `ConcurrencyMode` attribute on `ITool` for tools that need sequential execution |
| Code-execution / `CodeAgent` shape (#10) | v3 | Different paradigm; not on the roadmap |

---

## 9. What this design borrows, and from where

| From | What | Where it shows up |
|------|------|-------------------|
| #1 Anthropic computer-use | The loop body's exact shape (assistant turn → collect tool_use → execute → append tool_result as user) | `RunAsync` body |
| #15 MS Foundry .NET executor | The C# idioms for typing the message list and iterating | `RunAsync` body |
| #10 smolagents | `Context` is canonical, `messages[]` is derived; `SystemPromptBuilder` per iteration | D2, §4.3 |
| #7 Anthropic SDK docs | Five-message-type taxonomy as the public stream surface; CLAUDE.md re-injection pattern for `KnowledgeContext` | §3.6, D3 |
| #9 Vercel AI SDK | `stopWhen` / `StopCondition` as a pluggable predicate list | §3.5 |
| #11 AWS Strands | `IConversationManager` as a seam | §3.4 |
| #14 MS Agent Framework | Tool call/result as content blocks rather than separate role | D1 |
| #12 LangGraph `create_react_agent` | Parallel tool execution within a turn; explicit ReAct reasoning instruction in system prompt; retry with backoff for LLM calls; human-in-the-loop interrupt before tool dispatch | §4.2 step 5a, §4.3, §4.5, §8 deferral |

---

## 10. Open questions to resolve before implementation

1. **Cost calculator.** Where does the model→price mapping live? Probably
   `Agency.Llm.Common` next to `LlmTokenUsage`, but out of scope for v1.
2. **`JsonElement` vs `JsonNode`** for tool input/schema. Anthropic SDK
   uses `JsonElement`; Microsoft.Extensions.AI is migrating to
   `JsonElement` too. Going with `JsonElement` unless there's a counter.
3. **Cancellation semantics on tool failure.** Current design: catch and
   return as `ToolResultBlock(IsError: true)`, never propagate. Alternative:
   a `IToolErrorPolicy` enum (`Continue` / `Throw`). V1 hardcodes
   `Continue`; revisit if it bites.
4. **Should `Agent` be sealed?** Per CLAUDE.md guidance ("sealed classes
   cannot be mocked — use functional/integration tests or extract an
   interface"), and since v1 uses fakes for `ILlmClient` not `Agent`
   itself, **yes, seal it**. If a future caller needs to substitute,
   extract `IAgent` then.
5. **Retry policy dependency.** §4.5 shows Polly-style `IAsyncPolicy`.
   Options: (A) take a hard dependency on `Microsoft.Extensions.Resilience`
   (built on Polly v8, ships with .NET 10), (B) accept a `Func<Func<Task<T>>,
   Task<T>>` delegate so callers bring their own retry, (C) a simple
   hand-rolled 3-retry loop with exponential backoff and no external dep.
   Leaning **(C)** for v1 to keep the dependency graph minimal; switch to
   **(A)** if/when the solution already pulls in `Microsoft.Extensions.*`.
6. **Parallel tool execution and `yield return`.** C# does not allow
   `yield return` inside `async` lambdas. The §4.2 pseudocode collects
   `ToolInvokedEvent`s from `Task.WhenAll` and yields them after all
   tools complete. This means events are batched, not streamed per-tool.
   Acceptable for v1; v2 could use a `Channel<AgentEvent>` for true
   per-tool streaming.
7. **`StreamAgentAsync` default implementation.** Not all `ILlmClient`
   providers may support streaming. Options: (A) make `StreamAgentAsync`
   a default interface method that calls `SendAgentAsync` and yields a
   single terminal chunk (providers opt-in to real streaming), (B) throw
   `NotSupportedException` and let the `Agent` fall back. Leaning **(A)**
   — it makes `stream: true` always safe, even with a non-streaming
   provider, at the cost of no token-level deltas.
