# Agency.Acp — Design Specification (HLD)

**Status:** design. Supersedes the ten-bullet execution plan.
**Agreed with:** Agency.Huddle, over `PRIVATE/Huddle/01-`…`07-`.
**Grilled decisions:** §16 records the four architectural forks resolved in session.

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
9. [Per-Turn vs Per-Session Work](#9-per-turn-vs-per-session-work)
10. [Background Workers / Async Components](#10-background-workers--async-components)
11. [Performance Expectations](#11-performance-expectations)
12. [Edge Cases and Failure Modes](#12-edge-cases-and-failure-modes)
13. [End-to-End Flow](#13-end-to-end-flow)
14. [Design Notes / Rationale](#14-design-notes--rationale)
15. [Test-First Task Plan](#15-test-first-task-plan)
16. [Decision Log](#16-decision-log)

---

## 1. Goal

### 1.1 Primary goal

Expose the Agency.NET agent harness as an **Agent Client Protocol (ACP) agent**: a process that
speaks newline-delimited JSON-RPC 2.0 over stdio, so that any ACP client can drive an Agency agent
without linking against it.

The immediate consumer is Agency.Huddle, which today drives Anthropic's `claude-agent-acp` Node
adapter and wants to point the same unchanged client at Agency.NET. The strategic consumer is
anyone else with an ACP client — Zed, buzz, a future tool — because a standard protocol surface is
worth more to adoption than a bespoke library seam.

### 1.2 Concrete objectives

| # | Objective | Measure |
|---|---|---|
| O-1 | A Persona's reply renders **live**, token by token | `agent_message_chunk` arrives before the terminal response |
| O-2 | A Persona can call the client's own tools over MCP, **authenticated** | bearer-token MCP server connects and `tools/call` succeeds |
| O-3 | A human's Stop click cancels cleanly and leaves the session **resumable** | `stopReason: cancelled`, partial text retained, next prompt succeeds |
| O-4 | Six concurrent Personas do not take the inference box down | no more than *N* concurrent inferences, enforced outside every client |
| O-5 | The agent registers **no filesystem or shell tools**, provably | five named assertions, one guarantee |
| O-6 | Works against any local server speaking OpenAI-style or Claude-style HTTP | no vendor endpoint on the required path |

### 1.3 What "adapter" means here

`Agency.Acp` is **not** a re-implementation of the agent loop. It is a **protocol translator** with
four jobs:

1. Decode JSON-RPC from stdin, encode notifications and responses to stdout.
2. Own session lifetime — construct and dispose the per-session object graph.
3. Translate `AgentEvent` (Agency's turn stream) into `session/update` notifications.
4. Hide harness mechanics that have no ACP representation — principally the permission
   park/resume round-trip, which must look like one continuous `session/prompt`.

Everything else — prompting, tool dispatch, hooks, stop conditions — stays in `Agency.Harness`.

---

## 2. Example Use Cases

**U-1 — A Persona replies in a Room.**
Client sends `session/prompt` with the Room message. Agent streams `agent_message_chunk` as the
model produces text, emits `tool_call` / `tool_call_update` around each MCP tool invocation, and
closes with `stopReason: end_turn`.

**U-2 — A Persona invites another Persona.**
Mid-turn the model calls `invite_agent` on the client's MCP server. The adapter forwards it over
authenticated HTTP, returns the result to the model, and the loop continues — several iterations
inside one `session/prompt`.

**U-3 — A human hits Stop.**
Client sends `session/cancel` from any thread. The in-flight `session/prompt` returns
`stopReason: cancelled`. Text already streamed is kept; the transcript is left valid so the next
prompt works.

**U-4 — Switching a Persona's model mid-conversation.**
Client sends `session/set_config_option` naming a different model. History is preserved and the next
turn runs on the new model. The effort ladder is **re-sent for the rebuilt client, and is unchanged
unless the surface changed** — a model swap does not change the surface. The ladder is static
per-client (§6.7, H6), never a property of the model.

**U-5 — A stale stored model id.**
Client opens a session naming a model that no longer exists. The session starts anyway on the
agent's default. A stale stored choice never blocks a session.

**U-6 — A reasoning model on two different efforts.**
Two sessions in one process, same served model, different thinking budgets — a summariser on
minimal, a reviewer on high.

### 2.1 Composition example

```text
session/new(cwd, mcpServers:[team@http + bearer])
   → sessionId, models[], effortLevels[]
session/set_config_option(model = "qwen/qwen3.6-35b-a3b")
session/prompt("[#product] Ana: can you summarise the thread?")
   ← session/update  agent_message_chunk  "Sure"
   ← session/update  agent_thought_chunk  "The thread has three..."
   ← session/update  tool_call            get_help        (pending)
   ← session/update  tool_call_update     get_help        (completed)
   ← session/update  agent_message_chunk  " — here's the gist:"
   ← session/update  usage                {size, used}
   → { stopReason: "end_turn" }
```

---

## 3. Non-Goals

| Not doing | Why |
|---|---|
| `session/load`, `session/list`, `session/resume` | Park state is in-memory only; there is no serializer. `supportsLoadSession: false`. |
| `fs/*` and `terminal/*` client calls | Huddle advertises all three capabilities as `false`. Nothing in the agent needs them. |
| Filesystem or shell tools | §6.5 registers none. This is a guarantee with a test, not a default. |
| Cloud OpenAI / cloud Anthropic | Out of scope by product intent — the point is a Persona that costs nothing to run. |
| USD cost accounting | `TotalCostUsd` is structurally `0m`; there is no price table anywhere in the solution. |
| Memory, semantic search, skills | v2. Mechanisms settled, config-gated, off by default. |
| Loop Kit | Deferred. Returns with a price table or an explicit "USD ceilings unsupported" throw. |
| Session-scoped working directories | Unreachable while no file or shell tools are registered. |
| An authentication flow | `authMethods: []`. Credentials are the adapter's configuration. |

---

## 4. Design Principles

**P1 — Translate, don't re-implement.**
Every behaviour the harness already has is reached through `ChatSession`. The adapter adds no
agent logic. Where ACP needs something the harness cannot express, the fix lands in the harness as
a first-class feature, not as a shadow implementation in the adapter.

**P2 — Vendor knowledge lives at the provider edge, never on the required path.**
The contract is OpenAI-style or Claude-style HTTP. Richer metadata (model kind, residency, context
length) is *enrichment*: providers fill it where their server answers, and null everywhere else.
No feature may be load-bearing on a vendor-specific endpoint.

**P3 — Degrade honestly.**
Absent data renders as absent, never as a claim. A model with unknown residency shows as a plain
row, not as "not loaded".

**P4 — One terminal event per prompt.**
`session/prompt` returns exactly once. Park/resume loops, tool batches and multi-iteration
reasoning are all invisible to the client.

**P5 — Guarantees are tested, not documented.**
"The adapter registers no shell tool" is a five-assertion test. A property that holds by omission
is a property that will be removed by accident.

**P6 — Fail loudly at the boundary.**
A harness error becomes a JSON-RPC error response, not a silent `end_turn`. A supervisor that
cannot distinguish failure from completion cannot supervise.

**P7 — The endpoint ceiling is the endpoint's problem.**
Concurrency limiting belongs in front of the inference server, where it covers every client —
including `curl`. A client-side gate is a convention, not a guarantee.

**P8 — Protocol multi-session, honest about identity.**
Sessions are independent in conversation, tools and model. Process-global state (user identity,
credentials) is documented as shared rather than claimed as isolated.

---

## 5. Architecture Overview

### 5.1 Target architecture

```text
┌──────────────────────────────── ACP CLIENT (Huddle) ─────────────────────────────────┐
│  PersonaRunner ──► dotacp.client ──► stdio ──┐                                        │
│  AppToolServer  (MCP, POST /mcp, Bearer) ◄───┼──────────────────┐                     │
└──────────────────────────────────────────────┼──────────────────┼─────────────────────┘
                                               │ JSON-RPC 2.0     │ MCP over HTTP
                                               ▼                  │
┌──────────────────────── agency-acp  (single process) ───────────┼─────────────────────┐
│                                                                 │                     │
│  ┌──────────────┐   ┌──────────────────┐   ┌─────────────────┐  │                     │
│  │ StdioTransport│──►│ MethodDispatcher │──►│ SessionRegistry │  │                     │
│  │  reader loop  │   │  13 AgentMethods │   │  id → Session   │  │                     │
│  │  writer queue │◄──┤                  │◄──┤                 │  │                     │
│  └──────────────┘   └──────────────────┘   └────────┬────────┘  │                     │
│         ▲                                           │           │                     │
│         │ session/update                            ▼           │                     │
│  ┌──────┴────────┐                        ┌──────────────────┐  │                     │
│  │ EventTranslator│◄───── AgentEvent ──────│   TurnDriver     │  │                     │
│  │  chunk/tool/   │                        │ one prompt in    │  │                     │
│  │  usage/thought │                        │ flight; park     │  │                     │
│  └───────────────┘                         │ /resume bridge   │  │                     │
│                                            └────────┬─────────┘  │                     │
│                                                     ▼            │                     │
│  ┌──────────────────────────── Agency.Harness ────────────────┐  │                     │
│  │  ChatSession ──► Agent (ReAct loop) ──► IToolRegistry ─────┼──┘                     │
│  │      │                 │                     ▲              │                       │
│  │      │                 │              McpClientPool (per session, Headers)          │
│  │      │                 ▼                                    │                       │
│  │      │          IChatClient  (MEAI)                         │                       │
│  └──────┼─────────────────┼─────────────────────────────────────┘                      │
│         │                 │                                                            │
│  ┌──────▼──────────┐      │  PersonaPermissionEvaluator: allow-granted / deny-else     │
│  │ ModelCatalogue  │      │  (never Ask → park/resume never fires in v1)               │
│  └─────────────────┘      │                                                            │
└───────────────────────────┼────────────────────────────────────────────────────────────┘
                            │ OpenAI-style or Claude-style HTTP
                            ▼
              ┌──────────────────────────┐
              │  InferenceGate  :1234    │   SemaphoreSlim(N) · /healthz
              │  (separate repo)         │   forwarder only, no cache
              └────────────┬─────────────┘
                           ▼
              ┌──────────────────────────┐
              │ Local inference server   │   LM Studio / Ollama / any
              │ 127.0.0.1:1235           │   OpenAI- or Claude-compatible
              └──────────────────────────┘
```

### 5.2 What is new vs what is reused

| Layer | Status |
|---|---|
| `StdioTransport`, `MethodDispatcher`, `SessionRegistry`, `TurnDriver`, `EventTranslator` | **new** — `Agency.Acp` |
| `ChatSession`, `Agent`, `IToolRegistry`, `McpClientPool`, `IAgentFactory` | **reused unchanged** |
| `AssistantTextDeltaEvent`, `ToolStartedEvent`, `CallId`, truncation status, repair-on-cancel, timeout CTS | **harness deltas** — §6.9 |
| `McpServerConfig.Headers` | **landed** — coded, builds clean, untested |
| `Model` optional metadata, `LlmClientOptions` effort fields | **provider deltas** — §6.7 |
| `InferenceGate` | **new, separate repo** — §6.10 |

---

## 6. System Components

### 6.1 `StdioTransport`

**Purpose.** Move newline-delimited JSON-RPC 2.0 frames between stdin/stdout and the dispatcher.

**Responsibilities.** Read framing; deserialize using `dotacp.protocol`'s own converters; serialize
responses and notifications; guarantee interleaving safety on the write side.

| | |
|---|---|
| **In** | `Stream` stdin |
| **Out** | `Stream` stdout; decoded requests to `MethodDispatcher` |

**Internal flow.** A single reader loop deserializes and hands each message to the dispatcher
*without awaiting the handler* — otherwise `session/cancel` could not overtake an in-flight
`session/prompt`. All writes funnel through one `Channel<string>` drained by a single writer task,
so notifications emitted concurrently by several sessions never interleave mid-frame.

**Implementation notes.**
- Serialization uses `dotacp.protocol`'s attribute-driven contracts and its own converters. Default
  `JsonSerializerOptions` produce the wrong wire form for union discrimination (`McpServer` →
  `Http`/`Stdio`/`Sse`) and enum spellings. Use the package's options; do not hand-roll.
- stdout is **protocol-only**. Every log sink goes to stderr or a file. A stray `Console.WriteLine`
  corrupts the stream — this is the single most likely v1 defect and is asserted against.
- `ProtocolMeta.Version` is `1`, a `readonly struct` over `UInt16` serialized as a bare number.

**Constraints.** Writer strictly serialized. Reader never blocks on handler completion.

**V1 / V2.** V1 stdio only. V2 could add a socket transport for out-of-process debugging.

---

### 6.2 `MethodDispatcher`

**Purpose.** Route the 13 `AgentMethods` constants to handlers; produce JSON-RPC errors for
everything else.

| Method | V1 |
|---|---|
| `initialize` | ✅ |
| `session/new` | ✅ |
| `session/prompt` | ✅ |
| `session/cancel` | ✅ (notification) |
| `session/set_config_option` | ✅ model + effort |
| `session/close`, `session/delete` | ✅ dispose the graph |
| `authenticate`, `logout` | ❌ `authMethods: []` |
| `session/load`, `session/list`, `session/resume` | ❌ `supportsLoadSession: false` |
| `session/set_mode` | ❌ no mode concept |

**Implementation notes.** `initialize` reports `AgentName`, `AgentVersion` (from NBGV),
`ProtocolVersion` 1, `AuthMethods: []`, `SupportsLoadSession: false`. Unimplemented methods return
`-32601 Method not found` rather than failing silently.

---

### 6.3 `SessionRegistry`

**Purpose.** Own session lifetime and the per-session object graph.

**Responsibilities.** Create on `session/new`; look up by id; dispose on `session/close` and on
process shutdown.

**Per-session graph** — built from an `IServiceScopeFactory` scope:

| Object | Lifetime | Note |
|---|---|---|
| `ChatSession` | session | `IAsyncDisposable` |
| `Agent` | session | rebuilt on model or effort change |
| `AgentOptions` | **session** | a per-session instance, not the DI singleton — carries this session's `ContextWindowSize` and `TurnTimeoutSeconds` |
| `ToolContext` / `IToolRegistry` | session | populated from the MCP pool only |
| `McpClientPool` | session | from `session/new`'s `mcpServers`; `await using` |
| `CancellationTokenSource` | turn | replaced per prompt |

**Implementation notes.**
- `ChatSession`'s constructor takes `AgentOptions` **as a parameter**, so a per-session context
  window needs no harness change. This is the finding that makes multi-session cheap.
- `cwd` from `session/new` is accepted and recorded for diagnostics only. No tool can observe it
  (§6.5). Documented, not silently ignored.
- Concurrent `session/new` calls are safe: registry is a `ConcurrentDictionary`.

**Disposal triggers.** The graph is disposed on **`session/close`, transport disconnect, or
process shutdown — whichever comes first**. `session/close` is **optional in the protocol and the
first client does not send it**: Huddle's `DotAcpAgentSession.DisposeAsync` completes its local
channel and makes no wire call. Disposal must therefore never *depend* on it; `session/close` only
releases resources **sooner**. Under multi-session this is the difference between a bounded process
and one that holds every `McpClientPool` and DI scope ever created. See §12 (E-15).

**Constraints.** Process-global state — `AgentOptions.UserId`, credentials, the permission rules
file — is **shared across sessions** and documented as such (P8). This is acceptable only while
memory is v2 and no file tools exist; both conditions are asserted in §15.

Note the shared thing is a **value, not a structure**: `AgentOptions` is already a per-session
instance (see the table above), constructed from the configured one with only `ContextWindowSize`
and `TurnTimeoutSeconds` varied. `UserId` is left at the process-wide value by choice, not by
constraint.

**V1 / V2.** V1 multi-session with shared identity. V2, if memory ships, needs a per-session
`UserId` — which is **one assignment, not a structural change**. The open problem is not where to
put it but **where it comes from**: ACP carries no user identity on `session/new`, so it **needs a
carrier** — most plausibly `session/new`'s `_meta`, the channel the Persona prompt already rides.
That is a client-side proposal, not an adapter change.

---

### 6.4 `TurnDriver`

**Purpose.** Turn one `session/prompt` into one terminal response, whatever the harness does in
between.

**Internal flow.**

```text
prompt ──► guard: one in flight per session ──► ChatSession.SendAsync(text, turnCt)
             │
             ├─ AgentEvent stream ──► EventTranslator ──► session/update ×N
             │
             ├─ AgentResultStatus.AwaitingPermission?
             │      └─► collect PermissionRequestedEvents
             │          └─► session/request_permission (client)
             │              └─► ResumeWithPermissionsAsync ──► loop back  ⟲
             │
             ├─ OperationCanceledException? ──► stopReason: cancelled
             └─ terminal AgentResultEvent ──► map status ──► respond once
```

**Implementation notes.**
- The park loop is a **loop**, not a single round-trip. One prompt may park several times; each
  park yields one or more `PermissionRequestedEvent`s and resumes into the same `Context`.
- In v1 the park path is unreachable (§6.6) but fully implemented and tested — correct and
  unexercised, per the agreement.
- Cancellation emits **no terminal `AgentResultEvent`**; the exception is the signal. The driver
  synthesises the stop reason.

**Constraints.** One prompt in flight per session; a second returns a JSON-RPC error rather than
queueing, because overlapping prompts indicate a client defect and should fail loudly (P6).

---

### 6.5 Tool Surface

**Purpose.** Expose exactly the client's MCP tools, and nothing else.

**Internal flow.** `session/new` carries `mcpServers[]`. For each `McpServerHttp`, map
`Name`/`Url`/`Headers` onto `McpServerConfig`, call `McpClientPool.CreateAsync`, and register the
discovered `McpProxyTool`s into a fresh `ToolRegistry`. No built-in tool is registered at any point.

**Implementation notes.**
- `AddAgencyAgent()` registers **no tools**, so the empty registry is the default rather than a
  switch. The guarantee is that nothing adds to it.
- Tool names pass through **unmodified** — the harness mints no `mcp__server__tool` prefix, and the
  adapter adds none. The client's prompt must name tools as its server names them.
- MCP and native tools share one flat, last-write-wins namespace. With no native tools registered
  the collision surface is between MCP servers only.
- `ToolKind` classification is **not** performed. The raw name travels to the client, which owns
  the mapping.

**Constraints — the five locks (P5).** §15 asserts each independently:

| Lock | Closes |
|---|---|
| No built-in tools registered | `read_file`, `write_file`, `execute_powershell`, `subagent_tool` |
| `skill` tool not registered | model-invoked skills |
| No `ISkillShellRunner` wired | shell expansion suppressed entirely |
| `Skills:DisableShellExecution` | `` !`cmd` `` expansion in SKILL.md |
| No hook can return `Ask` | the permission park path |

The last is behavioural, not structural: an evaluator `Allow` does **not** clear a hook `Ask`, and
a hook `Ask` parks the turn *even with no evaluator supplied*. It enumerates the App Tool registry
so a tool added later is covered the day it is registered.

---

### 6.6 Permission Bridge

**Purpose.** Make `AwaitingPermission` invisible to the client.

**Model mismatch.** ACP is an async request mid-turn. Agency parks: `IPermissionEvaluator.Evaluate`
is synchronous and pure, an unresolved call ends the turn with `AwaitingPermission`, and the host
resumes. §6.4 bridges the two.

**V1 configuration.** `PersonaPermissionEvaluator` — allow the granted set, deny everything else,
**never ask** — so `session/request_permission` never fires. Deliberately *not* the Console's
`PermissionEvaluator`, which would leak `%LocalAppData%\Agency\permissions.local.json` grants
between sessions.

| Behaviour | Result |
|---|---|
| Granted tool | Allow |
| Anything else | `[Blocked] …` tool result, `IsError: true`, model recovers |
| `AllowAlways` | never offered — no grant file is written |

**Why it matters.** A Persona that parks waiting for a human it cannot reach is a hang, not a
prompt. And a denial reaching the model as a tool result is recoverable; a turn failure is not.

---

### 6.7 Model Catalogue and Effort

**Purpose.** Answer `session/new`'s catalogue, and apply per-session model and effort.

**Provider-neutral metadata (P2).** `Model` gains optional fields; providers fill what their server
answers:

| Field | Type | OpenAI-compat | Richer server |
|---|---|---|---|
| `Id`, `Name` | `string` | ✅ | ✅ |
| `Kind` | `ModelKind?` | `null` | chat / embedding / vision |
| `ContextLength` | `int?` | `null` | ✅ |
| `IsLoaded` | `bool?` | `null` | ✅ |

**Mapping to ACP.** `Model` → `AgentModelOption(Id, Name, Description)`, where `Description`
carries residency as a **point-in-time statement** ("loaded now"), and is `null` when unknown.
Null renders as a plain row and never as a claim (P3).

**Filtering.** Where `Kind` is known, embedding models are excluded. Where it is `null` they cannot
be excluded, so a non-chat selection must fail as a clear JSON-RPC error (P6) — `G6`'s
unknown-model fallback will not catch it, because the model genuinely is in the catalogue.

**Context window.** `ContextLength` populates the per-session `AgentOptions.ContextWindowSize`.
Where it is the model's maximum rather than the loaded length, the grounding line says "model
maximum" — the overstatement must stay visible to the model, which reads it every iteration.

**Effort.** Per-client, not per-request. `LlmClientOptions` is a `record`; each session builds its
client with `opts with { … }`, and a mid-session change rebuilds client and agent and calls
`ChatSession.SetAgent` — the same path a model change takes, history preserved.

| Surface | Mechanism | Ladder |
|---|---|---|
| OpenAI-style | `enable_thinking` (measured: reasoning → 0) | two entries |
| Claude-style | `thinking.budget_tokens` (measured: 52 → 1184 chars) | four named tiers |
| Neither | — | **empty** |

`reasoning_effort` is rejected as the mechanism: measured model-dependent in both presence and
level — byte-identical across `minimal`…`high` on one model, ~6% apart on another. An empty ladder
beats a decorative one.

**Constraints.** One client instance per distinct effort level. Cheap — these are thin factories.

---

### 6.8 `EventTranslator`

| `AgentEvent` | ACP `session/update` |
|---|---|
| `AssistantTextDeltaEvent` *(new)* | `agent_message_chunk` |
| `TextReasoningContent` in the stream | `agent_thought_chunk` |
| `ToolStartedEvent` *(new)* | `tool_call` (pending) |
| `ToolInvokedEvent` + `CallId` *(new)* | `tool_call_update` (completed / failed) |
| `IterationCompletedEvent` | `usage` — `Size` = context window, `Used` = latest `InputTokenCount` |
| `PermissionRequestedEvent` | `session/request_permission` (v1: unreachable) |
| `AgentResultEvent` | terminal response (§8.3) |
| `SessionStartedEvent` | — swallowed, no ACP analogue |

**Usage semantics.** `Used` is **occupancy**, not cumulative: the latest iteration's input token
count. It falls when history is trimmed, which suits a client that sums only the rises. `Used` is
emitted unconditionally; `Size` is `0` when unknown.

---

### 6.9 Harness Deltas

Changes required inside `Agency.Harness`. Each is a first-class harness feature, not an adapter
workaround (P1).

| # | Delta | Shape |
|---|---|---|
| D-1 | `McpServerConfig.Headers` → `AdditionalHeaders` | **landed**, untested |
| D-2 | Log first-attempt status on MCP `AutoDetect` fallback | `DelegatingHandler` on the `HttpClientTransport(options, httpClient, …)` overload |
| D-3 | `QueryContext.IdentityPrompt` | re-land; one line in `SystemPromptBuilder` |
| D-4 | Repair-on-cancel | `finally` that closes an orphaned `tool_use` |
| D-5 | Dedicated timeout CTS | separate timeout from user cancel |
| D-6 | Truncation split from `Error` | `FinishReason == Length` → its own status |
| D-7 | **Streaming** | `GetStreamingResponseAsync` + `AssistantTextDeltaEvent`, reassembled by `ToChatResponseAsync()` |
| D-8 | `ToolStartedEvent` + `CallId` | appended optional member — public API, analyzers on |

**D-7 is the long pole.** The design keeps it contained: aggregate the update stream back into the
`ChatResponse` the loop already consumes, so the tool loop, usage extraction, `FinishReason` check
and stop conditions are untouched. The usage risk is retired — the OpenAI SDK sets
`stream_options: {"include_usage": true}` automatically, verified on the wire.

**Why D-4 matters more than its size.** Cancelling between the assistant message being appended and
the tool results being appended leaves a `tool_use` with no matching `tool_result`. Nothing repairs
it, and the damage surfaces on the *next* turn — a latent failure away from its cause.

---

### 6.10 `InferenceGate` (adjacent, separate repo)

**Purpose.** Enforce the per-box concurrency ceiling where every client passes (P7).

| | |
|---|---|
| **Vehicle** | a new component, not a mode of the cache proxy — "caching provably off" is cheapest to prove by the cache code not being compiled in |
| **Topology** | gate on `0.0.0.0:1234`, inference server moved to `127.0.0.1:1235` |
| **Mechanism** | `SemaphoreSlim(N)` around the forward; `finally` releases |
| **Health** | `/healthz` — capacity, in-flight, queue depth, upstream reachability |
| **Rejections** | absorbed and retried behind the semaphore, never passed through |

**Why absorb rejections regardless of measurement.** The harness retries three times on a
degenerate response with **no delay at all**. Passing a 4xx to a client with no backoff converts one
rejection into a storm.

**Reversibility conditions.** Gate autostarts with the box; `:1235` documented as the deliberate
on-box bypass; **no client config anywhere encodes `1235`**, so rollback is one edit.

**Known residual.** The gate bounds *concurrency*, not *model residency*. Loading exhausts a GPU
with no concurrent inference at all. A model-aware gate that drains pending requests for the
resident model before admitting a switch would close it — deferred, not impossible.

---

## 7. Data Model / State Layer

### 7.1 There is no persistence

`Agency.Acp` holds **no database, no files, no serialization**. All state is process memory and
dies with the process. This is a deliberate consequence of `supportsLoadSession: false`.

| Store | Shape | Lifetime |
|---|---|---|
| `SessionRegistry` | `ConcurrentDictionary<string, SessionState>` | process |
| `SessionState` | see 7.2 | session |
| `Context.Conversation` | `InMemoryConversationManager` | session |
| `Context.PendingToolBatch` | park checkpoint | turn, in-memory only |
| Writer queue | `Channel<string>` | process |

### 7.2 `SessionState`

| Field | Type | Source |
|---|---|---|
| `SessionId` | `string` | generated at `session/new` |
| `ChatSession` | `ChatSession` | constructed per session |
| `Options` | `AgentOptions` | **per-session copy**, carries `ContextWindowSize` |
| `McpPool` | `McpClientPool` | `mcpServers[]` |
| `Scope` | `IServiceScope` | disposed with the session |
| `ModelId` / `EffortId` | `string?` | `session/set_config_option` |
| `TurnCts` | `CancellationTokenSource?` | replaced per prompt |
| `InFlight` | `int` (Interlocked) | one-prompt guard |
| `Cwd` | `string` | recorded, unobservable |

### 7.3 Data flow

```text
session/new ──► mcpServers[] ──► McpClientPool ──► ITool[] ──► ToolRegistry ──► ToolContext
                                                                                    │
                catalogue ──► Model[] ──► AgentModelOption[]                         │
                     │                                                               ▼
                     └──► ContextLength ──► AgentOptions ──► ChatSession ──► Context ──► Agent
```

### 7.4 Backend differences

None inside the adapter. The only backend variance is at the provider edge (§6.7): which optional
`Model` fields are populated, and which thinking dialect the effort ladder maps onto. Both are
null-tolerant by construction.

---

## 8. Core Algorithms

### 8.1 Session creation

```text
1. validate mcpServers[] shapes                     → -32602 on malformed
2. resolve catalogue      (one GET; failure → [])
3. filter Kind == embedding where known
4. select model: requested ∈ catalogue ? requested : AgentOptions.DefaultModel
5. AgentOptions' = clone with ContextWindowSize = selected.ContextLength
6. build client (effort folded in) → Agent → ChatSession
7. McpClientPool.CreateAsync(mcpServers)            → per-server failures recorded, not thrown
8. register; return sessionId, models[], effortLevels[]
```

**Step 4 is the G6 contract**: an unknown model id is never an error. **Step 7 is fail-soft**: an
unreachable MCP server yields a session with fewer tools, not a failed session.

### 8.2 Turn execution

```text
1. Interlocked guard          → busy ⇒ JSON-RPC error
2. new CancellationTokenSource, store on SessionState
3. await foreach (AgentEvent e in ChatSession.SendAsync(text, ct))
      translate → session/update
      if AgentResultEvent { AwaitingPermission } → goto 4
      if AgentResultEvent { terminal }           → goto 5
4. request_permission ×N → ResumeWithPermissionsAsync → back to 3   ⟲
5. map status → respond once; clear guard and CTS
```

### 8.3 Stop-reason mapping

| `AgentResultStatus` | ACP | Note |
|---|---|---|
| `Success` | `end_turn` | |
| `MaxStepsReached` | `max_turn_requests` | default cap is 20 iterations |
| *truncation* (D-6) | `max_tokens` | today indistinguishable from `Error` |
| `Error` | **JSON-RPC error** | not a silent `end_turn` (P6) |
| `BudgetExceeded` | — | never produced; stop condition reads the always-zero cost |
| `AwaitingPermission` | — | consumed by the bridge (§6.4) |
| `OperationCanceledException` | `cancelled` | synthesised; no terminal event exists |

### 8.4 Error handling

| Condition | Response |
|---|---|
| Unknown method | `-32601` |
| Malformed params | `-32602` |
| Unknown session id | `-32602`, message names the id |
| Second prompt in flight | application error — client defect, fail loudly |
| MCP server unreachable | session succeeds with fewer tools; recorded in `FailedServers` |
| Catalogue unreachable | empty `models[]`; session still starts |
| Model cannot chat | JSON-RPC error on first turn. The message **must name the model and the likely cause** — e.g. `model 'x' returned no chat completion; it may be an embedding model` — because with `Kind` null this is the only signal, it arrives after the user has asked the Persona to speak, and their next action is to pick a different model |
| Harness exception | JSON-RPC error carrying the message |

---

## 9. Per-Turn vs Per-Session Work

| Work | Frequency | Cost |
|---|---|---|
| MCP connect + `tools/list` | per session | one round-trip per server |
| Catalogue fetch | per session, cached | one `GET` |
| Client + `Agent` construction | per session; again on model/effort change | negligible |
| System prompt rebuild | **per iteration** | pure function, no I/O |
| Tool definitions → `AITool[]` | per iteration | allocation only |
| LLM call | per iteration | dominant |
| Event translation | per event | allocation only |

**Why the system prompt is rebuilt every iteration** — it is how grounding never drifts. It is a
pure function of `Context`, so the cost is allocation, and it is what makes `IdentityPrompt` (D-3)
present on every iteration rather than only the first.

---

## 10. Background Workers / Async Components

| Component | Kind | Failure |
|---|---|---|
| stdin reader loop | long-running `Task` | EOF ⇒ graceful shutdown of all sessions |
| stdout writer | `Channel` consumer | broken pipe ⇒ terminate; client is gone |
| Per-turn agent task | `IAsyncEnumerable` | exception → JSON-RPC error |
| MCP transports | SDK-owned | per-server, recorded not thrown |
| `InferenceGate` | separate process | down ⇒ connection refused at `:1234` — loud by design |

**No hosted services in v1.** Memory's distiller, consolidator and hygiene workers are v2. This is
why "zero hot-path cost when memory is off" is literal rather than aspirational: memory attaches
only through `AgentOptions.BaselineHooks`, which is null when unregistered.

**Ordering constraint.** The reader must not await handlers. `session/cancel` arriving while
`session/prompt` runs is the whole point of the design; awaiting would deadlock it.

---

## 11. Performance Expectations

### 11.1 Latency budgets

| Operation | Target | Dominated by |
|---|---|---|
| `initialize` | < 50 ms | process already warm |
| `session/new` | < 500 ms | one catalogue `GET` + MCP `tools/list` |
| First `agent_message_chunk` | < 2 s warm | model TTFT |
| First chunk, cold model | **10–60 s** | model load — physics, not a defect |
| `session/cancel` → response | < 500 ms | cooperative cancellation |
| Event translation | < 1 ms | allocation |

### 11.2 Throughput ceiling

Bounded by the gate, not the adapter. With *N* = 2 and six Personas, the sixth waits roughly three
inference rounds. `LlmClientOptions.Timeout` must exceed queue time plus generation.

### 11.3 Scaling notes

- **Process footprint** is dominated by `Microsoft.PowerShell.SDK` 7.6.3, which `Agency.Harness`
  hard-references even though §6.5 registers no shell tool. Measuring this may justify splitting
  the built-in tools out — the dependency is pure cost here.
- **Sessions are cheap**; models are not. Scale sessions freely; scale distinct models carefully.
- The harness retries three times on empty choices with 250 ms linear backoff, and three times on a
  degenerate response with **no delay**. Six adapters against a stalled endpoint is a storm — §6.10
  absorbs it.

---

## 12. Edge Cases and Failure Modes

| # | Case | Behaviour | Severity |
|---|---|---|---|
| E-1 | Cancel between assistant message and tool results | orphaned `tool_use` breaks the **next** turn | **high** — D-4 is a prerequisite |
| E-2 | Turn timeout vs user cancel | indistinguishable today; both `OperationCanceledException` | **high** — D-5 |
| E-3 | Streamed usage absent | `Used` silently zero | mitigated: SDK sets `include_usage` |
| E-4 | Stray `Console.WriteLine` | corrupts the JSON-RPC stream | **high** — asserted |
| E-5 | MCP server unauthenticated | reports `404`, not `401` (AutoDetect falls back to SSE) | medium — D-2 |
| E-6 | Two MCP servers, same tool name | last-write-wins, silently | **not reachable in v1** — the first client sends exactly one server; live only for a multi-server client |
| E-7 | Embedding model selected | fails at **first inference**, not at selection | medium — filtered where `Kind` known; §8.4 requires the error name the model and the likely cause |
| E-8 | Cold model | 10–60 s to first chunk | expected — surfaced by residency |
| E-9 | Model never evicted | VRAM exhaustion | **environmental** — outside the adapter |
| E-10 | Park while cancelling | park must be cleared | medium |
| E-11 | Second prompt in flight | JSON-RPC error | low — client defect |
| E-12 | Client dies mid-turn | broken pipe → **dispose every session graph**, then terminate | low |
| E-15 | Client never sends `session/close` | graph disposed on disconnect instead; `OnSessionEnd` still fires | **high under multi-session** — the observed behaviour of the first client |
| E-13 | `session/cancel` with nothing in flight | no-op | low — required |
| E-14 | Model lacks tool support | model ignores tools; Persona cannot act | medium — no reliable signal exists |

**E-14 has no clean answer.** `capabilities` on richer servers is demonstrably wrong — an embedding
model reports `tool_use` while another reports nothing — so it cannot be gated on.

---

## 13. End-to-End Flow

```text
 1  Huddle spawns  agency-acp --Agent:UserId=<guid>            [process start]
 2  → initialize {protocolVersion: 1}
    ← {agentName, agentVersion, authMethods: [], supportsLoadSession: false}
 3  Huddle starts AppToolServer on loopback, mints a bearer token
 4  → session/new {cwd, mcpServers: [{team, http://127.0.0.1:p/mcp, Headers}]}
      · catalogue GET  → Model[]  → filter embeddings → AgentModelOption[]
      · McpClientPool  → initialize → tools/list → ITool[] → ToolRegistry
      · AgentOptions'  → ChatSession
    ← {sessionId, models[], effortLevels[]}
 5  → session/set_config_option {model}          → rebuild client+Agent → SetAgent
 6  → session/prompt {"[#product] Ana: summarise the thread"}
      ├─ iteration 1: system prompt rebuilt · LLM streams
      │   ← agent_message_chunk ×N
      │   ← tool_call get_help (pending) → tool_call_update (completed)
      ├─ iteration 2: LLM streams the answer
      │   ← agent_message_chunk ×N
      │   ← usage {size, used}
    ← {stopReason: "end_turn"}
 7  → session/cancel                              [human hits Stop]
      · TurnCts.Cancel() — safe from any thread, no-op when idle
      · OperationCanceledException unwinds; partial text retained
      · transcript repaired (D-4)
    ← {stopReason: "cancelled"}
 8  → session/close  [OPTIONAL — the first client never sends it]
      · disposal is driven by whichever arrives first:
        session/close  |  transport disconnect  |  process shutdown
      · dispose pool, scope, ChatSession (fires OnSessionEnd once)
```

---

## 14. Design Notes / Rationale

**Why ACP rather than an in-process library seam.** A crashed local model cannot take the client's
web app down. And ACP lets Agency be driven by anything — Zed, buzz, a future client — which serves
adoption better than a bespoke seam. The cost is a protocol server that did not exist; the counter
was "there is no C# ACP library", which `dotacp` retires.

**Why the adapter takes `dotacp.protocol` rather than hand-rolling DTOs.** Wire compatibility
becomes *structural* rather than a matter of reading the client correctly. The package is
side-neutral — it carries both method tables — and its converters already solve union
discrimination and enum spellings that default `JsonSerializerOptions` get wrong.

**Why multi-session now.** The three blockers — working directory, user identity, permission grants
— are each neutralised by a *different* v1 scope decision. Building it later means unpicking a
single-session assumption; building it now costs a registry and honest documentation. It becomes
expensive again the moment memory or file tools arrive, which is precisely when the guarantee test
will fail and force the conversation.

**Why effort is per-client, not per-request.** Because the existing machinery already fits: each
session gets its own `Agent`, `LlmClientOptions` is a `record` already used with `with { }` to vary
behaviour, and `SetAgent` preserves history across a swap. Per-request plumbing would touch the
agent loop, the shared contract and both providers to buy turn-by-turn variation nobody asked for.

**Why the empty registry is not a config switch.** `AddAgencyAgent()` registers no tools; the
Console adds them by hand. So "no shell tool" is the default and the guarantee is that nothing adds
one. A switch would imply tools exist and are suppressed — a weaker property, and one an operator
could flip.

**Why the fifth lock is behavioural.** You cannot statically prove a delegate never returns `Ask`.
A structural substitute fails both ways: forbidding `OnPreToolUse` would block a legitimate rewrite
hook, while allowing it would pass while a future `Ask` slips in. Driving a turn and asserting no
`PermissionRequestedEvent` closes the path by behaviour — and enumerating the registry keeps
coverage from decaying as tools are added.

**Why usage is occupancy rather than cumulative.** One config key must mean the same thing across
backends. Cumulative tokens would count output and every re-read of a growing prompt across up to
20 iterations, so the same budget value would mean two different things depending on the backend.

**Why vendor metadata is optional fields rather than an interface.** An `IModelCatalogEnricher`
with one real implementation and a no-op default is an abstraction for single-use code. Optional
nullable fields on `Model` express exactly the same thing, degrade identically, and add no type.

**Why the gate is a separate component on the inference port.** Separate, because "caching provably
off" is cheapest to prove by the cache code being absent. On the port, because a guard the
offending client can route around is a convention — the incident that motivated this was a client
hitting the port directly, from outside any adapter.

---

## 15. Test-First Task Plan

Every implementation task is preceded by its test task. Tests are written first and must fail for
the right reason before implementation begins.

### Phase 1 — harness deltas

| # | Test task (first) | Implementation task |
|---|---|---|
| T-1 / I-1 | Unit: `Headers` on `McpServerConfig` reach `HttpClientTransportOptions.AdditionalHeaders` | **landed** — test outstanding |
| T-2 / I-2 | Unit: a tokenless connect logs the first-attempt status, not the fallback `404` | D-2 |
| T-3 / I-3 | Unit: `IdentityPrompt` replaces the identity line and appears on **every** iteration | D-3 |
| T-4 / I-4 | Unit: cancel mid-batch, then prompt again — transcript holds no `tool_use` without `tool_result` | D-4 |
| T-5 / I-5 | Unit: a turn timeout is distinguishable from a user cancel | D-5 |
| T-6 / I-6 | Unit: `FinishReason == Length` yields the truncation status, not `Error` | D-6 |
| T-7 / I-7 | Unit (scripted fake client): deltas precede the terminal event; aggregated response equals the non-streamed one; `TotalUsage` non-zero | **D-7 streaming** |
| T-8 / I-8 | Unit: `ToolStartedEvent` precedes dispatch; `CallId` correlates start and completion | D-8 |
| T-9 / I-9 | Unit: memory options bind from configuration; a Claude-typed client constructs a Claude client | O9a / O9b |
| T-10 | Functional (`RequiresLlm`): streamed tool round-trip, multi-tool batch, usage-on-stream, cancel-mid-stream | — |

### Phase 2 — the gate

| # | Test task (first) | Implementation task |
|---|---|---|
| T-11 / I-11 | Unit: two identical requests produce two upstream hits; assembly holds no reference to the cache | gate component |
| T-12 / I-12 | Unit: concurrency *N*+2 never exceeds *N* in flight; a killed client releases its slot | semaphore |
| T-13 / I-13 | Unit: `/healthz` distinguishes gate-up/upstream-down from gate-up/upstream-up | health |
| T-14 / I-14 | Functional: an upstream rejection is retried behind the semaphore, never passed through | absorb |

### Phase 3 — the adapter

| # | Test task (first) | Implementation task |
|---|---|---|
| T-15 / I-15 | Unit: framing round-trips; writes never interleave under concurrent emission | `StdioTransport` |
| T-16 / I-16 | Unit: **stdout carries protocol bytes only** — no log line can reach it | E-4 |
| T-17 / I-17 | Unit: unimplemented methods return `-32601`; `initialize` reports the agreed capabilities | `MethodDispatcher` |
| T-18 / I-18 | Unit: two sessions have independent history, tools and model; disposal releases the pool | `SessionRegistry` |
| T-19 / I-19 | Unit: an unknown model starts on the default; an unreachable MCP server yields fewer tools, not failure | §8.1 |
| T-20 / I-20 | Unit: a two-park turn yields **exactly one** terminal response | park bridge |
| T-21 / I-21 | Unit: each `AgentEvent` maps to its `session/update`; usage is occupancy | `EventTranslator` |
| T-22 / I-22 | Unit: every status maps per §8.3; cancel synthesises `cancelled` | stop reasons |
| T-23 / I-23 | **The guarantee** — five separately-named assertions, fifth behavioural, enumerating the App Tool registry | §6.5 |
| T-24 / I-24 | Unit: effort varies per session; a change rebuilds the agent and preserves history | §6.7 |
| T-25 | E2E: an ACP client drives a full turn — O-1, O-2, O-3, O-5 at the protocol boundary | **the Agency half of the milestone** |

**T-25 proves the Agency half of the milestone**, and everything above it is in service of it — but it is not the milestone. The agreed end state is *two Personas in one Room, live chunk rendering, and a Stop click leaving both resumable*; the last three properties live in the client's repository and are proven there. The milestone is a **joint run** that neither side's plan contains alone. See the project plan's *The joint run*.

---

## 16. Decision Log

Four architectural forks resolved during the grilling session, with the reasoning that decided
each.

| # | Decision | Rejected | Why |
|---|---|---|---|
| G-1 | **Multi-session capable** | strict single-session; full isolation | The three blockers are each neutralised by a different v1 scope decision, so it is nearly free now and expensive later |
| G-2 | **Optional fields on `Model`** | enricher interface; operator config; carry nothing | Same degradation shape as `Description: null`, and no abstraction for single-use code |
| G-3 | **Single exe, launched by path** | dotnet tool; library + host | Matches `Agency.Harness.Console` exactly; no CI package-gate change; reuse case is speculative |
| G-4 | **Effort per-client via `SetAgent`** | per-request `ChatOptions`; `AgentOptions` | Existing machinery already fits; `AgentOptions` is a singleton so it cannot vary per session |

### Candidate ADRs

Three decisions meet all three bars — hard to reverse, surprising without context, a real
trade-off — and are worth recording under `docs/adr/` once approved:

- **ACP as the integration seam** rather than an in-process library.
- **The gate on the inference port**, with the server moved to loopback.
- **Vendor metadata as optional fields**, establishing P2 for every future provider.

G-1, G-3 and G-4 do not meet the bar: each is cheaply reversible and unsurprising in context.

---
