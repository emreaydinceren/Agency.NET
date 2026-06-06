# Permission Model Design Document

Status: **Proposed** · Branch context: `feat/configured-hooks` · Scope: `Agency.Harness`, `Agency.Harness.Console`

This document specifies a permission layer for the Agency agent harness: every tool and MCP call the agent attempts is intercepted and checked against configuration-based allow/deny rules, and unresolved calls surface to the user as **first-class events in the agent's response stream** — the turn parks, the host renders the request however it likes, and the turn resumes with the user's answers. It is written to be implemented from directly — type signatures, algorithms, config schemas, an ordered implementation plan, and test specifications are all normative.

## 1. Overview & Goals

### 1.1 Problem

Today the only gate on tool execution is the `OnPreToolUse` hook (`PreToolUseDecision`: `Allow` / `Deny(Reason)` / `Rewrite(NewInput)`). Hooks are **operator policy** — code or external handlers wired in by whoever deploys the agent. There is no **user consent** layer: nothing asks the person driving the session "the agent wants to run `Remove-Item -Recurse` — allow it?".

### 1.2 Design decisions (settled)

1. **Separate permission layer**, not built on hooks. A `PermissionEvaluator` runs in `Agent.cs` tool dispatch *after* `OnPreToolUse` hooks and *before* `ToolRegistry.InvokeAsync`.
2. **Configuration-based allow/deny rules** with Claude Code-style syntax: `ReadFile`, `ExecutePowershell(git status*)`, `WriteFile(E:/secrets/**)`, `mcp__gitea__list_*`.
3. **Unmatched calls ask the user** — safe by default. Allow rules exist to reduce prompting, not to enable tools.
4. **Asks are prompt objects in the event stream, not a host callback.** Unresolved calls yield `PermissionRequestedEvent`s, the turn ends with `AgentResultStatus.AwaitingPermission`, and the host answers via a resume API. The host never implements a harness interface for prompting — it handles an event, exactly as it handles every other `AgentEvent`. (This supersedes an earlier `IPermissionPrompt` awaited-callback design; see §2.2 for the rationale.)
5. **"Always" answers persist** to `permissions.local.json` via a momentary exclusive file lock with millisecond backoff. **No merge logic** — last writer wins under the lock.
6. **Hooks can escalate.** `PreToolUseDecision` gains an `Ask(Reason)` variant so operator policy can demand user confirmation even for calls the rules would allow — mirroring Claude Code's `permissionDecision: "ask"` hook verdict. Deny still beats ask; allow rules cannot clear a hook ask (§3.5).

### 1.3 Reference model

The rule pipeline mirrors the Claude Agent SDK; the ask transport mirrors what Claude Code does at every *remote* boundary (SDK stdio `control_request`/`control_response` messages travel in the NDJSON stream):

| Claude Code / Agent SDK | Agency equivalent |
| --- | --- |
| `PreToolUse` hooks | `OnPreToolUse` hooks (existing) |
| Hook `permissionDecision: "ask"` + `permissionDecisionReason` | `PreToolUseDecision.Ask(Reason)` (§3.5) |
| Deny rules (`permissions.deny`) | `Permissions:Deny` + persisted deny grants |
| Allow rules (`permissions.allow`) | `Permissions:Allow` + persisted allow grants |
| `control_request` in the message stream | `PermissionRequestedEvent` in the event stream |
| `control_response` from the client | `ChatSession.ResumeWithPermissionsAsync(...)` |
| `updatedPermissions` → `settings.local.json` | "Always" answers → `permissions.local.json` |

Permission *modes* (`acceptEdits`, `bypassPermissions`, `plan`) are deliberately out of scope; `Permissions:Enabled = false` is the only global switch.

## 2. Architecture

### 2.1 The protocol symmetry

The design applies the LLM↔harness protocol one layer up. The LLM does not call into the harness — it *returns* structured tool-call requests and stops; the harness executes them and continues the conversation. Symmetrically, the harness does not call into the host — it *yields* structured permission requests and parks; the host answers them and resumes the turn:

```text
LLM ↔ Harness (existing):
  LLM:     assistant message = [ text, tool_use(WriteFile), tool_use(ReadFile) ]
  Harness: executes tools → sends back [ tool_result, tool_result ]
  LLM:     continues.

Host ↔ Harness (this design):
  Harness: event stream = [ ToolInvokedEvent(ReadFile),                 ← allowed sibling ran
                            PermissionRequestedEvent(id, WriteFile, …),
                            AgentResultEvent(AwaitingPermission) ]       ← turn parks
  Host:    renders request(s) → user answers
           → ResumeWithPermissionsAsync([ (id, AllowAlways) ])
  Harness: executes WriteFile, completes the batch, loop continues.
```

The harness is to the host what the LLM is to the harness: a producer of structured requests, never a caller of host code.

### 2.2 Why events, not an awaited host callback

The obvious alternative — an `IPermissionPrompt` interface injected via DI, awaited by the evaluator mid-turn (the Claude Code-internal `canUseTool` shape) — was designed in full and rejected for this codebase:

- **One interaction channel.** With a callback, hosts integrate twice: the event stream out, plus a sideways prompt service that bypasses the host's rendering loop (in the console this forced spinner stop/start gymnastics and out-of-order prompt rendering). With events, the prompt is just another `case` in the loop the host already has.
- **The roadmap.** Harness state persistence (blob store) is the planned next project, and a stateless Web host after that. An awaited callback keeps the pending question alive only in process memory at *human* timescale — the exact thing persistence is meant to fix — and converting callback→checkpoint later means demolishing the callback machinery (including its prompt-serialization semaphore, which exists only to protect a console UI). The event model is the end-state contract from day one; persistence later makes parked turns durable without changing it.
- **Precedent.** Claude Code itself converts its internal callback into stream messages at every process boundary (SDK `control_request`). This design makes the stream message the native contract instead of the disguise.

The trade-off accepted: parking a turn mid-batch touches `Agent.RunAsync` semantics (held results, a resume entry point) — more core-loop work than inserting one `await`. §6 specifies it precisely.

### 2.3 Evaluation pipeline

```text
LLM emits tool call batch
        │  (per call, in parallel)
        ▼
┌─────────────────────────────┐
│ 1. OnPreToolUse hooks        │  Allow / Deny / Rewrite / Ask (NEW)
│    (operator policy)         │  Rewrite replaces `input`;
│                              │  Ask flags the call for user confirmation
└──────────────┬───────────────┘
               ▼
┌─────────────────────────────┐
│ 2. PermissionEvaluator (NEW) │  pure decision, never blocks, never renders
│    a. deny rules   → Deny    │  config ∪ persisted ∪ session grants
│    b. allow rules  → Allow   │  (cannot clear a hook Ask)
│    c. unresolved   → Ask     │  (or Deny when OnUnresolved=Deny — headless)
└──────────────┬───────────────┘
               ▼
   Allow → 3. ToolRegistry.InvokeAsync (existing)
   Deny  →    "[Blocked] {reason}" result
   Ask   →    call is PENDED — not executed, no result yet
        │
        ▼  after the batch settles (Task.WhenAll)
   any pended calls?
     no  → append results, continue loop (unchanged behavior)
     yes → yield PermissionRequestedEvent per pended call
           yield AgentResultEvent(AwaitingPermission)
           park: completed results + pended calls held in Context
           … host answers …
           ResumeWithPermissionsAsync → execute/deny pended calls,
           append the FULL batch results, continue loop
```

Precedence within the evaluator: **deny > allow > ask**. A deny rule can never be overridden by an allow rule or a user answer in the same evaluation.

The combined per-call order across both layers:

1. Hook `Deny` → blocked (evaluator never consulted).
2. Rule deny → blocked (a deny rule denies even a hook-`Ask`-flagged call — deny always wins).
3. Hook `Ask` → pended, **even when an allow rule matches** — operator escalation cannot be cleared by config. With `OnUnresolved: Deny` (headless) it fails closed instead.
4. Rule allow → executes.
5. Unresolved → pended (or denied per `OnUnresolved`).

### 2.4 Insertion point in `Agent.RunAsync`

The gate is inserted inside the per-call lambda in `src\Harness\Agency.Harness\Agent.cs` (currently lines 384–451), **after** the `OnPreToolUse` block — so it evaluates post-`Rewrite` input, i.e. what will actually execute — and **before** the `toolActivity` / `InvokeAsync` block. `Agent` gains one optional constructor parameter (after `hooks`): `IPermissionEvaluator? permissions = null`, stored as `_permissions`. When null the rules layer is absent and behavior is unchanged — *unless a hook returns the new `Ask` variant* (§3.5), since park/resume is agent machinery and works without an evaluator. Permissions are opt-in by construction.

A key existing invariant makes parking cheap: result messages for a batch are appended **all-or-nothing after `Task.WhenAll`** (`Agent.cs:466-469`, one Tool-role message per `FunctionResultContent`). Parking holds that append until the batch is truly complete, leaving the conversation in the same legal intermediate state the LLM protocol itself uses between `tool_use` and `tool_result`.

### 2.5 Contract to the LLM

Denials reuse the existing `[Blocked] {reason}` / `IsError: true` shape produced by hook denies, so the model sees one consistent denial contract. Deny reason strings:

| Source | Reason fed to the LLM |
| --- | --- |
| Rule deny | `Permission rule 'WriteFile(E:/secrets/**)' denies this call.` (raw rule text) |
| User deny, no message | `The user denied permission for this tool call.` |
| User deny with message | `The user denied permission for this tool call: {message}` |
| Abandoned (new user message while parked) | `The user did not respond to the permission request.` |
| Hook ask under `OnUnresolved: Deny` (headless) | `A hook required user confirmation, but this session cannot ask: {hook reason}` |

The deny-with-message path lets the user steer the model ("use the dev database instead") and the model can adjust and retry.

## 3. Type Specifications

Core types live in a new folder `src\Harness\Agency.Harness\Permissions\`, namespace `Agency.Harness.Permissions`; event types join the existing `AgentEvents.cs` family in `Agency.Harness`. Visibility follows the repo convention: public only for contracts hosts consume; everything else internal with the existing `[InternalsVisibleTo("Agency.Harness.Test")]`.

### 3.1 Events and the resume contract (public)

```csharp
// AgentEvents.cs — new event alongside ToolInvokedEvent etc.
/// <summary>Emitted for each tool call that needs user permission. The turn ends with
/// <see cref="AgentResultStatus.AwaitingPermission"/> after all such events are yielded.</summary>
public sealed record PermissionRequestedEvent(
    Guid RequestId,
    string ToolName,
    JsonElement Input,
    string? KeyValue,          // extracted key field (command/path) for concise display; null when none
    string ProposedRule,       // rule persisted if the answer is an "always", e.g. "ExecutePowershell(git status)"
    PermissionRequestSource Source,  // UnresolvedRule | Hook — hosts adapt rendering (§9.2)
    string? Reason)            // hook-supplied reason when Source == Hook; null otherwise
    : AgentEvent;

/// <summary>Why a tool call is asking: no rule matched, or a hook escalated.</summary>
public enum PermissionRequestSource
{
    UnresolvedRule,
    Hook,
}

// AgentResultStatus — new member
public enum AgentResultStatus
{
    Success,
    MaxStepsReached,
    BudgetExceeded,
    Error,
    /// <summary>The turn is parked: one or more tool calls await user permission.
    /// Answer via <see cref="ChatSession.ResumeWithPermissionsAsync"/>.</summary>
    AwaitingPermission,
}

// Permissions\PermissionResponse.cs
/// <param name="Message">Optional user-supplied reason on a deny; fed back to the LLM.</param>
public sealed record PermissionResponse(Guid RequestId, PermissionResponseKind Kind, string? Message = null);

public enum PermissionResponseKind
{
    AllowOnce,
    AllowAlways,
    DenyOnce,
    DenyAlways,
}
```

Resume API:

```csharp
// ChatSession.cs — new member
/// <summary>Resumes a turn parked with <see cref="AgentResultStatus.AwaitingPermission"/>.
/// Every pending <see cref="PermissionRequestedEvent.RequestId"/> must have exactly one response.
/// Streams the remainder of the turn (completed-batch ToolInvokedEvents, then the loop continues).</summary>
/// <exception cref="InvalidOperationException">No turn is parked.</exception>
/// <exception cref="ArgumentException">Responses are missing, duplicated, or unknown.</exception>
public IAsyncEnumerable<AgentEvent> ResumeWithPermissionsAsync(
    IReadOnlyList<PermissionResponse> responses, CancellationToken ct = default);

// Agent.cs — new member, called by ChatSession
internal IAsyncEnumerable<AgentEvent> ResumeAsync(
    Context ctx, IReadOnlyList<PermissionResponse> responses, AgentOptions? options = null, CancellationToken ct = default);
```

### 3.2 Evaluator contracts (public interface, internal implementation)

```csharp
// Permissions\IPermissionEvaluator.cs
public interface IPermissionEvaluator
{
    /// <summary>Pure decision — never blocks, never renders, never talks to the user.</summary>
    PermissionDecision Evaluate(string toolName, JsonElement input);

    /// <summary>Records an "always" answer: adds a session grant and appends to the local rules file.</summary>
    Task RecordAlwaysAsync(string proposedRule, bool deny, CancellationToken ct);
}

// Permissions\PermissionDecision.cs — mirrors PreToolUseDecision's nested-record style
public abstract record PermissionDecision
{
    public sealed record Allow : PermissionDecision;
    public sealed record Deny(string Reason) : PermissionDecision;
    /// <summary>No rule matched; the call must be surfaced to the user.</summary>
    public sealed record Ask(string? KeyValue, string ProposedRule) : PermissionDecision;

    public static PermissionDecision Allowed { get; } = new Allow();

    private PermissionDecision() { }
}
```

`Evaluate` is synchronous: rule matching is in-memory regex work; the only I/O (`RecordAlwaysAsync`) happens on the resume path. The evaluator builds `ProposedRule` (exact-value match, §4.4) so rule-syntax knowledge stays in one place; hosts only render and answer.

### 3.3 Internal types

```csharp
// Permissions\PermissionRule.cs
internal sealed class PermissionRule
{
    internal string Raw { get; }            // original config string; used in deny reasons
    internal string ToolPattern { get; }    // may contain '*' (e.g. "mcp__gitea__list_*")
    internal string? InputPattern { get; }  // null = bare tool rule, matches any input

    internal static PermissionRule Parse(string text);                       // FormatException on malformed
    internal static bool TryParse(string text, out PermissionRule? rule);
    internal bool Matches(string toolName, string? keyValue);
}

// Permissions\PermissionsOptions.cs — bound from the "Permissions" config section
internal sealed class PermissionsOptions
{
    public bool Enabled { get; set; } = true;                  // ops kill switch
    public string[] Allow { get; set; } = [];
    public string[] Deny { get; set; } = [];
    public UnresolvedBehavior OnUnresolved { get; set; } = UnresolvedBehavior.Ask;  // Deny for headless/CI
    public Dictionary<string, string> ToolInputKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? LocalRulesPath { get; set; }                // default: permissions.local.json next to the app
}

internal enum UnresolvedBehavior { Ask, Deny }

// Permissions\PermissionsOptionsValidator.cs — fail-fast at startup, mirrors HooksOptionsValidator
internal static class PermissionsOptionsValidator
{
    internal static void Validate(PermissionsOptions options);  // throws on malformed rules
}

// Permissions\PermissionEvaluator.cs
internal sealed class PermissionEvaluator : IPermissionEvaluator
{
    public PermissionEvaluator(PermissionsOptions options, ILogger<PermissionEvaluator>? logger = null);
    // State:
    //   List<PermissionRule> _configAllow, _configDeny   — immutable after ctor
    //   List<PermissionRule> _grantedAllow, _grantedDeny — lock-guarded; seeded from PermissionsFileStore.Load()
    //   PermissionsFileStore _store;
}

// Permissions\PermissionsFileStore.cs
internal sealed class PermissionsFileStore
{
    internal PermissionsFileStore(string path);
    internal (List<PermissionRule> Allow, List<PermissionRule> Deny) Load();  // missing file => empty lists
    internal void Append(string rule, bool deny);                             // exclusive open + retry/backoff (§8)
}

// Contexts\Context.cs — new member holding parked-turn state (serialization target for the blob project)
internal sealed class PendingToolBatch
{
    internal int Iteration { get; init; }
    internal FunctionResultContent?[] Results { get; init; }       // completed siblings, indexed by batch position
    internal List<PendingToolCall> Pending { get; init; }
}

internal sealed record PendingToolCall(
    Guid RequestId, int BatchIndex, string CallId, string ToolName,
    JsonElement Input,            // post-Rewrite — what will execute on approval
    string? KeyValue, string ProposedRule,
    PermissionRequestSource Source, string? Reason);
```

### 3.4 File layout summary

| File | Type(s) | Visibility |
| --- | --- | --- |
| `AgentEvents.cs` | `PermissionRequestedEvent`, `PermissionRequestSource`, `AgentResultStatus.AwaitingPermission` | public (existing file) |
| `Hooks\PreToolUseDecision.cs` | `PreToolUseDecision.Ask(string? Reason)` added | public (existing file) |
| `Hooks\Configuration\Handlers\HookHandlerOutput.cs` | `"ask"` decision value mapped | internal (existing file) |
| `Permissions\PermissionResponse.cs` | `PermissionResponse`, `PermissionResponseKind` | public |
| `Permissions\IPermissionEvaluator.cs` | `IPermissionEvaluator` | public |
| `Permissions\PermissionDecision.cs` | `PermissionDecision` + nested `Allow`/`Deny`/`Ask` | public |
| `Permissions\PermissionRule.cs` | `PermissionRule` | internal |
| `Permissions\PermissionsOptions.cs` | `PermissionsOptions`, `UnresolvedBehavior` | internal |
| `Permissions\PermissionsOptionsValidator.cs` | `PermissionsOptionsValidator` | internal |
| `Permissions\PermissionEvaluator.cs` | `PermissionEvaluator` | internal sealed |
| `Permissions\PermissionsFileStore.cs` | `PermissionsFileStore` | internal sealed |
| `Permissions\PermissionServiceCollectionExtensions.cs` | `AddAgencyPermissions` | public static |
| `Contexts\Context.cs` | `PendingToolBatch`, `PendingToolCall` members | internal (existing file) |

The console host adds **no new harness-facing types** — it handles `PermissionRequestedEvent` in `ConsoleChatSession`'s existing event loop (§9.2).

### 3.5 Hook escalation — `PreToolUseDecision.Ask`

The existing hook decision union gains a fourth variant (mirroring Claude Code's `permissionDecision: "ask"` / `permissionDecisionReason`):

```csharp
// Hooks\PreToolUseDecision.cs — addition to the existing union
public sealed record Ask(string? Reason) : PreToolUseDecision;
```

- **Configured hooks**: external handler output (stdout JSON / HTTP response) accepts `{ "decision": "ask", "reason": "..." }`; `HookHandlerOutput` maps it to `Ask(reason)`.
- **Aggregation precedence** (multiple matching handlers / folded hook sources): `Deny > Ask > Rewrite > Allow`. Deny-wins is preserved; ask outranks silence. When several handlers return `Ask`, the first non-null reason is kept.
- **Semantics**: a hook `Ask` flags the call for user confirmation. A deny rule still denies it (deny beats ask); an allow rule does **not** clear it — operator escalation cannot be overridden by config. The flagged call is pended exactly like a rules-unresolved call, with `Source = Hook` and the hook's reason on the event.
- **Recurrence**: hook asks recur by design — persisting an allow rule cannot suppress a future hook `Ask` (rules cannot override the operator). `DenyAlways` *does* work (the persisted deny rule wins before the ask matters). Hosts should therefore hide *Allow always* for `Source == Hook` requests (§9.2). The harness itself accepts all four response kinds regardless of source.
- **Without an evaluator**: hook asks pend even when `AddAgencyPermissions` was never called — park/resume lives in `Agent`. In that case `KeyValue` is null and `ProposedRule` is the bare tool name; "always" answers are accepted but recorded nowhere (no evaluator, no grants) — hosts running hooks-only should offer only the *once* answers.
- Adding a variant to the closed union breaks exhaustive `switch`es over `PreToolUseDecision` — a deliberate compile-time signal. `docs\How Hooks Work.md` must be updated alongside the implementation.

## 4. Rule Syntax Specification

### 4.1 Grammar

```text
rule        := tool-pattern | tool-pattern "(" input-pattern ")"
tool-pattern  := identifier with optional '*' wildcards   e.g. ReadFile, mcp__gitea__list_*
input-pattern := any text with '*' wildcards              e.g. git status*, E:/secrets/**
```

- **Bare rule** (`SomeTool`): matches any invocation of that tool regardless of input.
- **Parameterized rule** (`SomeTool(pattern)`): matches only when the extracted key value (§4.3) matches the pattern.
- `*` matches any character sequence (including path separators). `**` is accepted and treated **identically to `*`** — a documented simplification, *not* gitignore semantics (see §13, risk 2).
- Matching is `StringComparison.OrdinalIgnoreCase` (consistent with `HookMatcher`, and sane on Windows).
- Before matching, the candidate value is normalized `\` → `/`, so `WriteFile(E:/secrets/**)` matches `E:\secrets\key.txt`.
- MCP tools need no special handling: MCP proxy tools register under their `mcp__server__tool` definition names in the normal `ToolRegistry`, so `mcp__gitea__list_*` is just a tool-pattern wildcard.

### 4.2 Compilation strategy

At parse time each pattern is translated to an anchored, compiled `Regex`: escape everything (`Regex.Escape`), then replace one-or-more consecutive escaped `*` with `.*`, anchor with `^...$`. Apply `RegexOptions.IgnoreCase | RegexOptions.Compiled` and a 250 ms match timeout (mirrors `HookMatcher`'s defensive timeout). Malformed rules throw `FormatException` from `Parse`.

### 4.3 Key-field resolution — which input field does the pattern match?

Chosen approach: **config map with built-in defaults, plus a small convention fallback.**

1. `PermissionsOptions.ToolInputKeys` maps tool name → JSON property name. Built-in defaults are merged in the evaluator ctor:

   | Tool | Key field |
   | --- | --- |
   | `ExecutePowershell` | `command` |
   | `ReadFile` | `path` |
   | `WriteFile` | `path` |

   > Note: the built-in file tools use the input property **`path`** (see `ReadFileTool.cs` / `WriteFileTool.cs`), not `file_path`.

2. For unmapped tools, convention fallback: the first **present string property** among `command`, `path`, `file_path`, `url`.
3. If no key value is found: bare-tool rules still match; parameterized rules **never** match, so the call falls through to `Ask` — fail-safe.

Rationale: rules stay Claude-syntax-clean (no field name embedded in every rule); zero-config works for the three built-in tools; the config map is the escape hatch for MCP tools (e.g. `"ToolInputKeys": { "mcp__gitea__get_file_contents": "filepath" }`). Alternatives rejected: rules carrying the field name (noisier, non-Claude-compatible) and "match any string field" (a deny pattern could accidentally match `content` instead of `path` — a correctness hazard).

### 4.4 Proposed rule construction

The evaluator builds `ProposedRule` for "always" persistence when returning `Ask`:

- Key value found → `ToolName(exactKeyValue)` — exact match, no wildcards. (Wildcard widening is a future UI concern.)
- No key value → bare `ToolName`.

## 5. Evaluator Algorithm

```text
Evaluate(toolName, input):
  1. if !options.Enabled                         -> Allow
  2. keyValue = ExtractKeyValue(toolName, input) // ToolInputKeys map -> convention -> null
  3. if any deny rule matches (toolName, keyValue)
        [search: configDeny ∪ grantedDeny]       -> Deny("Permission rule '{rule.Raw}' denies this call.")
  4. if any allow rule matches
        [search: configAllow ∪ grantedAllow]     -> Allow
  5. unresolved:
        OnUnresolved == Deny                     -> Deny("No permission rule allows this call.")   // headless/CI
        OnUnresolved == Ask                      -> Ask(keyValue, BuildProposedRule(toolName, keyValue))
```

- Steps 3–4 read the immutable config lists lock-free plus a short `lock` over the granted lists.
- The evaluator never blocks, never performs I/O on this path, and is safe to call from the parallel tool-batch tasks with no extra synchronization.
- `RecordAlwaysAsync(proposedRule, deny)` — called only from the resume path (§6.3) — parses the rule, adds it to the granted list, and calls `_store.Append`. Append failures are logged and swallowed: the session grant still applies; persistence is best-effort.

## 6. Turn Lifecycle — Park and Resume

This is the core new mechanism. It replaces both the awaited prompt and the prompt-serialization concurrency design of the earlier draft.

### 6.1 The batch with pended calls

Inside the per-call lambda (§2.4), an ask from **either layer** — a hook returning `PreToolUseDecision.Ask` (unless a deny rule kills the call first) or the evaluator returning `PermissionDecision.Ask` — marks the call **pended**: no execution, no `FunctionResultContent`, no `OnPostToolUse`. The call records a `PendingToolCall` (fresh `RequestId`, batch index, `CallId`, post-`Rewrite` input, `KeyValue`, `ProposedRule`, `Source`, hook `Reason` if any). Allowed and rule-denied siblings execute exactly as today, in parallel, to completion.

After `Task.WhenAll`:

- **No pended calls** → unchanged behavior: yield `ToolInvokedEvent`s, fire `OnPostToolBatch`, append result messages, next iteration.
- **Pended calls exist** →
  1. Yield `ToolInvokedEvent`s for the completed siblings (the host sees what already ran).
  2. Yield one `PermissionRequestedEvent` per pended call — *all* of them, enabling consolidated rendering ("3 actions need approval").
  3. Store `PendingToolBatch { Iteration, Results (completed siblings), Pending }` on the `Context`.
  4. Yield `AgentResultEvent(AwaitingPermission, FinalText: null, usage, cost)` and end the enumerable.
  5. **Do not** append any result messages and **do not** fire `OnPostToolBatch` — the batch is incomplete. The conversation rests in the same legal state the LLM protocol uses between `tool_use` and `tool_result`: an assistant message awaiting its results.

### 6.2 Message-ordering invariant

Result messages for a batch are appended all-or-nothing, in batch order, only when the batch is complete (today: `Agent.cs:466-469`; parked: on resume). Partial appends are never legal — an assistant `tool_use` answered by only some results is a malformed conversation for every provider.

### 6.3 Resume

`ChatSession.ResumeWithPermissionsAsync(responses, ct)` → `Agent.ResumeAsync(ctx, responses, options, ct)`:

```text
ResumeAsync(ctx, responses):
  1. ctx.PendingToolBatch is null            -> InvalidOperationException
  2. validate: exactly one response per pending RequestId, no unknown ids -> ArgumentException
  3. for each response with Kind in { AllowAlways, DenyAlways }:
        await evaluator.RecordAlwaysAsync(pending.ProposedRule, deny: Kind == DenyAlways)
  4. for each pending call (parallel, same as a normal batch):
        AllowOnce | AllowAlways -> InvokeAsync(pending.ToolName, pending.Input)   // hooks already ran pre-park
                                   then OnPostToolUse
        DenyOnce  | DenyAlways  -> ToolResult("[Blocked] {user deny reason}", IsError: true)
        yield ToolInvokedEvent per call as it completes
  5. merge into PendingToolBatch.Results by BatchIndex; fire OnPostToolBatch with the FULL batch
  6. append all result messages in batch order; clear ctx.PendingToolBatch
  7. continue the standard RunAsync loop from the next iteration (stop-condition check, LLM call, …)
```

Implementation note: extract the post-batch tail and the iteration loop of `RunAsync` into a shared private method so `RunAsync` and `ResumeAsync` cannot drift. `OnPreToolUse` hooks are **not** re-run on resume — they ran before parking and their `Rewrite` output is what was pended; re-running would double-apply policy.

### 6.4 Abandonment — `SendAsync` while parked

If the host calls `SendAsync(userMessage)` while a turn is parked (user typed something instead of answering), the session **implicitly denies all pending calls** with reason `The user did not respond to the permission request.`, completes the batch (step 5–6 above), appends the new user message, and runs the turn normally. This matches Claude Code's UX (new input cancels pending requests) and guarantees the conversation never wedges. `Reset()` clears any pending batch along with history.

### 6.5 Cancellation and timeouts

A parked turn runs no code — `TurnTimeoutSeconds` (applied per `ChatAsync`/`ResumeAsync` invocation via the linked CTS) never fires while parked, by construction. Resume gets a fresh timeout. Ctrl+C mid-park is a host concern: nothing is executing, so there is nothing to cancel; the host abandons via `SendAsync` or `Reset()`.

### 6.6 What stays in memory — and the persistence project

`PendingToolBatch` lives on `Context`, the same object that already holds cross-turn conversation history in `ChatSession`. In v1 a process restart loses a parked turn exactly as it loses the conversation — no regression, no new durability promise. The planned harness state-persistence project (blob store) serializes `Context`; `PendingToolBatch` and `PermissionRequestedEvent` are plain data (strings, `Guid`, `JsonElement`) and ride along, at which point a parked turn survives restarts and can resume **on any process or instance** — the stateless-web answer falls out of this design rather than requiring a second mechanism. The one extra requirement it places on that project: completed siblings' results are part of the checkpoint, so resume never re-executes side-effectful tools (idempotency).

## 7. Configuration

### 7.1 `Permissions` section (appsettings.json — new top-level section)

```json
"Permissions": {
  "Enabled": true,
  "Allow": [ "ReadFile", "ExecutePowershell(git status*)", "mcp__gitea__list_*" ],
  "Deny":  [ "WriteFile(E:/secrets/**)" ],
  "OnUnresolved": "Ask",
  "ToolInputKeys": { "mcp__gitea__get_file_contents": "filepath" },
  "LocalRulesPath": null
}
```

- `Enabled: false` short-circuits the evaluator to Allow (kill switch for ops).
- `OnUnresolved: "Deny"` makes unresolved calls fail closed instead of parking — for CI and unattended runs where nobody can answer (§9.3).
- `LocalRulesPath: null` → default `Path.Combine(AppContext.BaseDirectory, "permissions.local.json")`.
- Malformed rules in **appsettings fail fast** at startup via `PermissionsOptionsValidator` (consistent with `HooksOptionsValidator`).

### 7.2 `permissions.local.json` (user-grant persistence)

```json
{
  "Allow": [ "ExecutePowershell(git status)", "ReadFile(E:/Repos/Agency/**)" ],
  "Deny":  [ "WriteFile(E:/secrets/**)" ]
}
```

- Loaded **directly** by the evaluator ctor via `PermissionsFileStore.Load()` — *not* bound through `IConfiguration`. This keeps load and write-back symmetrical in one class and avoids configuration-reload semantics entirely.
- Malformed entries in this file are **logged and skipped** — a corrupt local file must not brick startup (it is machine-written, unlike appsettings).
- The file should be added to `.gitignore` (it is per-machine, like Claude Code's `settings.local.json`).

## 8. Persistence Design (`PermissionsFileStore`)

Per the settled decision: momentary exclusive file lock, millisecond backoff, **no merge logic**.

`Append(rule, deny)` algorithm:

1. Open `new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)` — exclusive handle.
2. On `IOException` (sharing violation): wait ~50 ms, retry; give up after ~10 attempts (log a warning; the session grant still holds).
3. Under the handle: read the current JSON (empty file → fresh document), append the one rule string to the `Allow` or `Deny` array (skip if already present), seek to start, truncate, write, flush, close.

Intra-process callers are serialized by the resume path (one resume at a time per session; `ChatSession` is documented single-flight); the file lock covers other processes. Within the exclusive handle the write is read-modify-write; across processes, last writer wins — accepted by design, no merging.

## 9. Host Integration

### 9.1 The host contract (any UI)

A host integrates by handling two things in the event loop it already has:

1. `PermissionRequestedEvent` — collect (there may be several per turn).
2. `AgentResultEvent` with `Status == AwaitingPermission` — the turn has parked: render the collected requests (tool name, key value or truncated input JSON, and the `ProposedRule` an "always" answer persists), gather one `PermissionResponse` per request, call `ResumeWithPermissionsAsync`, and iterate the returned stream exactly like a `SendAsync` stream.

There is nothing to implement, register, or inject. Rendering, ordering, batching of the questions ("approve all"), and answer UX are entirely host-owned.

### 9.2 Console host (`ConsoleChatSession`)

Changes are confined to the existing event loop in `ConsoleChatSession`:

- On `PermissionRequestedEvent`: add to a per-turn `List<PermissionRequestedEvent>`.
- On `AgentResultEvent(AwaitingPermission)`: the turn — and therefore the spinner — has already ended naturally (the stream completed); no spinner gymnastics. For each collected request: render a bordered panel via `output.WriteMarkdownInBorderedPanel("Permission required", ...)` (tool, key value or truncated input, proposed rule, and the hook `Reason` when `Source == Hook`), select via the existing `ConsolePicker.Show` — four rows (*Allow once / Allow always / Deny once / Deny always*) for rule-sourced requests, **three rows (no *Allow always*) for hook-sourced requests** since persisted allow rules cannot suppress a recurring hook ask (§3.5) — and on a deny optionally read one free-text line ("reason for the model, Enter to skip") — the REPL's `ConsoleInputReader` is idle between turns, so there is no input contention.
- Call `ResumeWithPermissionsAsync(responses)` and feed the resulting stream through the same rendering switch (it can park again if the continued turn triggers new asks — loop until a non-`AwaitingPermission` result).

Compared with the rejected callback design, the console gets simpler: no `ConsolePermissionPrompt` class, no mid-turn spinner stop/start, and prompts render *after* the batch's tool panels in natural stream order.

### 9.3 Headless / CI

Set `OnUnresolved: "Deny"` (or `Enabled: false` to disable the layer): unresolved calls fail closed with a deterministic `[Blocked]` reason instead of parking a turn nobody will resume. A headless host that leaves `OnUnresolved: Ask` and ignores `AwaitingPermission` will park sessions indefinitely — the doc-level contract is: **hosts that enable Ask must handle the status.**

### 9.4 Web host (future)

A thin browser client over a server-side session — the architecture Claude Code uses for web/remote (`src/remote/`, SSE/WebSocket transports; the session process owns state, the browser is a dumb terminal):

- `PermissionRequestedEvent`s and the `AwaitingPermission` result are pushed to the browser (SignalR/SSE) like every other event; the answer POST calls `ResumeWithPermissionsAsync`. Because the turn is parked data — not a suspended `await` — there is no pending continuation to protect, no `TaskCompletionSource` registry, no request affinity to a blocked call.
- Until the blob-persistence project lands, the parked `Context` lives in server memory (a restart loses the session — conversation and pending asks alike). After it lands, a parked turn is durable and resumable on any instance (§6.6) with **no change to this contract**.
- Multi-session: one `ChatSession` + one `IPermissionEvaluator` per user session. The evaluator is per-session state (session grants); register it scoped-per-session in a multi-session host, singleton in the console (§10).

### 9.5 Sub-agents (`AgentTool`)

A child agent runs inside one of the parent's tool tasks; if it parked, the parent's batch could never settle. v1 policy: **`AgentTool` is the child's host and auto-denies.** It consumes the child's event stream; on `PermissionRequestedEvent` + `AwaitingPermission` it immediately calls `ResumeWithPermissionsAsync` answering every request `DenyOnce` with message `Sub-agents cannot request permission; grant a rule to the parent session instead.` Child agents therefore operate on rules only — config allow/deny plus grants already recorded by the parent's user. Future work: forward child requests up through the parent's stream (cascading park) — explicitly out of scope (§13, risk 6).

## 10. DI Wiring

Mirrors `HookServiceCollectionExtensions.AddAgencyConfiguredHooks`:

```csharp
public static IServiceCollection AddAgencyPermissions(
    this IServiceCollection services, IConfiguration config, string sectionName = "Permissions")
{
    services.AddOptions<PermissionsOptions>().Bind(config.GetSection(sectionName));
    services.AddSingleton<IPermissionEvaluator>(sp =>
    {
        PermissionsOptions options = sp.GetRequiredService<IOptions<PermissionsOptions>>().Value;
        PermissionsOptionsValidator.Validate(options);            // fail fast on malformed rules
        return new PermissionEvaluator(options, sp.GetService<ILogger<PermissionEvaluator>>());
    });
    return services;
}
```

- **Agent wiring goes through `AgentFactory`**: it gains an `IPermissionEvaluator? permissions = null` ctor parameter and passes it to `new Agent(..., permissions: this.permissions)`. (The `PostConfigureOptions<AgentOptions>` route used by hooks was rejected — the evaluator is a stateful service, not a delegate bundle.)
- **`Program.cs` addition** (next to the existing `AddAgencyConfiguredHooks` call): `builder.Services.AddAgencyPermissions(builder.Configuration);` — nothing else; there is no prompt service to register.
- Singleton lifetime is correct for the console (one process = one session). A multi-session host registers the evaluator per-session (§9.4).

If `AddAgencyPermissions` is never called, `Agent` receives no evaluator and the harness behaves exactly as today.

## 11. Implementation Plan

Ordered work items, each with its verification step:

1. **`PermissionRule`** (parse, compile, match) → verify: `PermissionRuleTests` green.
2. **`PermissionsOptions` + `PermissionsOptionsValidator`** → verify: malformed rule strings fail at startup; valid config binds; `OnUnresolved` binds from string.
3. **`PermissionsFileStore`** (Load/Append, lock + backoff) → verify: `PermissionsFileStoreTests` green (incl. contention test).
4. **`PermissionDecision`, `PermissionResponse`, `PermissionRequestedEvent`, `AgentResultStatus.AwaitingPermission`** → verify: compiles; XML docs on public surface.
5. **`PermissionEvaluator`** (algorithm §5 + `RecordAlwaysAsync`) → verify: `PermissionEvaluatorTests` green.
6. **Hook `Ask` support** (`PreToolUseDecision.Ask`, handler JSON `"ask"` verdict, aggregation precedence `Deny > Ask > Rewrite > Allow`, §3.5) → verify: hook decision/aggregation tests green; `docs\How Hooks Work.md` updated.
7. **Park path in `Agent.RunAsync`** (ctor param, gate insertion §2.4, pended-call collection, `PendingToolBatch` on `Context`, AwaitingPermission emission §6.1) → verify: agent-level park tests green; existing hook tests untouched and green.
8. **`Agent.ResumeAsync` + `ChatSession.ResumeWithPermissionsAsync`** (§6.3) and abandonment in `SendAsync` (§6.4) → verify: resume/abandon tests green, incl. message-ordering assertions.
9. **`AgentTool` auto-deny** (§9.5) → verify: sub-agent test — child's unresolved call returns `[Blocked] Sub-agents cannot…`, parent batch completes.
10. **`AddAgencyPermissions` DI extension + `AgentFactory`/`Program.cs` wiring** → verify: resolves from a host builder; no-registration path unchanged.
11. **Console integration in `ConsoleChatSession`** (§9.2) → verify: manual run — agent proposes a `WriteFile`, panel renders after the batch's tool panels, all four answers behave; "always" appends to `permissions.local.json` and suppresses the next ask; a hook-flagged call shows the reason and offers no *Allow always*; typing a new message instead of answering abandons cleanly.
12. **Docs & config**: add `Permissions` section to `appsettings.json`, add `permissions.local.json` to `.gitignore` → verify: clean clone builds and runs with defaults.

## 12. Testing Strategy

All unit tests in `Agency.Harness.Test` (internals already visible via `AssemblyInfo.cs`).

### `PermissionRuleTests`

- Parse: bare name; parameterized; malformed (`Tool(`, empty, `()` only) → `FormatException`; `TryParse` false.
- Match: command prefixes (`git status*` vs `git status --short` / `git stash`); path globs with `\`→`/` normalization and case-insensitivity; tool wildcards (`mcp__gitea__list_*` matches `mcp__gitea__list_branches`, not `mcp__gitea__create_branch`); bare rule matches any input including null key value; parameterized rule never matches a null key value.

### `PermissionEvaluatorTests`

- Deny rule beats allow rule for the same call; allow rule wins over Ask; unresolved → `Ask` with correct `KeyValue`/`ProposedRule`; `OnUnresolved = Deny` → `Deny`; `Enabled = false` → `Allow`.
- Key extraction: `ToolInputKeys` map wins; convention fallback order; no key found → parameterized rules skipped, bare rules still apply.
- `RecordAlwaysAsync`: adds a session grant (subsequent `Evaluate` resolves without Ask); appends to a temp-path local file; deny grants beat config allow rules.
- Local file load: grants seeded at ctor; malformed entries skipped without throwing.

### Agent park/resume tests (with `FakeLlmClient` + `FakeTool`)

- **Park**: batch of 3 calls (1 allowed, 1 rule-denied, 1 unresolved) → 2 `ToolInvokedEvent`s, 1 `PermissionRequestedEvent`, terminal `AwaitingPermission`; no Tool-role messages appended; `OnPostToolBatch` not fired; unresolved tool never invoked.
- **Resume allow**: `AllowOnce` → tool invokes with the **post-`Rewrite`** input captured at park time; `OnPostToolUse` fires; full batch results appended in batch order; `OnPostToolBatch` fires once with the full batch; loop continues to the next LLM call.
- **Resume deny**: `DenyOnce` with message → `[Blocked] The user denied…: {message}` result, `IsError: true`, tool not invoked.
- **Always**: `AllowAlways` → grant recorded + file appended; an identical call in the *next* batch resolves without parking.
- **Validation**: resume with missing/unknown/duplicate `RequestId` → `ArgumentException`; resume with no parked turn → `InvalidOperationException`.
- **Abandonment**: `SendAsync` while parked → all pendings produce the abandoned `[Blocked]` reason, batch completes, new user message processed in the same call.
- **Multiple asks**: 2 unresolved calls → 2 `PermissionRequestedEvent`s before one `AwaitingPermission`; mixed answers resolve independently.
- **Hook ask**: hook returns `Ask("flagged")` + allow rule matches → call still pends with `Source = Hook`, `Reason = "flagged"`; hook `Ask` + deny rule → denied without parking; hook `Ask` under `OnUnresolved = Deny` → denied with the hook reason; hook aggregation — one handler `Deny` + one `Ask` → `Deny`; `AllowAlways` answered on a hook-sourced request → grant recorded, but the identical call in the next batch pends again (recurrence semantics).
- **No evaluator**: `Agent` constructed without permissions → byte-for-byte existing behavior (regression guard); hook `Ask` with no evaluator pends with `Source = Hook` (the park machinery is owned by the agent, not the evaluator).

### `PermissionsFileStoreTests`

- Append to a missing file creates it with the correct shape.
- Append while another stream holds an exclusive handle: backoff/retry succeeds after release.
- Duplicate rule append is a no-op.

### `AgentTool` sub-agent test

- Child agent hits an unresolved call → parent's tool result contains the sub-agent deny message; parent batch settles; parent turn does not park.

## 13. Risks, Limitations & Future Work

1. **Single-key prefix matching is shallow for shell commands.** `ExecutePowershell(git status*)` happily matches `git status; Remove-Item -Recurse E:\`. This is the same documented weakness as Claude Code's Bash prefix rules. Deny rules and asking remain the backstop; state this plainly to users.
2. **`**` is cosmetic.** Both `*` and `**` match across path separators. Compatible-looking but not gitignore-equivalent; a future tightening (`*` stops at `/`) would silently change rule behavior — call it out in any such change.
3. **A parked turn is in-memory until the persistence project lands.** Process restart loses it — together with the conversation it belongs to, so this is not a new class of loss. §6.6 defines what the blob checkpoint must include (completed sibling results — idempotent resume).
4. **Session grants survive `/clear` only if the evaluator does.** In the console the evaluator is a process singleton, so grants outlive `Reset()`. Acceptable; worth a line in user docs.
5. **Hook `Rewrite` runs before evaluation.** A configured hook can rewrite an input *into* an allowed pattern. Hooks are operator-trusted by definition; evaluating post-rewrite input (what actually executes) is the correct choice regardless. Hooks are deliberately not re-run on resume (§6.3).
6. **Sub-agents cannot ask in v1.** `AgentTool` auto-denies child permission requests (§9.5); deep agent trees degrade to rules-only. Future work: cascading park — forward child requests through the parent's event stream and route answers down.
7. **Identical pended calls are not deduplicated.** Two identical `WriteFile` calls in one batch yield two requests; an `AllowAlways` answer to one does not auto-answer the other within the same park (it will prevent future parks). Hosts may render them grouped; the harness keeps the contract strict: one response per `RequestId`.
8. **`permissions.local.json` is trusted input.** Anything that can write the file can self-grant. Same trust model as appsettings; do not point `LocalRulesPath` at a world-writable location.
9. **Hosts that enable Ask must handle `AwaitingPermission`.** An unaware host parks sessions forever. Mitigations: `OnUnresolved: Deny` for headless (§9.3); abandonment-on-`SendAsync` (§6.4) prevents wedging interactive hosts.
10. **Hook asks recur by design.** "Allow always" cannot suppress a future hook `Ask` — rules cannot override operator escalation (§3.5). `DenyAlways` does suppress (deny beats ask). Hosts must adapt rendering (`Source == Hook` → no *Allow always*); a host that ignores `Source` merely shows a misleading option, it cannot weaken security.

Future work (explicitly out of scope): permission modes (acceptEdits/bypass/plan analogues), rule widening in the answer UX ("allow all `git *`"), per-directory rule scoping, an `Ask` rule list mirroring Claude's `permissions.ask`, audit logging of decisions (compose with the existing `AuditHooks` instead), cascading park for sub-agents (risk 6), and durable parked turns via the planned harness state-persistence project (§6.6).
