# Agency.Acp

## What It Is

`Agency.Acp` is a standalone executable that exposes the Agency agent harness as an **[Agent Client Protocol](https://agentclientprotocol.com) (ACP) agent**: a process speaking newline-delimited JSON-RPC 2.0 over stdio, so any ACP client can drive an Agency agent without linking against it.

It is **a protocol translator, not a second agent loop**. Prompting, tool dispatch, hooks and stop conditions all stay in [Agency.Harness](Agency.Harness.md). This project does exactly four things:

1. Decode JSON-RPC from stdin; encode responses and notifications to stdout.
2. Own session lifetime — construct and dispose the per-session object graph.
3. Translate `AgentEvent` (Agency's turn stream) into `session/update` notifications.
4. Hide harness mechanics that have no ACP representation — principally the permission park/resume round-trip, which must look like one continuous `session/prompt`.

**Namespace:** `Agency.Acp`

The design spec is [`docs/specs/Agency.Acp-Specifications.md`](../specs/Agency.Acp-Specifications.md).

## Prerequisites

- An LLM endpoint configured under `Agent:LLmClients`, with `Agent:DefaultClientName` and `Agent:DefaultModel` resolving to one of them. The contract is **OpenAI-style or Claude-style HTTP** — no vendor-specific endpoint is on the required path.
- Nothing else. There is **no database, no files, no serialization**: all state is process memory and dies with the process. That is a deliberate consequence of `supportsLoadSession: false`.

### Configuration

`agency-acp` ships an `appsettings.json` alongside the executable, so the process starts without
any environment configuration. The shipped defaults are deliberately minimal:

| Key | Default | Note |
|---|---|---|
| `Agent:DefaultClientName` | `local` | matches the single client below |
| `Agent:DefaultModel` | `REPLACE_ME_WITH_YOUR_MODEL_ID` | **placeholder — override for real use** |
| `Agent:LLmClients[0]` | `local`, `OpenAI`, `http://localhost:1234/v1` | the generic local-server default; no vendor endpoint is on the required path |
| `Skills:DisableShellExecution` | `true` | mirrors a hardcoded lock — see below |

**Environment variables override the file.** Configuration sources are layered, lowest precedence
first:

```text
shared-appsettings.json  →  appsettings.json  →  appsettings.{Environment}.json
                         →  environment variables  →  command line
                         →  hardcoded in-memory lock   (highest)
```

So `Agent__DefaultModel=qwen/qwen3.6-35b-a3b` wins over the shipped placeholder, as does
`Agent__DefaultClientName`, `Agent__LLmClients__0__BaseUrl`, and so on — double underscore separates
configuration sections. A host that already supplies these (as an embedding client typically does)
is unaffected by the shipped file.

**`Skills:DisableShellExecution` cannot be relaxed by configuration.** It appears in the file for
documentation only. `Program.BuildHost` adds it again as a final in-memory source, which outranks
both the file and the environment — setting it `false` in `appsettings.json` or via
`Skills__DisableShellExecution=false` has no effect. Shipping a configuration file must not create
a route to unlocking a v1 safety guarantee.

**The file is not in the NuGet package.** `appsettings.json` is copied to the build output but
packed with `Pack="false"`, so `AgencyDotNet.Acp` contains only `lib/`. A downstream
`PackageReference` therefore never has a placeholder config copied into its own build output.

## API Surface

This is an executable (`<OutputType>Exe</OutputType>`). Every type is `internal`, exposed to the test project via `[assembly: InternalsVisibleTo("Agency.Acp.Test")]`. There is no public API intended for consumption by other libraries — the *protocol* is the surface.

Wire types come from the **`dotacp.protocol`** package (pinned `2026.7.19`), which carries both sides' method tables.

> **`dotacp.protocol` is Newtonsoft.Json-based, not System.Text.Json.** Its DTOs carry `[JsonProperty]` plus attribute-attached converters (`DiscriminatorConverter<T>`, `UnionTypeConverter<T>`, `JsonEnumMemberConverter<T>`), so plain `JsonConvert` / `JObject` with **default** settings produces the correct wire form. Do not hand-roll converters or build a settings object. The package ships **no JSON-RPC envelope type**, so `MethodDispatcher` builds `{jsonrpc, id, result|error}` itself.

### Components

| Type | Role |
|---|---|
| `Transport/StdioTransport` | One reader loop, one serialized writer |
| `Dispatch/MethodDispatcher` | Routes `AgentMethods` constants to handlers; `-32601` for the rest |
| `Sessions/SessionRegistry` | `ConcurrentDictionary<string, SessionState>`; owns session lifetime |
| `Sessions/SessionFactory` | The `session/new` algorithm |
| `Sessions/SessionState` | The per-session graph |
| `Turns/TurnDriver` | One `session/prompt` → exactly one terminal response |
| `Turns/StopReasonMapper` | `AgentResultStatus` → ACP `StopReason` |
| `Turns/EventTranslator` | `AgentEvent` → `session/update` |
| `Permissions/PersonaPermissionEvaluator` | Allow the granted set, deny everything else, never ask |

### Supported methods

| Method | Supported |
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

Unsupported methods return `-32601 Method not found` rather than failing silently.

## How It Works

### Transport

A single reader loop deserializes each line and hands it to the dispatcher **without awaiting the handler**. This is not an optimisation — if the reader awaited, `session/cancel` could never overtake an in-flight `session/prompt`, which is the whole point of the design.

All writes funnel through one `Channel<string>` drained by a single writer task, so notifications emitted concurrently by several sessions never interleave mid-frame.

**stdout is protocol-only.** Every log sink goes to stderr or a file, and the composition root calls `Console.SetOut(TextWriter.Null)` after capturing the real handle, so a stray `Console.WriteLine` anywhere in the process — including inside a dependency — cannot corrupt the stream. This is asserted by `StdoutPurityTests`.

### Model catalogue and effort

> **The spec's `models[]` / `effortLevels[]` / `AgentModelOption` vocabulary does not exist in the package.** `NewSessionResponse` is `{ Meta, ConfigOptions, Modes, SessionId }`. Both the model catalogue and the effort ladder are expressed as `SessionConfigOption` → `SessionConfigSelect` → `SessionConfigSelectOption { Name, Value, Description }`, where `Value` is the model id and `Description` carries residency.

Model metadata is **enrichment, never a requirement**. [Agency.Llm.Common](Agency.Llm.Common.md)'s `Model` carries optional `Kind`, `ContextLength` and `IsLoaded`; providers fill what their server answers and leave the rest `null`. **`null` means *unknown*, never *false*** — a model with unknown residency renders as a plain row, not as "not loaded". `Description` has exactly three states: `"loaded now"` when `IsLoaded == true`, `"not loaded"` when the provider **explicitly** reported `false`, and absent when it is `null`. The adapter never *infers* non-residency; it does relay it when a server states it.

Effort is **per-client, not per-request**: each session builds its client with `opts with { … }`, and a mid-session change rebuilds client and agent and calls `ChatSession.SetAgent`, preserving history. OpenAI-style surfaces use `enable_thinking`; Claude-style use `thinking.budget_tokens`. Where neither applies the ladder is **empty** — an empty ladder beats a decorative one.

`reasoning_effort` is deliberately **not** emitted: it was measured model-dependent in both presence and level, byte-identical across `minimal`…`high` on one model.

### Session creation degrades rather than fails

```text
1. validate mcpServers[] shapes                     → -32602 on malformed
1a. parse _meta.systemPrompt → identityPrompt       → null on absent/unknown shape (never an error)
2. resolve catalogue      (one GET; failure → [])
3. filter Kind == embedding where known
4. select model: AgentOptions.DefaultModel
5. AgentOptions' = clone with ContextWindowSize = selected.ContextLength
6. build client (effort folded in) → Agent → ChatSession(identityPrompt)
7. McpClientPool.CreateAsync(mcpServers)            → per-server failures recorded, not thrown
8. register; return sessionId + ConfigOptions
```

**Step 1a is fail-soft:** a missing, empty or unrecognised `_meta.systemPrompt` yields `null` — the default identity line — and never fails session creation. See [Identity](#identity). **Step 4:** `session/new` always starts on `Agent:DefaultModel`; a client switches models afterwards via `session/set_config_option`, and an unknown model id is *never* an error — a stale stored choice must not block a session. **Step 7 is fail-soft:** an unreachable MCP server yields a session with fewer tools, not a failed session.

### Identity

A Persona supplies **who it is**; the harness supplies **how it works**. `session/new` carries the
Persona's identity in `_meta.systemPrompt`, and it replaces exactly one thing: the opening identity
line of the system prompt.

```jsonc
// session/new params
{
  "cwd": "/work",
  "mcpServers": [ /* ... */ ],
  "_meta": { "systemPrompt": { "append": "You are Ana, who routes requests to specialists." } }
}
```

**Populating `_meta` from a client.** `dotacp.protocol`'s `NewSessionRequest` has **no typed
identity field**. Clients set `NewSessionRequest.Meta` — a `Dictionary<string, object>` tagged
`[JsonProperty("_meta")]` — while `MethodDispatcher` reads the raw token at
`@params?["_meta"]?["systemPrompt"]`. The two ends are joined only by the wire property name, with
**no compile-time link**, so a typo on either side fails silently rather than failing to build.
`EndToEndAcpTests` demonstrates the client side.

**Two accepted shapes, one meaning.** `{"append": "..."}` and a bare string `"..."` are treated
identically. A bare string is not a second mode — it is shorthand for the first.

**Append semantics, and why a replace is refused.** "Append" means *replace the identity line, keep
everything else*. The ReAct reasoning instruction, the skills catalogue and the temporal/environment
grounding are harness invariants and survive every identity. There is deliberately no replace mode:
`QueryContext.IdentityPrompt` structurally cannot express one — `SystemPromptBuilder` substitutes a
single line and assembles the rest unconditionally — and a true replace would strip the scaffolding
the agent loop depends on, making a Persona look broken rather than misconfigured.

**Present on every iteration.** The system prompt is a pure function of the `Context`, rebuilt on
each loop iteration, so identity is re-emitted every time rather than injected once. A long identity
is therefore a real per-iteration token cost — a 2 KB Persona prompt across 20 iterations is roughly
10k tokens of repeated input.

**Per-session, not per-turn.** Identity is fixed for the life of the session. `_meta` on
`session/prompt` is not read: `Context.Query` is `init`-only, so a mid-session identity change would
mean rebuilding the `Context` and losing history. Changing a Persona's identity means a new session.

**Fail-soft, always.** Absent, empty, whitespace-only, or an unrecognised shape (`{}`, an array, a
number, `{"append": 42}`) all yield "no identity" — the runtime's default line — and the session is
created normally. An unrecognised shape is tolerated rather than rejected so a forward-compatible
client cannot break against an older adapter; the parse outcome and identity length are logged at
`Debug` so a silently-dropped identity is still visible.

**Survives a model change.** `session/set_config_option` rebuilds the client and `Agent` and calls
`ChatSession.SetAgent`, which preserves the existing `Context` — so the identity survives a model
swap for free.

**No bleed between sessions.** Identity is written once at session creation and read once per
iteration, with no mutation path. Two sessions hold two `Context` instances, each with its own
immutable `QueryContext`, so two Personas in one process cannot see each other's identity.

### Disposal is driven by three triggers, not one

The per-session graph is disposed on **`session/close`, transport disconnect, or process shutdown — whichever comes first**, and disposal is idempotent.

> `session/close` is optional in the protocol and **the first real client never sends it**. Disposal must therefore never *depend* on it; `session/close` only releases resources **sooner**. Under multi-session this is the difference between a bounded process and one that holds every `McpClientPool` and DI scope ever created.

### Turn execution

One `session/prompt` yields exactly one terminal response, whatever the harness does in between. An `Interlocked` guard admits one prompt per session; a second returns a JSON-RPC error rather than queueing, because overlapping prompts indicate a client defect and should fail loudly.

The park/resume path is a **loop**, not a single round-trip — one prompt may park several times. In v1 that path is unreachable in production (the evaluator never returns `Ask`) but is **fully implemented and tested**, driven by a test-only evaluator.

Cancellation emits **no terminal `AgentResultEvent`** — the exception is the signal, and the driver synthesises the stop reason.

### Stop-reason mapping

| `AgentResultStatus` | ACP |
|---|---|
| `Success` | `EndTurn` |
| `MaxStepsReached` | `MaxTurnRequests` |
| `Truncated` | `MaxTokens` |
| `Error` | **a JSON-RPC error**, not a silent `end_turn` |
| `OperationCanceledException` | `Cancelled` (synthesised) |
| `AwaitingPermission` | consumed by the permission bridge; never reaches the client |

### Event translation

| `AgentEvent` | ACP `session/update` |
|---|---|
| `AssistantTextDeltaEvent` | `agent_message_chunk` |
| `AssistantThoughtDeltaEvent` | `agent_thought_chunk` |
| `ToolStartedEvent` | `tool_call` (pending) |
| `ToolInvokedEvent` (correlated by `CallId`) | `tool_call_update` |
| `IterationCompletedEvent` | `usage` |
| `SessionStartedEvent` | — swallowed, no ACP analogue |

**Usage is occupancy, not cumulative:** `Used` is the latest iteration's input token count, so it *falls* when history is trimmed. Cumulative tokens would count output and every re-read of a growing prompt across up to 20 iterations, so the same budget value would mean two different things on different backends. `Used` is emitted unconditionally; `Size` is `0` when unknown.

Tool names travel **unmodified** — neither the harness nor the adapter mints an `mcp__server__tool` prefix — and no `ToolKind` classification is performed. (`ToolCall.Kind` is a non-nullable enum defaulting to `Read`, so "unclassified" is expressed as the explicit `ToolKind.Other` sentinel.)

## The v1 safety guarantee

`Agency.Acp` registers **no filesystem or shell tools**. This is a *tested guarantee*, not an accident of nobody having added one — a property that holds by omission is a property that will be removed by accident.

`V1GuaranteeTests` is one test method calling six independently-named assertions, so a regression names itself:

| Lock | Closes |
|---|---|
| `NoBuiltInToolsRegistered` | `read_file`, `write_file`, `execute_powershell`, `subagent_tool` |
| `SkillToolNotRegistered` | model-invoked skills |
| `NoShellRunnerWired` | shell expansion suppressed entirely |
| `SkillShellExecutionDisabled` | `` !`cmd` `` expansion in `SKILL.md` |
| `NoHookCanReturnAsk` | the permission park path |
| `PermissionEvaluatorIsWired` | the gate being absent rather than permissive |

**`NoHookCanReturnAsk` is behavioural, not structural, by design.** You cannot statically prove a delegate never returns `Ask`; an evaluator `Allow` does *not* clear a hook `Ask`, and a hook `Ask` parks the turn even with no evaluator supplied. So it drives a real turn calling **every tool enumerated from the registry** (never a hardcoded list, so a tool added later is covered the day it is registered) and asserts zero `PermissionRequestedEvent`. Do not "simplify" it into a structural check — that would pass while the path stayed open.

`PermissionEvaluatorIsWired` exists because the evaluator was once fully implemented, fully tested, and **never registered** — harmless only for as long as no tool existed to deny.

## Known limitations

- **Session-scoped working directories are unreachable.** `cwd` from `session/new` is recorded on `SessionState` for diagnostics only; no tool can observe it, because no file or shell tool is registered. Documented, not silently ignored.
- **Process-global state is shared across sessions.** `AgentOptions.UserId`, credentials and the permission rules file are process-wide and documented as such. This is acceptable only while memory is v2 and no file tools exist. ACP carries no user identity on `session/new`, so per-session identity needs a *carrier* — most plausibly `session/new`'s `_meta`.
- **`TotalCostUsd` is structurally `0m`.** No price table exists anywhere in the solution, so every USD budget guard is inert. Do not build on it.
- **A model that cannot chat fails at first inference, not at selection.** Where `Kind` is `null` an embedding model cannot be filtered out, so the error must name the model and the likely cause.
- **Process footprint** is dominated by `Microsoft.PowerShell.SDK`, which `Agency.Harness` hard-references even though no shell tool is registered here.

## Related

- [Agency.Harness](Agency.Harness.md) — the agent loop this translates for
- [Agency.Llm.Common](Agency.Llm.Common.md) — `Model` metadata and `LlmClientOptions` effort fields
- [ADR 0003 — ACP as the integration seam](../adr/0003-acp-as-the-integration-seam.md)
- [ADR 0005 — Vendor metadata as optional fields](../adr/0005-vendor-metadata-as-optional-fields.md)
