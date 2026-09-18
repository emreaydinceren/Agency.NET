# Agency.Acp — Persona Identity and Dispatch Robustness (HLD)

**Status:** design.
**Supersedes:** nothing. **Amends:** [`Agency.Acp-Specifications.md`](Agency.Acp-Specifications.md) §6.2, §6.3, §8.1, §8.4.
**Driven by:** Agency.Huddle, `PRIVATE/Huddle/11-huddle-system-prompt-never-read.md` (2026-09-18), against `AgencyDotNet.Acp 0.1.195-ga1fc165f21`.
**Grilled decisions:** §14.5 records the three forks resolved in session.

---

## Table of Contents

1. [Goal](#1-goal)
2. [Example Use Cases](#2-example-use-cases)
3. [Non-Goals](#3-non-goals)
4. [Design Principles](#4-design-principles)
5. [Architecture Overview](#5-architecture-overview)
6. [System Components](#6-system-components)
7. [Data Model / State Layer](#7-data-model--state-layer)
8. [Core Algorithms](#8-core-algorithms)
9. [Per-Session vs Per-Turn Work](#9-per-session-vs-per-turn-work)
10. [Background Workers / Async Components](#10-background-workers--async-components)
11. [Performance Expectations](#11-performance-expectations)
12. [Edge Cases and Failure Modes](#12-edge-cases-and-failure-modes)
13. [End-to-End Flow](#13-end-to-end-flow)
14. [Design Notes / Rationale](#14-design-notes--rationale)
15. [Test-First Task Plan](#15-test-first-task-plan)

---

## 1. Goal

### 1.1 Primary goal

Make a **Persona** — a named agent with its own identity and its own tool surface — actually be itself when driven over ACP.

Today it cannot. `AgencyDotNet.Acp 0.1.195` never reads the Persona prompt the client sends, so every Persona on the adapter runs on the harness's baseline identity line. Two Personas are the same agent with different names on the tile.

### 1.2 The defect, precisely

The two sides are exactly crossed:

| `_meta` key | Huddle sends | `agency-acp` 0.1.195 reads |
|---|---|---|
| `systemPrompt` | ✅ the composed Persona prompt | ❌ |
| `model` | ❌ never | ✅ `MethodDispatcher.cs:158` |

Each side uses the slot the other ignores.

**How it fell through, recorded so it cannot recur.** The harness half shipped: `QueryContext.IdentityPrompt` exists (`Contexts/QueryContext.cs:18`), `SystemPromptBuilder.Build` consumes it (`SystemPromptBuilder.cs:25`), and `SystemPromptIdentityTests` covers it with four passing tests. The adapter half shipped: `session/new` implements spec §8.1 steps 1–8. **The hop between them — read `_meta.systemPrompt` off the request and put it in `QueryContext.IdentityPrompt` — belonged to neither task.** Delta D-3 specified the harness end; §8.1 specified `session/new` and reads only `model`. The joining line was never written down, and because nothing errors, no test and no manual run surfaces it: a `session/new` carrying a 2 KB Persona prompt succeeds, silently, exactly as one carrying nothing does.

This is a **specification defect before it is a code defect**, and §15 fixes both.

### 1.3 Concrete objectives

| # | Objective | Measure |
|---|---|---|
| **O-1** | A Persona's identity reaches the model on every iteration | `_meta.systemPrompt` text appears in the submitted system prompt of iterations 1 *and* 2 |
| **O-2** | Identity never costs the harness's scaffolding | ReAct, skills-catalogue and grounding sections survive verbatim alongside a custom identity |
| **O-3** | Two Personas on one process are distinguishable | Two sessions with different identities produce different system prompts; neither bleeds into the other |
| **O-4** | No handler fault can hang a client | Every unhandled exception from any handler yields a JSON-RPC error response, never silence |
| **O-5** | A vanilla `agency-acp` starts and serves a turn | `session/new` succeeds with no environment configuration supplied |
| **O-6** | No public API is removed | `PublicAPI.Unshipped.txt` gains only additions; zero `*REMOVED*` entries |

### 1.4 What this is not

This is **not** a new subsystem. It is a **four-point amendment** to a shipped adapter: one load-bearing data path, one dead-code removal, one bootstrap default, one error-contract closure. Its value is entirely in unblocking the joint milestone.

---

## 2. Example Use Cases

**U-1 — Ana routes, Kai writes code.**
Huddle opens two sessions against one `agency-acp` process, each carrying a different `_meta.systemPrompt`. Ana's turns are grounded in the router identity; Kai's in the coding identity. Neither session's identity is visible to the other.

**U-2 — A Persona discovers its tools.**
The Persona prompt names exactly one tool, `get_help`, which names the other six. With identity plumbed, the model reads `get_help` from its own identity text *and* reads the harness's ReAct block, which survives. It calls `get_help`, learns the rest, and can create a Room.

> **Correction (verified during implementation).** The progressive-discovery instruction is **not** present in an ACP session. `SystemPromptBuilder` gates it on `ctx.Tools.Registry is IProgressiveDiscovery`, and `SessionFactory` builds a plain `ToolRegistry`, which does not implement that interface — only `ProgressiveDiscoveryToolRegistry` does. The Persona therefore learns about `get_help` from its own identity text alone. This does not change the append-vs-replace decision (see §14.1), but it does mean the `tool_help` directive is not among the sections a replace would have deleted. **Huddle has since confirmed** their composed prompt is self-contained — it names `get_help`, says when to call it, and lists all seven tool names — so the priming was never load-bearing for them, and they have asked that progressive discovery stay off (§14.5, G-4).

**U-3 — A bare-string identity.**
A client sends `_meta.systemPrompt` as a plain string rather than `{"append": …}`. It is treated identically to `append`: the opening identity line is replaced, everything else survives.

**U-4 — A misconfigured client name.**
`Agent:DefaultClientName` matches no configured client, so `IAgentFactory.CreateAgent` throws. Today the client waits forever. After this change it receives `-32603 Internal error` naming the failure, and can surface it.

**U-5 — A vanilla launch.**
An operator who is not Huddle downloads the package and runs `agency-acp` with no environment at all. `session/new` succeeds against the shipped defaults instead of hard-failing on `Agent:DefaultModel is not configured`.

### 2.1 Composition example

```text
session/new(cwd, mcpServers:[team@http + bearer],
            _meta: { systemPrompt: { append: "You are Ana, who routes…" } })
   → sessionId, configOptions[model, effort]
session/set_config_option(model = "qwen/qwen3.6-35b-a3b")
session/prompt("[#product] can you summarise the thread?")
   ← agent_message_chunk  "Sure — let me check"
   ← tool_call            get_help        (pending)
   ← tool_call_update     get_help        (completed)
   ← agent_message_chunk  " — here's the gist:"
   → { stopReason: "end_turn" }
```

The only change from 0.1.195 is the `_meta` argument on line 1 — and it is the difference between Ana and a nameless agent.

---

## 3. Non-Goals

| Not doing | Why |
|---|---|
| **Full system-prompt replacement** | §I1 forbids a Replace mode. A true replace deletes the ReAct instruction, the skills catalogue and grounding. See §14.1. (The `tool_help` directive is *not* emitted in an ACP session — see the correction under U-2.) |
| A general `_meta` extension registry | Two keys are in play, one of which is being deleted. An extensibility framework for a single consumer is an abstraction for single-use code. V2 if a third key appears. |
| Per-session `UserId` | Still V2, still needs a carrier, still out of scope. `_meta` remains its most plausible home (base spec §6.3). |
| Changing `SystemPromptBuilder`'s section order or content | D-3 landed and is tested. This work only supplies its input. |
| Retrofitting `_meta` onto `session/prompt` | Identity is per-session, not per-turn. A mid-session identity change is a new session in Huddle's model (§14.4). |
| Fixing `ConsolidatorOptions`' unbound options | Known, unrelated, recorded in the base plan's execution record. |

---

## 4. Design Principles

**P1 — The protocol's field, not ours.**
`_meta.systemPrompt` is what ACP defines for the purpose and what `claude-agent-acp` reads. We adapt to it rather than asking a working client to move to a field we prefer.

**P2 — Identity replaces a line, never the prompt.**
A Persona supplies *who it is*. It does not supply *how the harness works*. The ReAct instruction, progressive tool discovery, skills catalogue and grounding are harness invariants and survive every identity.

**P3 — Silence is the worst failure.**
A handler fault that produces no reply is strictly worse than one that crashes: the client cannot distinguish it from a slow model, and no timeout on either side can close it. Base spec **P6** already promises a harness error becomes a JSON-RPC error; this work makes that promise true on *every* path, not most.

**P4 — Additive public surface.**
The adapter ships as `AgencyDotNet.*` packages. A signature change that removes the old signature is binary-breaking even when source-compatible. Overloads, not optional parameters. (Established when `ToolInvokedEvent.CallId` was moved to an `init`-only property.)

**P5 — Runnable out of the box.**
A component that cannot start without undocumented environment variables is a component only its author can run. Defaults ship; environment still overrides.

**P6 — Fix the spec with the code.**
This defect existed because a hop between two tasks was written down nowhere. Closing the code without closing the specification invites the same omission.

---

## 5. Architecture Overview

### 5.1 Target data path

The change is one continuous path from wire to prompt, currently broken at the first arrow.

```text
┌───────────────────────────── ACP CLIENT (Huddle) ──────────────────────────────┐
│  PersonaRunner composes identity text                                           │
│      └─► session/new { cwd, mcpServers[], _meta: { systemPrompt: {append} } }   │
└────────────────────────────────────┬───────────────────────────────────────────┘
                                     │ JSON-RPC 2.0 over stdio
                                     ▼
┌──────────────────────── agency-acp ────────────────────────────────────────────┐
│                                                                                 │
│  StdioTransport ──► MethodDispatcher.HandleSessionNewAsync                      │
│                          │                                                      │
│                          ├─ ❶ ParseIdentityPrompt(_meta.systemPrompt)  ◄── NEW  │
│                          │      { "append": "…" } ──┐                           │
│                          │      "…" (bare string) ──┴─► string? identityPrompt  │
│                          │                                                      │
│                          ├─ ✂ _meta.model reader                       ◄── GONE │
│                          │                                                      │
│                          ▼                                                      │
│                   SessionFactory.CreateAsync(request, identityPrompt, ct)       │
│                          │                                                      │
│                          ▼                                                      │
│                   ChatSession(agent, opts, …, identityPrompt:)         ◄── NEW  │
│                          │            (stored; not yet a Context)               │
│                          ▼                                                      │
│              [first session/prompt]                                             │
│                   Agent.CreateContext(…, identityPrompt:)              ◄── NEW  │
│                          │                                                      │
│                          ▼                                                      │
│                   Context.Query = QueryContext { IdentityPrompt = … }           │
│                          │                                                      │
│                          ▼   ── rebuilt EVERY iteration ──                      │
│                   SystemPromptBuilder.Build(ctx)                                │
│                          │                                                      │
│                          ├─ "You are Ana, who routes…"     ◄── identity line    │
│                          ├─ "When solving a task, always…" ◄── ReAct   SURVIVES │
│                          ├─ (tool_help line: NOT emitted — see U-2 correction)         │
│                          └─ <skills> <knowledge> <grounding>          SURVIVES  │
└─────────────────────────────────────────────────────────────────────────────────┘
```

### 5.2 Error-contract change, orthogonal to the above

```text
        reader loop                 dispatcher                    handler
             │                          │                            │
             │──── line ───────────────►│                            │
             │  (never awaited)         │───── invoke ──────────────►│
             │                          │                            │ throws
             │                          │◄──── AcpJsonRpcException ──┤  ✅ mapped
             │                          │◄──── JsonException ────────┤  ✅ mapped
             │                          │◄──── anything else ────────┤  ❌ ESCAPES
             │◄─ escapes to transport ──┤
             │
             └─ catch (Exception) { }   ◄── swallowed: NO reply, NO crash, NO log
                                            client waits forever
```

**After:** `DispatchAsync` gains a terminal `catch (Exception)` mapping to `ErrorCode.InternalError`. The transport's swallow remains as the last line of defence, but is no longer load-bearing, and gains a stderr log so a swallowed fault is at least observable.

### 5.3 What is new vs what is reused

| Layer | Status |
|---|---|
| `QueryContext.IdentityPrompt`, `SystemPromptBuilder` | **reused unchanged** — shipped and tested by D-3 |
| `Agent.CreateContext`, `ChatSession` ctor | **overloads added** — existing signatures untouched (P4) |
| `SessionFactory.CreateAsync` | **signature changed** — `internal`, no PublicAPI impact |
| `MethodDispatcher.HandleSessionNewAsync` | **amended** — one parser added, one reader deleted |
| `MethodDispatcher.DispatchAsync` | **amended** — terminal catch |
| `Agency.Acp.csproj` + `appsettings.json` | **new** — shipped defaults |
| `IdentityPromptParser` | **new** — the only new type, `internal static` |

---

## 6. System Components

### 6.1 `IdentityPromptParser`

**Purpose.** Turn the untyped `_meta.systemPrompt` token into a `string?` identity, tolerating both wire shapes.

| | |
|---|---|
| **In** | `JToken?` — the `_meta.systemPrompt` value, or absent |
| **Out** | `string?` — identity text, or `null` meaning *use the default line* |

**Internal flow.**

```text
token is null / JTokenType.Null        ──► null
token is JValue(String) s              ──► Normalise(s)
token is JObject o && o["append"]      ──► Normalise(o["append"])
token is JObject o (no "append" key)   ──► null            (unknown shape, tolerated)
token is anything else (array, number) ──► null            (unknown shape, tolerated)

Normalise(s) = string.IsNullOrWhiteSpace(s) ? null : s
```

**Implementation notes.**

- **Both shapes map to the same output.** Huddle's own message is internally contradictory on the bare-string form — line 66 says treat it as an append, the summary table says it means replace. We resolve to *append* for the reason in §14.1, and a bare string is therefore not a second mode but a shorthand for the first.
- **Unknown shapes degrade to `null` rather than erroring.** `_meta` is an extension channel; a client is entitled to put things there we do not understand. Rejecting an unrecognised `systemPrompt` shape with `-32602` would make a forward-compatible client fail against an older adapter. The cost is that a malformed identity is silently ignored — mitigated by the diagnostic log below.
- **Whitespace-only is `null`, not empty.** An identity line of `""` would emit a blank leading line and read as a formatting bug, not as "no identity".
- **One stderr log line per parse outcome**, at Debug: shape recognised, length, or the reason it was ignored. This is the observability that the original defect lacked — a silently-dropped 2 KB prompt would have been visible in one line.

**Constraints.** Pure and static. No allocation beyond the returned string. No `JsonException` may escape: the input is already-parsed `JToken`, and every branch is a type test.

**V1 / V2.** V1 handles `append` and bare string. V2, if ACP defines further modes (e.g. `prepend`, `replace` with an explicit opt-in), extends the switch — the seam is deliberately a single pure function so that extension is local.

---

### 6.2 `MethodDispatcher` — `session/new` amendment

**Purpose.** Extract identity from the request and pass it to the session factory; stop reading a field no client sends.

**Current state** (`Dispatch/MethodDispatcher.cs:150-165`):

```csharp
NewSessionRequest request = @params?.ToObject<NewSessionRequest>() ?? new NewSessionRequest();
string? requestedModelId = @params?["_meta"]?["model"]?.Value<string>();

(SessionState state, NewSessionResponse response) =
    await _sessionFactory.CreateAsync(request, requestedModelId, cancellationToken);
```

**Target state:**

```csharp
NewSessionRequest request = @params?.ToObject<NewSessionRequest>() ?? new NewSessionRequest();
string? identityPrompt = IdentityPromptParser.Parse(@params?["_meta"]?["systemPrompt"]);

(SessionState state, NewSessionResponse response) =
    await _sessionFactory.CreateAsync(request, identityPrompt, cancellationToken);
```

**Implementation notes.**

- The `_meta.model` reader is **deleted, not deprecated**. Huddle confirmed they never send it and select a model afterwards via `session/set_config_option` — the path the base spec documents as canonical. Leaving a speculative reader in place would be unrequested configurability with no consumer.
- `SessionFactory.CreateAsync`'s second parameter changes meaning from `requestedModelId` to `identityPrompt`. Both are `string?`, so **the compiler will not catch a mistake here** — the parameter is renamed *and* the call site updated in the same commit, and §15's tests pin the behaviour rather than the shape.
- The XML doc comment on `HandleSessionNewAsync` currently documents the `_meta.model` behaviour and must be rewritten, not merely extended.

**Constraints.** `session/new` must remain fail-soft (base spec §8.1 step 7). A missing, malformed or unparseable identity must never fail session creation.

---

### 6.3 `MethodDispatcher` — dispatch error contract

**Purpose.** Guarantee base spec **P6** on every path: a harness error becomes a JSON-RPC error, never silence.

**Current state** (`Dispatch/MethodDispatcher.cs:102-114`) catches exactly two types:

| Exception | Mapped to |
|---|---|
| `AcpJsonRpcException` | `ex.Code`, `ex.Message` |
| `JsonException` | `ErrorCode.InvalidParams` |
| **anything else** | **escapes → swallowed by the transport → no reply** |

**Target state** adds a terminal arm:

| Exception | Mapped to |
|---|---|
| `OperationCanceledException` when `cancellationToken` fired | rethrow — shutdown is not an application error |
| anything else | `ErrorCode.InternalError`, message naming the method and exception type |

**Implementation notes.**

- **Ordering matters.** The terminal `catch (Exception)` must come last; `AcpJsonRpcException` and `JsonException` keep their specific mappings.
- **Cancellation must not become an error response.** Process shutdown cancels the token and unwinds in-flight handlers; mapping that to `InternalError` would emit spurious errors during a clean exit. Guard with `when (!cancellationToken.IsCancellationRequested)` on the terminal arm and let genuine shutdown propagate.
- **The message must name the method.** `"Internal error handling 'session/prompt': InvalidOperationException: No LLM client named 'foo'"` is actionable; `"Internal error"` is not. The exception *message* is included; the stack trace is not — it goes to the stderr log, not the wire.
- **The transport's swallow stays**, demoted to a genuine last resort, and gains a stderr log. A fault escaping *after* the dispatcher's terminal catch means the dispatcher itself faulted (e.g. serialising the error response), which is worth seeing.
- **Notifications have no `id`.** `BuildError` returns `null` when `hasId` is false. A faulting notification handler therefore still produces no wire output — correct, since JSON-RPC forbids responding to a notification — but it must still log.

**Constraints.** The reader loop must never be taken down by a handler fault (base spec §10). Nothing here changes that.

---

### 6.4 Identity threading — `ChatSession` and `Agent.CreateContext`

**Purpose.** Carry the identity from session construction to the `Context` created lazily on the first turn.

**The shape of the problem.** `Context.Query` is `{ get; init; }` (`Contexts/Context.cs:57`), so the `QueryContext` cannot be replaced after the `Context` exists. `ChatSession` creates its `Context` lazily at `ChatSession.cs:182` (`this._ctx ??= Agent.CreateContext(…)`) and eagerly at `:93` for `PreviewContext()`. Identity must therefore be **held on the session** from construction and **injected at Context creation**, in both places.

**Target surface** — overloads, per **P4**:

```csharp
// Agent.cs — existing signature untouched; new overload adds one parameter.
public static Context CreateContext(
    string initialPrompt,
    ToolContext? tools, EnvironmentalContext? environment, UserSpecificContext? user,
    TimeProvider? timeProvider, SkillContext? skills, SessionContext? session,
    string? instructionsBlock,
    string? identityPrompt);

// ChatSession.cs — existing constructor untouched; new overload adds one parameter.
public ChatSession(
    Agent agent, AgentOptions options, ToolContext? toolContext, UserSpecificContext? user,
    SkillContext? skills, SessionContext? session, string? instructionsBlock,
    string? identityPrompt);
```

**Implementation notes.**

- The existing members **delegate** to the new ones with `identityPrompt: null`. One implementation, two entry points — no duplicated construction logic.
- `ChatSession` stores `_identityPrompt` beside the existing `_instructionsBlock` and passes it at **both** `CreateContext` call sites. Missing the `PreviewContext()` site would make `/preview`-style hosts disagree with live turns — a divergence that would be very hard to diagnose.
- Both new members require `PublicAPI.Unshipped.txt` entries. **No `*REMOVED*` line may appear**; its presence means an optional parameter was added instead of an overload, and the build must be treated as failing review even though it compiles.
- `identityPrompt` is deliberately **not** on `AgentOptions`. `AgentOptions` is a per-session instance already, but identity is a property of the *query*, not of the agent's operating limits, and `SystemPromptBuilder` reads it from `ctx.Query`. Putting it on `AgentOptions` would split one concept across two homes.

**Constraints.** `SystemPromptBuilder.Build` runs **every iteration** (base spec §9). Identity is therefore present on every iteration by construction, not by a re-injection mechanism — this is what makes O-1's two-iteration assertion meaningful rather than incidental.

---

### 6.5 Configuration bootstrap

**Purpose.** Make `agency-acp` runnable with no environment configuration.

**Current failure.** `Agency.Acp.csproj` declares no content-copy items; the build output contains `Agency.Acp.deps.json` and `Agency.Acp.runtimeconfig.json` and no `appsettings.json`. `Host.CreateApplicationBuilder` therefore finds no configuration, `AgentOptions.DefaultModel` is empty, and `session/new` hard-fails with *"Agent:DefaultModel is not configured"*. Huddle worked around this with per-profile `EnvironmentOverrides`; anyone who is not Huddle simply cannot start the process.

**Target.**

```xml
<ItemGroup>
  <Content Include="appsettings.json" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

with a minimal, documented default:

| Key | Default | Rationale |
|---|---|---|
| `Agent:DefaultClientName` | `local` | Matches the single client defined below |
| `Agent:DefaultModel` | *(documented placeholder)* | Must be overridden for real use; named in the failure message |
| `Agent:LLmClients[0]` | `local`, OpenAI-style, `http://localhost:1234/v1` | The most common local-server default; **no vendor endpoint on the required path** (base spec **P2**) |
| `Skills:DisableShellExecution` | `true` | Lock #4 of the v1 guarantee — already hardcoded in `Program.BuildHost`; mirrored here so the file cannot be read as permission to enable it |

**Implementation notes.**

- **Layering must preserve environment precedence.** `Host.CreateApplicationBuilder` adds `appsettings.json`, then `appsettings.{Environment}.json`, then environment variables, then command line. Huddle's `Agent__DefaultModel` override therefore continues to win. This ordering is asserted in §15, because silently inverting it would break their existing integration.
- **`Skills:DisableShellExecution` in the file is belt-and-braces.** `Program.BuildHost` adds a non-overridable in-memory source setting it `true`; the file value is documentation. §15 asserts the hardcoded source still wins, so a hand-edited `appsettings.json` cannot unlock shell execution — the file must not become a way to defeat a v1 lock.
- **Packaging.** `dotnet pack` already emits `lib/net10.0/Agency.Acp.dll` (verified by the D6 smoke gate). Adding content affects the *build output*, not the lib folder; the pack gate must be re-verified rather than assumed.

**Constraints.** The default must not silently mask a misconfiguration in production. The failure message when a model genuinely cannot be resolved must still name the exact keys and their environment-variable equivalents.

**V1 / V2.** V1 ships one local-server default. V2 could ship an `appsettings.Development.json` with richer diagnostics, or a `--print-config` switch.

---

## 7. Data Model / State Layer

### 7.1 There is still no persistence

Unchanged from the base spec: no database, no files, no serialization. Identity is process memory and dies with the session.

### 7.2 `SessionState` gains one field

| Field | Type | Source | Lifetime |
|---|---|---|---|
| `IdentityPrompt` | `string?` | `_meta.systemPrompt` at `session/new` | session |

Recorded on `SessionState` for two reasons beyond construction:

1. **Diagnostics.** "Which identity is this session running?" is unanswerable today.
2. **Rebuild survival.** `session/set_config_option` rebuilds the client and `Agent` and calls `ChatSession.SetAgent`. `SetAgent` preserves the existing `Context`, so identity survives a model change for free — but only because it lives on the `Context`, not on the `Agent`. Holding it on `SessionState` too makes that invariant inspectable and gives a future rebuild path a source of truth.

### 7.3 Data flow

```text
session/new._meta.systemPrompt
        │
        ▼ IdentityPromptParser.Parse            (pure)
   string? identityPrompt
        │
        ├──────────────────────────► SessionState.IdentityPrompt   (diagnostics)
        │
        ▼ SessionFactory.CreateAsync
   ChatSession(_identityPrompt)                  (held, no Context yet)
        │
        ▼ first session/prompt
   Agent.CreateContext(identityPrompt:)
        │
        ▼
   Context.Query.IdentityPrompt                  (init-only, immutable thereafter)
        │
        ▼ every iteration
   SystemPromptBuilder.Build(ctx) → line 1 of the system prompt
```

**Single assignment, single reader.** Identity is written once at session creation and read once per iteration. There is no mutation path, which is what makes O-3 (no bleed between sessions) structural rather than tested-by-hope: two sessions have two `Context` instances, each with its own immutable `QueryContext`.

### 7.4 Backend differences

None. Identity is prompt text; every backend receives it the same way, in the system prompt position its provider adapter already uses.

---

## 8. Core Algorithms

### 8.1 Identity resolution

```text
1. token ← @params?["_meta"]?["systemPrompt"]
2. identity ← IdentityPromptParser.Parse(token)         // §6.1, total function
3. log at Debug: shape recognised | ignored, and length
4. pass identity to SessionFactory.CreateAsync
5. store on SessionState.IdentityPrompt
6. construct ChatSession with identityPrompt: identity
7. on first turn, Agent.CreateContext(identityPrompt:) → QueryContext
8. every iteration: SystemPromptBuilder emits identity ?? default as line 1
```

**Step 2 is total** — it cannot throw and cannot fail. **Step 8 is where `null` acquires meaning**: it is not "no identity" but "use the runtime's default identity line", which is the pre-existing behaviour and therefore the safe degradation.

### 8.2 Dispatch error mapping

```text
1. resolve handler for method            → not found: -32601
2. invoke handler
3. catch AcpJsonRpcException             → ex.Code, ex.Message
4. catch JsonException                   → -32602 Invalid params
5. catch OperationCanceledException
       when ct.IsCancellationRequested   → rethrow      (clean shutdown)
6. catch Exception                       → -32603 Internal error,
                                           message names method + exception type
7. if no id (notification)               → log only, emit nothing
```

**Step 5 before step 6 is mandatory.** Reversed, a clean shutdown emits a spurious `InternalError` for every in-flight handler.

### 8.3 Error-code mapping table

| Condition | Code | Changed? |
|---|---|---|
| Unknown method | `-32601` | no |
| Unparseable request | `-32700` | no |
| Malformed params | `-32602` | no |
| Unknown session id | `-32602`, names the id | no |
| Second prompt in flight | application error | no |
| **Handler throws anything unmapped** | **`-32603`, names method + type** | **NEW** |
| Shutdown cancellation | *(no response; rethrown)* | **NEW** |

---

## 9. Per-Session vs Per-Turn Work

| Work | Frequency | Cost |
|---|---|---|
| `_meta.systemPrompt` parse | **once per session** | one type test, one string reference |
| `SessionState.IdentityPrompt` store | once per session | one reference |
| `Context` creation carrying identity | once per session (first turn) | one reference |
| **System prompt rebuild including identity** | **per iteration** | string concat; no I/O |
| Dispatch error mapping | per faulting request only | zero on the happy path |

**Why the per-iteration rebuild is the right place.** The base spec's §9 rationale applies unchanged: the system prompt is a pure function of `Context`, rebuilt every iteration so grounding never drifts. Identity rides that existing mechanism rather than introducing a re-injection step — which is precisely why O-1's "present on iteration 2" assertion holds without any additional machinery.

---

## 10. Background Workers / Async Components

No new workers. Two existing components change behaviour:

| Component | Change | Failure mode |
|---|---|---|
| stdin reader loop | unchanged | EOF ⇒ dispose every session |
| `InvokeHandlerAsync` | swallow retained, **stderr log added** | a fault reaching here means the dispatcher itself faulted |
| `DispatchAsync` | terminal catch added | maps to `InternalError`; rethrows shutdown cancellation |

**Ordering constraint, unchanged and load-bearing:** the reader must not await handlers. The terminal catch lives in `DispatchAsync`, *inside* the un-awaited handler task, so it cannot reintroduce head-of-line blocking.

---

## 11. Performance Expectations

### 11.1 Latency

| Operation | Δ vs 0.1.195 | Dominated by |
|---|---|---|
| `session/new` | **+ ~microseconds** | one `JToken` type test |
| First `agent_message_chunk` | **0** | model TTFT, unchanged |
| Per iteration | **+ one string append** | identity text length |
| Faulting request | **−∞ → bounded** | previously hung forever; now one response |

### 11.2 Token cost — the only real cost

Identity text is prepended to **every iteration's** system prompt. A 2 KB Persona prompt across 20 iterations is ~10k tokens of repeated input.

This is not a regression — it is the intended behaviour, and the same cost `claude-agent-acp` incurs. But it interacts with two existing facts worth stating:

- `usage.used` is **occupancy**, so a long identity raises the floor of every reading. Clients summing only the rises (Huddle does) are unaffected.
- `AgentOptions.ContextWindowSize` is populated from `Model.ContextLength`. A very long identity against a small context window shortens the usable conversation. No guard is proposed in V1; §12 records it.

### 11.3 Scaling notes

- **Sessions remain cheap.** Identity adds one string reference per session.
- **Two Personas in one process** is the milestone case: two `Context` instances, two immutable `QueryContext` values, zero shared mutable identity state.

---

## 12. Edge Cases and Failure Modes

| # | Case | Behaviour | Severity |
|---|---|---|---|
| **E-1** | `_meta` absent entirely | Default identity line; session succeeds | expected — every non-Huddle client today |
| **E-2** | `_meta.systemPrompt` is `{"append": "…"}` | Identity line replaced | the primary path |
| **E-3** | `_meta.systemPrompt` is a bare string | Identical to E-2 (§14.1) | expected |
| **E-4** | `_meta.systemPrompt` is `{}` or an unknown shape | Ignored, `null`, session succeeds, **logged** | low — forward compatibility |
| **E-5** | `_meta.systemPrompt` is `""` or whitespace | Treated as absent | low — avoids a blank leading line |
| **E-6** | Identity longer than the context window | Turn fails at first inference with the truncation path | **medium** — unguarded in V1 |
| **E-7** | Two sessions, different identities | Independent; no bleed (structural, §7.3) | the milestone case |
| **E-8** | Identity set, then `session/set_config_option` model change | Identity **survives** — `SetAgent` preserves the `Context` | must be asserted, not assumed |
| **E-9** | Handler throws `InvalidOperationException` (bad client name) | `-32603` naming method and type | **high** — today hangs forever |
| **E-10** | Handler throws during **shutdown** | Rethrown, no response | medium — spurious errors if mis-ordered |
| **E-11** | **Notification** handler throws | Logged; no response (JSON-RPC forbids one) | low |
| **E-12** | `appsettings.json` present but `Agent:DefaultModel` still unresolvable | Error names the key **and** the env-var form | medium |
| **E-13** | Operator edits `appsettings.json` to enable shell execution | **Ignored** — the hardcoded source wins | **high** — a v1 lock must not be file-defeatable |
| **E-14** | Client sends `_meta.model` (no one does today) | Silently ignored; no longer read | low — deliberate |

**E-6 has no clean answer in V1.** The adapter cannot know the tokenised length of an identity without a tokeniser it does not have, and `ContextLength` is `null` for most servers (base spec **P3**). Recorded, not solved.

**E-13 is the reason the config file is not simply "configuration".** Four of the six v1 locks are structural; `Skills:DisableShellExecution` is the one expressible in a config file, and shipping such a file creates the first plausible route to flipping it. The hardcoded in-memory source must therefore remain last-wins, and §15 asserts it.

---

## 13. End-to-End Flow

```text
 1  Huddle spawns  agency-acp                                  [no env required — §6.5]
 2  → initialize {protocolVersion: 1}
    ← {agentInfo, authMethods: [], agentCapabilities.loadSession: false}
 3  Huddle starts AppToolServer on loopback, mints a bearer token
 4  → session/new {
        cwd, mcpServers: [{team, http://127.0.0.1:p/mcp, Authorization: Bearer …}],
        _meta: { systemPrompt: { append: "You are Ana, who routes…" } }   ◄── NEW
      }
      · IdentityPromptParser.Parse → "You are Ana, who routes…"
      · SessionState.IdentityPrompt recorded
      · McpClientPool → tools/list → ToolRegistry
      · ChatSession(identityPrompt: "You are Ana…")
    ← {sessionId, configOptions[model, effort]}
 5  → session/set_config_option {model}   → rebuild client+Agent → SetAgent
      · Context is preserved, so identity survives                       ◄── E-8
 6  → session/prompt {"[#product] summarise the thread"}
      ├─ Context created; Query.IdentityPrompt = "You are Ana…"
      ├─ iteration 1: system prompt =
      │     "You are Ana, who routes…"            ◄── identity
      │     "When solving a task, always…"        ◄── ReAct     SURVIVES
      │     (no tool_help line — plain ToolRegistry; see U-2 correction)
      │     <skills> <knowledge> <grounding>      ◄── SURVIVES
      │   ← agent_message_chunk ×N
      │   ← tool_call get_help → tool_call_update
      ├─ iteration 2: system prompt REBUILT, identity present again      ◄── O-1
      │   ← agent_message_chunk ×N
    ← {stopReason: "end_turn"}
 7  A second session with a different identity runs concurrently,
    sharing nothing                                                      ◄── O-3
 8  Any handler fault anywhere above → -32603 naming the method          ◄── O-4
```

---

## 14. Design Notes / Rationale

### 14.1 Why a bare string is an append, not a replace

Huddle's message says both, in two places. We resolve to **append** — meaning "replace the opening identity line, keep everything else" — for three reasons, in increasing order of force:

1. **Their own line 66 says so**: *"handle the object form and treat a bare string as an append."*
2. **§I1 forbids a Replace mode**, and `QueryContext.IdentityPrompt` structurally cannot express one: `SystemPromptBuilder` substitutes a single line and assembles the rest unconditionally.
3. ~~**A true replace would break the feature it is meant to enable.**~~ **Withdrawn during implementation.** This argument assumed the harness prompt carries *"Always call `tool_help(name)`…"* in an ACP session. It does not: that line is gated on `ctx.Tools.Registry is IProgressiveDiscovery`, and the ACP adapter builds a plain `ToolRegistry`. The argument is therefore void — but the decision stands unchanged on arguments 1 and 2, either of which is sufficient. Recorded rather than deleted, because a decision whose stated reasoning is partly wrong should show that plainly to the next reader.

   A replace would still delete the ReAct instruction, the skills catalogue and the grounding sections, which remains reason enough to refuse one.

The rejected alternative — `-32602` on a bare string — is honest but breaks a client that demonstrably sends the form, to enforce a distinction we have decided does not exist.

### 14.2 Why overloads rather than an optional parameter

Adding `string? identityPrompt = null` to `Agent.CreateContext` and the `ChatSession` constructor is source-compatible and one line each. It also **deletes both existing signatures from the public surface** — `PublicAPI.Unshipped.txt` records them as `*REMOVED*` — which is binary-breaking for anyone compiled against the published `AgencyDotNet.Harness` package.

This is the identical trade-off taken on `ToolInvokedEvent.CallId` before the 0.1.195 PR, and taken the same way. Consistency here is itself the argument: a codebase that sometimes protects binary compatibility and sometimes does not protects it never, because consumers cannot tell which release did which.

The cost is real — an eight-parameter constructor gains a nine-parameter sibling. It is paid once, in a constructor hosts call once per session.

### 14.3 Why the terminal catch belongs in the dispatcher, not the transport

The transport already has `catch (Exception) { }`, so one could argue the fix belongs there. It does not:

- The transport has **no access to the request id**, which a JSON-RPC error response requires. By the time a fault reaches `InvokeHandlerAsync`, the id has been parsed and discarded.
- The transport cannot distinguish a **notification** (must not be answered) from a **request** (must be).
- The transport's comment claims *"the handler is responsible for translating its own failures"*. Handlers are not; `DispatchAsync` is, and it covered two types out of all possible types. **The comment described an intended contract that no code enforced** — which is the general shape of this entire document's defect class.

The transport's swallow stays as a genuine last resort, with a log, because a fault that escapes the dispatcher's terminal catch indicates the dispatcher itself is broken and must not take down the reader loop.

### 14.4 Why identity is per-session, not per-turn

ACP would allow `_meta` on `session/prompt`. We do not read it there, because Huddle's model treats a Persona's identity as fixed for the session's life — changing it restarts the Persona and clears what it remembers. Supporting per-turn identity would mean either mutating an `init`-only `QueryContext` or rebuilding the `Context` mid-session and losing history — machinery to buy turn-by-turn variation nobody has asked for. The same reasoning the base spec applies to effort (§6.7, G-4) applies here.

### 14.5 Decision log

| # | Decision | Rejected | Why |
|---|---|---|---|
| **G-1** | Both `_meta.systemPrompt` shapes set `IdentityPrompt` (append semantics) | `-32602` on bare string; true full replace | §14.1 — a replace deletes the ReAct instruction, skills catalogue and grounding (the progressive-discovery rationale was withdrawn; see §14.1) |
| **G-2** | Overloads on `CreateContext` / `ChatSession` | optional trailing parameter | §14.2 — avoids `*REMOVED*`; consistent with `ToolInvokedEvent` |
| **G-3** | Ship a default `appsettings.json` | document env vars only | §6.5 — a component only its author can start is not shippable; env still overrides |
| **G-4** | Leave progressive tool discovery **off** for ACP sessions | switch `SessionFactory` to `ProgressiveDiscoveryToolRegistry` | Huddle's decision, `13-huddle-drop-validated-one-blocker-left.md` §4. Their composed prompt is self-contained and names all seven tools, so the `tool_help` priming was never load-bearing for them. Actively harmful to turn on: `get_help` (semantic) and `tool_help` (syntactic) are near-homonyms serving different purposes, and chaining two discovery hops is a failure the models in scope cannot afford |
| **G-5** | Ship `ApiKey` in the default config **and** guard the empty case with a named error | ship the value only | §6.5 / §12 E-12. The value alone fixes the vanilla path but leaves the next person who edits the file with `ArgumentException … (Parameter 'key')`, which names nothing |

**None of the three meets the ADR bar** (hard to reverse × surprising × real trade-off). G-1 is reversible by extending one pure function; G-2 is a repo-wide convention already recorded; G-3 is a one-file change. They are recorded here instead.

### 14.6 The specification fix

§1.2 records that this defect existed because a hop between two tasks was written down nowhere. Code alone does not close that. The base spec is amended in the same change:

- **§8.1** gains a step **1a**: *parse `_meta.systemPrompt` → `identityPrompt`*, placed before step 4 so it is visibly part of `session/new`'s contract.
- **§6.9 (D-3)** gains a sentence naming its consumer: *"the ACP adapter supplies this from `_meta.systemPrompt`; see §8.1 step 1a."*
- **§6.7** loses the `_meta.model` reference; `session/set_config_option` is documented as the only model-selection path.
- `docs/Projects/Agency.Acp.md` gains an Identity section — it currently does not mention `IdentityPrompt` at all, mirroring the code gap exactly.

**Why this matters:** a delta with no named consumer and a step with no named producer are the same bug written twice. Naming both ends makes the omission visible to the next reader.

---

## 15. Test-First Task Plan

Every implementation task is preceded by its test task. **Vertical slices only**: one test → its implementation → the next. Do **not** write all tests then all implementations — tests written in bulk describe imagined behaviour and end up asserting shape rather than conduct.

Tests must exercise **public/observable behaviour**: drive `session/new` and inspect the submitted prompt, rather than asserting that a parser was called.

### Phase 1 — the identity path (blocking; unblocks the joint milestone)

| # | Test task (first) | Implementation task |
|---|---|---|
| **T-1 / I-1** | Unit: `IdentityPromptParser.Parse` returns the text for `{"append":"X"}`, for bare `"X"`, and `null` for absent / `{}` / `""` / whitespace / a number | `IdentityPromptParser` (§6.1) |
| **T-2 / I-2** | Unit: `Agent.CreateContext(…, identityPrompt: "X")` yields `ctx.Query.IdentityPrompt == "X"`; **the existing overload still compiles unchanged and yields `null`** | `CreateContext` overload (§6.4) |
| **T-3 / I-3** | Unit: a `ChatSession` built with `identityPrompt: "X"` submits a system prompt whose first line is `X`, **and** still contains the ReAct and progressive-discovery text | `ChatSession` overload + both `CreateContext` call sites (§6.4) |
| **T-4 / I-4** | Unit: **`session/new` carrying `_meta.systemPrompt` produces a session whose first submitted prompt carries that identity** — driven through the dispatcher, not the parser | `HandleSessionNewAsync` wiring (§6.2) |
| **T-5 / I-5** | Unit: identity is present in the submitted prompt of iteration **1 and 2** of one turn (**O-1**) | *(no new code — asserts §9's per-iteration rebuild)* |
| **T-6 / I-6** | Unit: two sessions with different identities produce different prompts; neither contains the other's text (**O-3**) | *(no new code — asserts §7.3 immutability)* |
| **T-7 / I-7** | Unit: identity survives `session/set_config_option` changing the model (**E-8**) | *(no new code — asserts `SetAgent` preserves `Context`)* |
| **T-8 / I-8** | Unit: `_meta.model` is ignored; `PublicAPI.Unshipped.txt` contains **no `*REMOVED*` entry** (**O-6**) | delete the `_meta.model` reader (§6.2) |

**T-5, T-6 and T-7 have no paired implementation.** They assert properties that fall out of existing structure. That is deliberate: a property believed to hold for structural reasons and never asserted is exactly how the original defect shipped.

### Phase 2 — the error contract

| # | Test task (first) | Implementation task |
|---|---|---|
| **T-9 / I-9** | Unit: a handler throwing `InvalidOperationException` yields `-32603` naming the method and type — **not** silence (**O-4**, **E-9**) | terminal `catch` in `DispatchAsync` (§6.3) |
| **T-10 / I-10** | Unit: `AcpJsonRpcException` and `JsonException` keep their existing mappings **unchanged** | *(regression guard on catch ordering)* |
| **T-11 / I-11** | Unit: a handler faulting during shutdown cancellation produces **no** error response (**E-10**) | `when (!ct.IsCancellationRequested)` guard (§6.3) |
| **T-12 / I-12** | Unit: a faulting **notification** handler emits nothing but logs (**E-11**) | notification path in the terminal catch |

### Phase 3 — configuration bootstrap

| # | Test task (first) | Implementation task |
|---|---|---|
| **T-13 / I-13** | Unit: `Program.BuildHost()` with **no** environment resolves a non-empty `Agent:DefaultModel` (**O-5**) | ship `appsettings.json` + content item (§6.5) |
| **T-14 / I-14** | Unit: `Agent__DefaultModel` **overrides** the shipped file value — Huddle's integration depends on this | *(asserts configuration source ordering)* |
| **T-15 / I-15** | Unit: `Skills:DisableShellExecution` remains `true` even when `appsettings.json` sets it `false` (**E-13**) | *(asserts the hardcoded source wins)* |
| **T-16** | Verify `dotnet pack` still emits `lib/net10.0/Agency.Acp.dll` after adding content | — |

### Phase 4 — end-to-end

| # | Test task | |
|---|---|---|
| **T-17** | Functional (`Category=Functional`, `RequiresLlm`): drive the real `agency-acp` process with `_meta.systemPrompt` naming a distinctive token; assert the token influences the reply, a second session with a different identity does not see it, and **no filesystem or shell tool is advertised** | the Agency half of the joint milestone |

### Regression gate

The entire suite — unit **and** functional — must stay green: **24 assemblies, 1,896 tests, 0 failures, 0 build warnings**, run with `-- RunConfiguration.MaxCpuCount=1`.

**`PublicAPI.Unshipped.txt` must contain zero `*REMOVED*` entries** when this work lands. Its presence means an optional parameter was used instead of an overload (§14.2) and the change must be reworked even though it compiles and passes.

### Documentation tasks (not optional — §14.6)

| # | Task |
|---|---|
| **D-1** | `Agency.Acp-Specifications.md` §8.1: add step **1a** parsing `_meta.systemPrompt` |
| **D-2** | `Agency.Acp-Specifications.md` §6.9 (D-3): name the ACP adapter as the consumer |
| **D-3** | `Agency.Acp-Specifications.md` §6.7: remove `_meta.model`; document `session/set_config_option` as the sole model path |
| **D-4** | `docs/Projects/Agency.Acp.md`: add an Identity section — it currently omits `IdentityPrompt` entirely |
| **D-5** | `docs/Projects/Agency.Acp.md`: document the shipped configuration defaults and env-var precedence |

---

## Appendix A — Traceability

| Huddle ask | Spec section | Tasks | Blocking? |
|---|---|---|---|
| 1. Read `_meta.systemPrompt` → `IdentityPrompt` | §6.1, §6.2, §6.4 | T-1…T-7 | **yes — the only one** |
| 2. Drop the `_meta.model` reader | §6.2 | T-8 | no |
| 3. Ship a default `appsettings.json` | §6.5 | T-13…T-16 | no — worked around client-side |
| 4. General `catch` so **P6** holds everywhere | §6.3 | T-9…T-12 | no — mitigated client-side |

## Appendix B — What this does not prove

Task **T-17** proves the Agency half at the protocol boundary. It does not prove the product. The agreed milestone remains *two Personas in one Room, live chunk rendering, and a Stop click leaving both resumable* — and three of those live in Agency.Huddle. Huddle reports all four client-side prerequisites landed and their suite at 1,294 green, so the joint run is schedulable once Phase 1 ships.
