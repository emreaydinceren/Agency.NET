# Hooks in Agency.Agentic

A design guide for adding Claude Code–style lifecycle hooks to the `Agency.Agentic` agent loop.

---

## Background: Claude Code Hooks vs. AgentEvents

`Agency.Agentic` already exposes an **observer stream** via `IAsyncEnumerable<AgentEvent>`. Consumers receive `SessionStartedEvent`, `AssistantTurnEvent`, `ToolInvokedEvent`, `IterationCompletedEvent`, and `AgentResultEvent` after each action completes. This is the *tell, don't ask* model: you hear what happened, but cannot change it.

**Claude Code hooks** are a different contract: they fire *before* or *after* actions and can:

- **Block** a tool call entirely (return `permissionDecision: "deny"`)
- **Rewrite** a tool's input before invocation
- **Inject** additional context into the agent's system prompt
- **Stop** the agent turn early

The table below maps Claude Code hook events to the equivalent points in `Agent.RunAsync`:

| Claude Code Hook | When | `Agency.Agentic` Equivalent |
|---|---|---|
| `SessionStart` | Session begins | Start of `RunAsync`, before first iteration |
| `UserPromptSubmit` | Before LLM processes user message | Before `_llm.GetResponseAsync` on the first turn |
| `PreToolUse` | Before any tool executes | Before `ctx.Tools.Registry.InvokeAsync` |
| `PostToolUse` | After successful tool execution | After `InvokeAsync` returns |
| `AssistantTurn` | After the LLM responds | After `AssistantTurnEvent` is yielded |
| `IterationCompleted` | After tools + append to conversation | After `IterationCompletedEvent` is yielded |
| `Stop` | Before agent halts | Before `AgentResultEvent` is yielded |

---

## Proposed API

### 1. Hook Context Types

Each hook receives a strongly-typed context record rather than raw JSON. These are immutable and carry exactly what that lifecycle point knows.

```csharp
namespace Agency.Agentic.Hooks;

/// <summary>Passed to hooks that fire at the start of a new agent run.</summary>
public sealed record SessionStartedHookContext(string SessionId, Context AgentContext);

/// <summary>Passed to hooks that fire before a tool is invoked.</summary>
public sealed record PreToolUseHookContext(string ToolName, JsonElement Input, Context AgentContext);

/// <summary>Passed to hooks that fire after a tool completes (success or error).</summary>
public sealed record PostToolUseHookContext(
    string ToolName,
    JsonElement Input,
    ToolResult Result,
    Context AgentContext);

/// <summary>Passed to hooks that fire after the LLM produces a response.</summary>
public sealed record AssistantTurnHookContext(ChatMessage Message, Context AgentContext);

/// <summary>Passed to hooks that fire before the agent emits its final result.</summary>
public sealed record StopHookContext(AgentResultEvent Result, Context AgentContext);
```

### 2. Hook Result Types

`PreToolUse` hooks need a decision type. All other hooks are fire-and-forget (they return `Task`).

```csharp
namespace Agency.Agentic.Hooks;

/// <summary>
/// The decision a <c>PreToolUse</c> hook returns.
/// </summary>
public abstract record PreToolUseDecision
{
    /// <summary>Allow the tool call to proceed with the original input.</summary>
    public sealed record Allow : PreToolUseDecision;

    /// <summary>
    /// Block the tool call. The agent receives <paramref name="Reason"/> as a tool error,
    /// which it can use to self-correct.
    /// </summary>
    public sealed record Deny(string Reason) : PreToolUseDecision;

    /// <summary>
    /// Rewrite the tool's input before invocation. Useful for normalising paths,
    /// injecting defaults, or substituting safer alternatives.
    /// </summary>
    public sealed record Rewrite(JsonElement NewInput) : PreToolUseDecision;

    /// <summary>Shorthand for <see cref="Allow"/>.</summary>
    public static PreToolUseDecision Allowed { get; } = new Allow();
}
```

When multiple `PreToolUse` hooks run (see [Composing Hooks](#composing-hooks)), the most restrictive decision wins: `Deny > Rewrite > Allow`.

### 3. The `AgentHooks` Record

All hooks are optional delegates on a single record. Pass `AgentHooks` to `Agent` via its constructor (or `AgentOptions`). A `null` delegate is a no-op.

```csharp
namespace Agency.Agentic.Hooks;

/// <summary>
/// Lifecycle hook delegates for an <see cref="Agent"/> run.
/// All members are optional; omit any delegate you do not need.
/// </summary>
public sealed record AgentHooks
{
    /// <summary>Fires once before the first iteration of the agent loop.</summary>
    public Func<SessionStartedHookContext, CancellationToken, Task>? OnSessionStarted { get; init; }

    /// <summary>
    /// Fires before each tool invocation. Return a <see cref="PreToolUseDecision"/> to
    /// allow, block, or rewrite the call.
    /// </summary>
    public Func<PreToolUseHookContext, CancellationToken, Task<PreToolUseDecision>>? OnPreToolUse { get; init; }

    /// <summary>Fires after each tool invocation, whether it succeeded or errored.</summary>
    public Func<PostToolUseHookContext, CancellationToken, Task>? OnPostToolUse { get; init; }

    /// <summary>Fires after the LLM emits a response and it has been appended to the conversation.</summary>
    public Func<AssistantTurnHookContext, CancellationToken, Task>? OnAssistantTurn { get; init; }

    /// <summary>Fires just before the agent emits <see cref="AgentResultEvent"/> and stops.</summary>
    public Func<StopHookContext, CancellationToken, Task>? OnStop { get; init; }

    /// <summary>Empty hooks — all delegates are null.</summary>
    public static AgentHooks None { get; } = new();
}
```

---

## Integration Points in `Agent.cs`

The following diff shows exactly where hooks slot into the existing `RunAsync` method. No existing logic is removed; hooks wrap the critical points.

### SessionStarted

```csharp
// BEFORE (existing)
yield return new SessionStartedEvent(sessionId);

// AFTER
yield return new SessionStartedEvent(sessionId);
if (hooks.OnSessionStarted is { } onSessionStarted)
{
    await onSessionStarted(new SessionStartedHookContext(sessionId, ctx), ct);
}
```

### PreToolUse / PostToolUse

The tool-execution block in `RunAsync` (the `Select` + `WhenAll`) becomes:

```csharp
var toolTasks = toolCalls.Select(async (call, index) =>
{
    ct.ThrowIfCancellationRequested();
    var input = ToJsonElement(call.Arguments);

    // --- PreToolUse hook ---
    if (hooks.OnPreToolUse is { } onPreToolUse)
    {
        var decision = await onPreToolUse(
            new PreToolUseHookContext(call.Name, input, ctx), ct);

        if (decision is PreToolUseDecision.Deny deny)
        {
            var blocked = new ToolResult($"[Blocked] {deny.Reason}", IsError: true);
            resultMessages[index] = new FunctionResultContent(call.CallId, blocked.Content);
            yield return new ToolInvokedEvent(call.Name, input, blocked);
            return;
        }

        if (decision is PreToolUseDecision.Rewrite rewrite)
        {
            input = rewrite.NewInput;
        }
    }

    // --- Existing invocation ---
    ToolResult result;
    try
    {
        result = await ctx.Tools.Registry.InvokeAsync(call.Name, input, ct);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        result = new ToolResult($"Tool error: {ex.Message}", IsError: true);
    }

    // --- PostToolUse hook ---
    if (hooks.OnPostToolUse is { } onPostToolUse)
    {
        await onPostToolUse(new PostToolUseHookContext(call.Name, input, result, ctx), ct);
    }

    // ... rest of existing per-tool telemetry + event yield
});
```

### AssistantTurn

```csharp
// BEFORE (existing)
ctx.Conversation.Append(lastAssistant);
yield return new AssistantTurnEvent(lastAssistant);

// AFTER
ctx.Conversation.Append(lastAssistant);
yield return new AssistantTurnEvent(lastAssistant);
if (hooks.OnAssistantTurn is { } onAssistantTurn)
{
    await onAssistantTurn(new AssistantTurnHookContext(lastAssistant, ctx), ct);
}
```

### Stop

```csharp
// BEFORE (existing)
yield return new AgentResultEvent(status, finalText, ctx.TotalUsage, ctx.TotalCostUsd);
yield break;

// AFTER
var resultEvent = new AgentResultEvent(status, finalText, ctx.TotalUsage, ctx.TotalCostUsd);
if (hooks.OnStop is { } onStop)
{
    await onStop(new StopHookContext(resultEvent, ctx), ct);
}
yield return resultEvent;
yield break;
```

---

## Wiring Hooks to `Agent`

Add `AgentHooks` as an optional constructor parameter (defaulting to `AgentHooks.None`):

```csharp
public Agent(
    IChatClient llm,
    string model,
    string? clientType = null,
    StopCondition? stopWhen = null,
    AgentHooks? hooks = null,         // <-- new
    ILogger<Agent>? logger = null)
{
    // ...
    this._hooks = hooks ?? AgentHooks.None;
}

private readonly AgentHooks _hooks;
```

`ChatAsync` passes `this._hooks` through to `RunAsync`.

---

## Composing Hooks

When you need several independent behaviours (audit logging *and* a block-list), compose them rather than putting all logic in one delegate:

```csharp
public static class AgentHooksExtensions
{
    /// <summary>
    /// Returns a new <see cref="AgentHooks"/> where <paramref name="second"/> runs after
    /// <paramref name="first"/>. For <c>PreToolUse</c>, the most restrictive decision wins.
    /// </summary>
    public static AgentHooks Compose(this AgentHooks first, AgentHooks second) => new()
    {
        OnSessionStarted = Combine(first.OnSessionStarted, second.OnSessionStarted),
        OnPreToolUse = CombinePreToolUse(first.OnPreToolUse, second.OnPreToolUse),
        OnPostToolUse = Combine(first.OnPostToolUse, second.OnPostToolUse),
        OnAssistantTurn = Combine(first.OnAssistantTurn, second.OnAssistantTurn),
        OnStop = Combine(first.OnStop, second.OnStop),
    };

    private static Func<T, CancellationToken, Task>? Combine<T>(
        Func<T, CancellationToken, Task>? a,
        Func<T, CancellationToken, Task>? b) =>
        (a, b) switch
        {
            (null, null) => null,
            (not null, null) => a,
            (null, not null) => b,
            _ => async (ctx, ct) => { await a(ctx, ct); await b(ctx, ct); },
        };

    private static Func<PreToolUseHookContext, CancellationToken, Task<PreToolUseDecision>>?
        CombinePreToolUse(
            Func<PreToolUseHookContext, CancellationToken, Task<PreToolUseDecision>>? a,
            Func<PreToolUseHookContext, CancellationToken, Task<PreToolUseDecision>>? b) =>
        (a, b) switch
        {
            (null, null) => null,
            (not null, null) => a,
            (null, not null) => b,
            _ => async (ctx, ct) =>
            {
                var [da, db] = await Task.WhenAll(a(ctx, ct), b(ctx, ct));
                // Deny beats Rewrite beats Allow
                if (da is PreToolUseDecision.Deny || db is PreToolUseDecision.Deny)
                    return da is PreToolUseDecision.Deny ? da : db;
                if (da is PreToolUseDecision.Rewrite)
                    return da;
                return db;
            },
        };
}
```

Usage:

```csharp
AgentHooks hooks = AuditHooks.Default
    .Compose(BlockListHooks.Dangerous)
    .Compose(MetricsHooks.Default);

var agent = new Agent(llm, model, hooks: hooks);
```

---

## Built-in Hook Recipes

### Audit Logging

```csharp
public static class AuditHooks
{
    public static AgentHooks ForLogger(ILogger logger) => new()
    {
        OnPreToolUse = (ctx, _) =>
        {
            logger.LogInformation(
                "PreToolUse: {Tool} input={Input}",
                ctx.ToolName, ctx.Input);
            return Task.FromResult(PreToolUseDecision.Allowed);
        },
        OnPostToolUse = (ctx, _) =>
        {
            logger.LogInformation(
                "PostToolUse: {Tool} error={IsError}",
                ctx.ToolName, ctx.Result.IsError);
            return Task.CompletedTask;
        },
    };
}
```

### Dangerous-Command Block-List

Mirrors the Claude Code pattern of blocking `rm -rf`, `DROP TABLE`, etc.

```csharp
public static class BlockListHooks
{
    private static readonly string[] _dangerous =
        ["rm -rf", "drop table", "format c:", "del /f /s"];

    public static AgentHooks Dangerous { get; } = new()
    {
        OnPreToolUse = (ctx, _) =>
        {
            if (ctx.ToolName is not ("Bash" or "ExecutePowershell"))
                return Task.FromResult(PreToolUseDecision.Allowed);

            string? command = ctx.Input.TryGetProperty("command", out var prop)
                ? prop.GetString()
                : null;

            if (command is not null &&
                _dangerous.Any(d => command.Contains(d, StringComparison.OrdinalIgnoreCase)))
            {
                return Task.FromResult<PreToolUseDecision>(
                    new PreToolUseDecision.Deny($"Blocked: '{command}' matches dangerous pattern."));
            }

            return Task.FromResult(PreToolUseDecision.Allowed);
        },
    };
}
```

### Context Injection on Session Start

Equivalent to Claude Code's `SessionStart` → `additionalContext` pattern: re-injects fresh project state every session.

```csharp
public static AgentHooks WithProjectContext(string projectRoot) => new()
{
    OnSessionStarted = async (ctx, ct) =>
    {
        string? branch = await Git.CurrentBranchAsync(projectRoot, ct);
        string? status = await Git.StatusSummaryAsync(projectRoot, ct);

        // Inject into KnowledgeContext so SystemPromptBuilder picks it up
        ctx.AgentContext.Knowledge = ctx.AgentContext.Knowledge with
        {
            Facts = [.. ctx.AgentContext.Knowledge.Facts,
                $"Current git branch: {branch}",
                $"Working tree status: {status}"],
        };
    },
};
```

### Budget-Guard on Stop

Warn when the agent is about to return a result that consumed more than a cost threshold:

```csharp
public static AgentHooks WithBudgetAlert(decimal maxUsd, ILogger logger) => new()
{
    OnStop = (ctx, _) =>
    {
        if (ctx.Result.TotalCostUsd > maxUsd)
        {
            logger.LogWarning(
                "Agent run exceeded budget. Cost={Cost:C4}, Limit={Limit:C4}",
                ctx.Result.TotalCostUsd, maxUsd);
        }
        return Task.CompletedTask;
    },
};
```

### Input Rewriting (Path Normalisation)

Equivalent to Claude Code's `updatedInput` — transparently rewrite tool arguments before they reach the implementation:

```csharp
public static AgentHooks NormaliseFilePaths(string baseDir) => new()
{
    OnPreToolUse = (ctx, _) =>
    {
        if (!ctx.Input.TryGetProperty("path", out var pathProp))
            return Task.FromResult(PreToolUseDecision.Allowed);

        string? raw = pathProp.GetString();
        if (raw is null || Path.IsPathRooted(raw))
            return Task.FromResult(PreToolUseDecision.Allowed);

        string absolute = Path.GetFullPath(raw, baseDir);
        using var doc = JsonDocument.Parse(ctx.Input.GetRawText());
        // Clone JSON, overwrite "path"
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ctx.Input)!;
        dict["path"] = JsonSerializer.SerializeToElement(absolute);
        var rewritten = JsonSerializer.SerializeToElement(dict);
        return Task.FromResult<PreToolUseDecision>(new PreToolUseDecision.Rewrite(rewritten));
    },
};
```

---

## Relationship to `AgentEvents`

| | `IAsyncEnumerable<AgentEvent>` | `AgentHooks` |
|---|---|---|
| **Contract** | Observer (read-only) | Interceptor (can block/modify) |
| **Timing** | After the fact | Before *and* after |
| **Blocking** | Never | `PreToolUse.Deny` blocks tool |
| **Mutation** | Cannot change agent state | `Rewrite` changes tool input; `OnSessionStarted` can mutate `KnowledgeContext` |
| **Best for** | UI updates, telemetry, streaming output to callers | Policy enforcement, audit, input sanitisation |

Both mechanisms are complementary. Use `AgentEvents` to drive UI or forward telemetry to consumers. Use `AgentHooks` to enforce policy that must fire *before* the action happens.

---

## Configuration via `AgentOptions`

For host applications (e.g., `Agency.Agentic.Console`), hooks can be registered alongside other options:

```csharp
services.AddSingleton<AgentHooks>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<AgentHooks>>();
    return AuditHooks.ForLogger(logger)
        .Compose(BlockListHooks.Dangerous)
        .Compose(WithBudgetAlert(0.50m, logger));
});
```

Then inject into the `Agent` factory:

```csharp
services.AddTransient<Agent>(sp =>
{
    var options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
    var hooks = sp.GetService<AgentHooks>() ?? AgentHooks.None;
    var llm = /* resolve IChatClient */;
    return new Agent(llm, options.DefaultModel, hooks: hooks);
});
```

---

## Summary of New Files

| File | Purpose |
|---|---|
| `Hooks/AgentHooks.cs` | `AgentHooks` record with all delegates |
| `Hooks/HookContexts.cs` | `SessionStartedHookContext`, `PreToolUseHookContext`, `PostToolUseHookContext`, `AssistantTurnHookContext`, `StopHookContext` |
| `Hooks/PreToolUseDecision.cs` | `Allow`, `Deny`, `Rewrite` discriminated union |
| `Hooks/AgentHooksExtensions.cs` | `Compose()` extension method |

All hook types live in the `Agency.Agentic.Hooks` namespace. `Agent.cs` takes `AgentHooks?` in its constructor; the rest of the public API is unchanged.
