# Configuration-Hydrated Hooks — Design Specifications (HLD)

**Status:** Proposed (Phase 2)
**Audience:** Implementers, reviewers, future maintainers
**Scope:** `Agency.Harness` (new `Hooks/Configuration/` subsystem), `Agency.Harness.Console` (composition root)
**Source plan:** `docs/HookPhase2.md`

**Last revised:** 2026-06-04 — initial specification derived from the Phase-2 plan. Scope confirmed with the requester: configuration lives in the `appsettings.json` `"Hooks"` section; the Phase-1 handler surface is **Command + HTTP**; the deliverable is a full implementation plus a unit-test suite.

---

## 1. Goal

Give `Agency.Harness` a **declarative, configuration-hydrated hook system** — modelled on the Claude Code hook contract (`event → matcher → external handler`, expressed in JSON) — **without changing the agent loop and without weakening the existing compiled-C# hook model**.

Today an `AgentHooks` delegate can only be authored in C# and compiled into the host. That is powerful for first-party policy (memory retrieval, audit, block-lists) but closed to operators: there is no way to attach a guard, an audit webhook, or a tool-rewrite to a deployment *from configuration*. This system closes that gap by introducing a layer that **reads hook declarations from config and projects them onto the existing `AgentHooks` record**, which `Agent.cs` already fires at nine lifecycle points.

The system answers three questions for every configured deployment:

1. **What external behaviour should fire at this lifecycle point?** (Declaration — the `"Hooks"` config section.)
2. **Does this declaration apply to the thing that just happened?** (Matching — tool-name matchers.)
3. **What should the agent do with the handler's verdict?** (Projection — map external output onto `PreToolUseDecision` and the existing loop semantics.)

Success is measured by four properties:

| Property | Measurement |
|---|---|
| **Loop transparency** | `Agent.cs` is byte-for-byte unchanged. The loop still consumes only an `AgentHooks` record. |
| **Behavioural parity** | A config-declared `PreToolUse` deny blocks a tool identically to the existing C# `BlockListHooks.Dangerous` deny — same `[Blocked] {reason}` surface, same deny-wins precedence. |
| **Zero-config neutrality** | With no `"Hooks"` section (or an empty one), agent behaviour is statistically and semantically identical to a build without this feature. |
| **Composability** | Config hooks compose with first-party baseline hooks (memory) and ad-hoc user hooks via the existing `AgentHooksExtensions.Compose`, preserving deny-wins across all sources. |

This is **not** a new hook-firing mechanism. The harness already fires hooks. This system is a **producer of `AgentHooks`** that happens to be driven by JSON rather than by a compiler.

---

## 2. Example Use Cases

| # | Scenario | What happens |
|---|---|---|
| **U1** | An operator wants to block any shell tool call containing `rm -rf` without rebuilding the host. | They add a `PreToolUse` matcher group (`matcher: "Bash\|ExecutePowershell"`) with a `command` handler pointing at `guard.ps1`. The script reads the payload on stdin, sees the pattern, prints `{"hookSpecificOutput":{"permissionDecision":"deny","permissionDecisionReason":"rm -rf blocked"}}`, exits 0. The agent's tool call is blocked with that reason. |
| **U2** | A security team wants every tool *result* mirrored to a SIEM. | They add a `PostToolUse` matcher group (`matcher: "*"`) with an `http` handler POSTing the payload to `http://collector/audit`. Each tool result is shipped fire-and-forget; the handler's response is ignored for control flow. |
| **U3** | A platform team wants to normalise a tool argument (e.g. force `--no-color`) before execution. | A `PreToolUse` command handler reads `tool_input`, emits a rewritten `tool_input` in its stdout JSON; the registry maps it to `PreToolUseDecision.Rewrite`, and the loop substitutes the new input before invoking the tool. |
| **U4** | A handler script crashes / times out. | The command handler kills the process tree on timeout and returns a **non-blocking error**; the projection logs it and treats it as `Allow`. The agent is never wedged by a broken hook. |
| **U5** | Two matcher groups both match `Bash`; one allows, one denies. | `AggregateDecision` folds in config-declared order with the existing precedence — Deny wins over Rewrite wins over Allow — so the deny is authoritative regardless of which handler finished first. |
| **U6** | A config hook says `allow` but the compiled `BlockListHooks.Dangerous` says `deny` for the same call. | The two are merged by `Compose`; `CombinePreToolUse` enforces deny-wins, so the compiled deny is authoritative. Config cannot widen first-party policy. |
| **U7** | A deployment ships no `"Hooks"` section. | `HookRegistry.Empty.ToAgentHooks()` returns `AgentHooks.None`; every delegate is `null`; the loop keeps its "null = skip" fast path. No processes spawn, no allocations on the hot path. |

These cases drive the design decisions throughout this document.

---

## 3. Non-Goals

The following are **explicitly out of scope for Phase 2 (V1 of this subsystem)**:

| Out of scope | Reason |
|---|---|
| **`mcp_tool`, `prompt`, `agent` handler kinds** | These need the MCP client pool / an LLM client and a richer execution context. The enum reserves them; the factory throws `NotSupportedException`. Deferred to V2. |
| **A dedicated `.claude/settings.json`-style file loader** | Confirmed: V1 binds the existing `appsettings.json` `"Hooks"` section via `IConfiguration`. A bespoke layered file loader is a V2 convenience. |
| **Runtime hot-reload of hook config** | Hooks are compiled once at startup (matchers + handlers resolved in the registry ctor). `IOptionsMonitor`-style live reload is V2. |
| **Matcher semantics for non-tool events** | Matchers gate on **tool name** only. For non-tool events (SessionStart, Stop, …) the matcher is treated as match-all in V1. Gating on session id / start-reason is V2. |
| **`continue: false` / loop-abort from non-tool handlers** | The existing non-tool delegates return `Task` (no decision channel). Only `PreToolUse` can block in V1. Surfacing `systemMessage` / loop-abort for other events is V2. |
| **`ask` / deferred permission** | Claude Code's `ask` / `defer` permission decisions collapse to `Allow` in V1 (documented). There is no interactive permission UI in the harness loop to honour `ask`. |
| **Shell-form command handlers (pipes, `&&`, redirects)** | V1 spawns executables directly via `ArgumentList` (injection-safe). Shell interpolation is intentionally not supported. |
| **Per-handler concurrency / rate limiting** | Handlers within a matched set run via `Task.WhenAll`; there is no global handler scheduler or throttle in V1. |
| **Handler output schema validation** | V1 tolerantly parses known fields; it does not reject unknown JSON or enforce a strict schema. |
| **Secrets management for HTTP headers** | Header values are taken verbatim from config. Environment-variable interpolation / secret stores are V2. |

Drawing this line keeps V1's surface narrow enough to measure against real operator usage before adding the heavier handler kinds (`mcp_tool`, `prompt`, `agent`) and live reload.

---

## 4. Design Principles

Six principles govern the subsystem. Every component-level decision in §6–§13 traces back to one of them.

### P1 — The loop is closed

`Agent.cs` consumes exactly one type: `AgentHooks`. This subsystem is a *producer* of that type and nothing else. No new firing point, no new delegate signature, no edit to the loop.

**Why this matters:** the nine firing points in `Agent.cs` are already correct and tested. Reusing them means config hooks inherit deny/rewrite semantics, parallel-tool batching, and event ordering for free, and the blast radius of this change is a new folder plus three small edits in the composition root.

### P2 — Declaration is data; behaviour is a delegate

A hook is *declared* as inert config (`event → matcher → handler`) and *realised* as a delegate by the registry's projection. The two are never conflated: config POCOs hold no behaviour; delegates hold no config.

**Why this matters:** it mirrors the existing `McpServerConfig` → `McpClientPool.CreateTransport` split — POCO in, runtime object out — which the team already understands. It also makes the projection the single, testable seam between "what was written" and "what runs".

### P3 — Compile once, match many

Matchers and handlers are resolved **once**, in the registry constructor (regex compiled, pipe-lists hashed, handler factories bound), exactly as `ToolRegistry` resolves tool definitions in its ctor. Per-invocation work is a dictionary lookup plus a matcher test.

**Why this matters:** `PreToolUse` fires on every tool call. Recompiling a regex or re-parsing config per call would put avoidable CPU on the path that gates tool execution. Compile-once keeps matching O(1)-ish and ReDoS-bounded.

### P4 — The agent never wedges on a hook

A handler that crashes, times out, returns malformed output, or exits non-zero in a non-blocking way is logged and treated as `Allow`. Only an explicit deny (exit 2, or `permissionDecision: "deny"`) blocks a tool.

**Why this matters:** external handlers are operator-authored shell scripts and webhooks — they *will* break. A broken audit webhook must never deadlock a user-facing turn. Fail-open is the safe default for everything except an explicit, intentional deny.

### P5 — Deny-wins is global and deterministic

Across multiple matching handlers, across multiple matcher groups, and across composition with first-party C# hooks, the precedence is always **Deny > Rewrite > Allow**, folded in **config-declared order** — never in `Task.WhenAll` completion order.

**Why this matters:** parity with the existing `CombinePreToolUse` is a hard requirement (Property 2). If config hooks resolved nondeterministically, the same config could allow a call on one run and deny it on the next. Folding by declared index makes the verdict reproducible and auditable.

### P6 — Injection-safe by construction

Command handlers spawn a real executable with arguments passed via `ProcessStartInfo.ArgumentList` (no shell, `UseShellExecute = false`). HTTP handlers POST a serialized payload to a fixed URL. No part of the agent's conversation or tool input is ever interpolated into a shell string.

**Why this matters:** the payload contains attacker-influenceable content (the model's tool input, the user's prompt). Passing it through `sh -c` would be a command-injection vector. Argument-vector spawning and JSON-over-stdin eliminate the class entirely.

---

## 5. Architecture Overview

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                        STARTUP — composition root (once)                      │
│                                                                              │
│  appsettings.json "Hooks" section                                            │
│        │  AddOptions<HooksOptions>().Bind(section)                           │
│        ▼                                                                      │
│  HooksOptions  (Dictionary<HookEventName, HookMatcherGroupConfig[]>)         │
│        │                                                                      │
│        │  new HookRegistry(options, IHookHandlerFactory, ILogger)            │
│        ▼                                                                      │
│  ┌────────────────────── HookRegistry (compile-once) ──────────────────────┐ │
│  │  for each event:                                                        │ │
│  │     for each matcher group:                                            │ │
│  │        HookMatcher.Create(matcher)        ── compile regex / hash list │ │
│  │        factory.Create(handlerConfig)      ── Command | Http            │ │
│  │  ⇒ Dictionary<HookEventName, List<(HookMatcher, IHookHandler[])>>      │ │
│  └──────────────────────────────┬─────────────────────────────────────────┘ │
│                                 │  ToAgentHooks()                            │
│                                 ▼                                            │
│  AgentHooks { OnPreToolUse?, OnPostToolUse?, OnSessionStarted?, ... }        │
│        │  (delegate wired ONLY for events that have config)                  │
│        ▼                                                                      │
│  IPostConfigureOptions<AgentOptions> ⇒ AgentOptions.ConfiguredHooks          │
│        │                                                                      │
│        ▼  AgentFactory folds:  Baseline ▸ Configured ▸ User  (via Compose)   │
│  effective AgentHooks ──► new Agent(...)                                     │
└──────────────────────────────────────────┬───────────────────────────────────┘
                                           │
                                           ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                    HOT PATH — Agent.RunAsync loop (unchanged)                  │
│                                                                              │
│  ... ── OnPreToolUse(ctx) ─────────────────────────────────────────┐         │
│            │  (delegate produced by ToAgentHooks)                   │         │
│            ▼                                                        │         │
│   ┌─ projection delegate ───────────────────────────────────────┐  │         │
│   │ handlers = MatchingHandlers(PreToolUse, ctx.ToolName)        │  │         │
│   │ if none ⇒ return Allowed                                    │  │         │
│   │ payload = HookPayloadFactory.ForPreToolUse(ctx)             │  │         │
│   │ outputs = await Task.WhenAll(h.InvokeAsync(payload, ct))    │  │         │
│   │ return AggregateDecision(outputs)  // deny ▸ rewrite ▸ allow│  │         │
│   └──────────────────┬──────────────────────────────────────────┘  │         │
│                      ▼                                              │         │
│        Deny ⇒ [Blocked] reason   Rewrite ⇒ new input   Allow ⇒ run │         │
│                                                                    ▼         │
│  ... ── OnPostToolUse / OnSessionStarted / OnStop / ... ── fire-and-await ──  │
└──────────────────────────────────────────┬───────────────────────────────────┘
                                           │  handler execution
                                           ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                     HANDLERS — external execution boundary                     │
│                                                                              │
│  CommandHookHandler                         HttpHookHandler                   │
│  ──────────────────                         ───────────────                   │
│  ProcessStartInfo (UseShellExecute=false)   HttpClient.PostAsync(url, json)   │
│  stdin  ◄── payload JSON ──► close           body ◄── payload JSON            │
│  stdout ──► JSON verdict                     response body ──► JSON verdict    │
│  stderr ──► reason / logs                    status code  ──► ExitCode map     │
│  exit code ──► {0 ok, 2 deny, other nb-err}  (2xx ⇒ 0, else ⇒ non-blocking)   │
│        │                                            │                          │
│        └──────────────► HookHandlerOutput ◄─────────┘                          │
│            (ExitCode, Json?, RawStdout?, RawStderr?)                           │
└──────────────────────────────────────────────────────────────────────────────┘
```

Key invariants the diagram implies:

- **One producer, one consumer type.** Everything funnels into a single `AgentHooks` instance; the loop sees nothing else.
- **Compile-once.** All regex/handler resolution happens at startup; the hot path only matches and dispatches.
- **Two handler kinds in V1.** `Command` and `Http`. The factory is the only place that knows the kind taxonomy.
- **Null delegates for absent events.** Events with no config produce `null` delegates, preserving the loop's skip path.
- **Deny-wins everywhere.** Whether decisions come from one handler, many handlers, or composed C# hooks, `Deny > Rewrite > Allow` holds.

---

## 6. System Components

Each subsystem is detailed in a uniform shape: **Purpose / Responsibilities / Inputs+Outputs / Internal flow / Implementation notes / Constraints / V1 vs V2 / Test surface (TDD-first)**. All new types live under `src/Harness/Agency.Harness/Hooks/Configuration/`, with file-scoped namespaces, `sealed` types, and implementation types marked `internal` (the project already declares `InternalsVisibleTo("Agency.Harness.Test")`).

### 6.1 The Configuration Schema (`HooksOptions` + POCOs)

#### Purpose

Provide a strongly-typed, `IConfiguration`-bindable mirror of the Claude Code hook JSON, bound from the `appsettings.json` `"Hooks"` section.

#### Responsibilities

- Express the three-level nesting `event → matcher group → handler` as bindable POCOs.
- Carry handler parameters (kind, command, args, timeout, url, headers).
- Bind cleanly with `Microsoft.Extensions.Configuration.Binder`, including enum-keyed dictionaries.

#### Inputs / Outputs

| In | Out |
|---|---|
| `IConfigurationSection "Hooks"` | `HooksOptions` (root) → `HookMatcherGroupConfig[]` per event → `HookHandlerConfig[]` per group |

#### Internal flow

Binding is delegated to the framework binder; no custom converter in V1. The enum dictionary key (`HookEventName`) is parsed by the binder from the section key string.

#### Implementation notes

- POCOs are **`class` with `{ get; set; }`**, **not** positional records — the configuration binder requires parameterless ctors and settable properties (this is the same reason `McpServerConfig` is a class).
- `HookHandlerConfig.Timeout` is an `int?` in **seconds** (Claude Code's `timeout` unit). A default applies when null (§7.4). If the JSON key must be exactly `timeout`, the property is named `Timeout`; the binder is case-insensitive.
- `Headers` is `Dictionary<string,string>?` for HTTP handlers.
- Unknown event-name keys must fail fast with a clear message naming the offending key (mirroring `McpClientPool`'s "X is required" exception style).

#### Type catalogue

| File | Type | Shape |
|---|---|---|
| `HookEventName.cs` | `enum` | `SessionStart, UserPromptSubmit, PreIteration, PreToolUse, PostToolUse, PostToolBatch, AssistantTurn, Stop, SessionEnd` |
| `HookHandlerKind.cs` | `enum` | `Command, Http` (+ reserved `McpTool, Prompt, Agent`) |
| `HookHandlerConfig.cs` | `class` | `Type`, `Command`, `Args[]`, `Timeout`, `Url`, `Headers` |
| `HookMatcherGroupConfig.cs` | `class` | `Matcher` (string?), `Hooks` (`HookHandlerConfig[]`) |
| `HooksOptions.cs` | `class` | `Hooks` (`Dictionary<HookEventName, HookMatcherGroupConfig[]>`) |

#### Constraints

- No behaviour on POCOs. They are inert (P2).
- No cross-field validation in the POCOs themselves; validation happens at registry-build time where a useful error context exists.

#### V1 vs V2

| Aspect | V1 | V2 |
|---|---|---|
| Source | `appsettings.json` `"Hooks"` | Layered dedicated file (`.claude/settings.json` shape) |
| Reload | Bound once at startup | `IOptionsMonitor` hot-reload |
| Handler kinds | Command, Http | + McpTool, Prompt, Agent |
| Secret handling | Literal header values | Env-var / secret-store interpolation |

#### Test surface (TDD-first)

| Test task | Implementation task |
|---|---|
| `Bind_PreToolUseGroup_PopulatesMatcherAndHandlers` | Define POCOs + binding |
| `Bind_EnumKeyedDictionary_ParsesEventNames` | Confirm enum-key binding |
| `Bind_TimeoutKey_MapsToTimeoutSeconds` | `Timeout` property name |
| `Bind_UnknownEventName_ThrowsWithKeyName` | Fail-fast validation hook |
| `Bind_EmptyHooksSection_YieldsEmptyOptions` | Zero-config neutrality |
| `Bind_HttpHandler_PopulatesUrlAndHeaders` | HTTP fields |

---

### 6.2 The Matcher (`HookMatcher`)

#### Purpose

Decide whether a matcher group applies to a given subject (a tool name, for tool events), implementing Claude Code matcher semantics with the kind resolved once at construction.

#### Responsibilities

- Classify a raw matcher string into one of three modes: match-all, exact/pipe-list, or regex.
- Expose `bool IsMatch(string candidate)` with O(1)-ish cost.
- Be ReDoS-safe: bound regex execution time and fail fast on malformed patterns.

#### Inputs / Outputs

| In | Out |
|---|---|
| `string? matcher` (at construction) | `HookMatcher` with a resolved mode |
| `string candidate` (per call) | `bool` |

#### Internal flow

```
Create(matcher):
  if matcher is null/""/"*"        ⇒ mode = MatchAll
  elif Regex.IsMatch(matcher, "^[A-Za-z0-9_|]+$"):
        names = matcher.Split('|') into HashSet(OrdinalIgnoreCase)
        mode  = ExactSet(names)
  else:
        try compiled = new Regex(matcher,
              RegexOptions.Compiled | RegexOptions.CultureInvariant,
              matchTimeout: 250ms)
        catch RegexParseException ⇒ throw (fail fast at build)
        mode = RegexMode(compiled)

IsMatch(candidate):
  switch mode:
    MatchAll      ⇒ true
    ExactSet      ⇒ names.Contains(candidate)
    RegexMode     ⇒ try compiled.IsMatch(candidate)
                    catch RegexMatchTimeoutException ⇒ log warning; return false
```

#### Implementation notes

- **Resolution happens once** (P3). The `Regex` is compiled in `Create`, not per call.
- **Match timeout** (~250 ms) caps catastrophic backtracking; a timeout is treated as *no match* and logged, never thrown into the loop (P4).
- **Case sensitivity:** exact/pipe-list uses `OrdinalIgnoreCase` to match tool names tolerantly (consistent with `BlockListHooks._shellToolNames`, which is `OrdinalIgnoreCase`). Regex honours its own flags but defaults to case-sensitive — documented.
- For **non-tool events**, the registry passes a sentinel candidate (e.g. the event name) but treats any matcher as match-all in V1 (§3). The matcher object is still constructed for forward-compatibility.

#### Constraints

- A malformed regex is a **startup** failure (build-time), not a runtime surprise. This is deliberate: operators learn of a bad pattern when the host boots, not three weeks later when a tool happens to fire.
- No glob syntax in V1; the three modes are exhaustive.

#### V1 vs V2

| Aspect | V1 | V2 |
|---|---|---|
| Subjects | Tool name only | Session id, start-reason, file path |
| Modes | all / exact / pipe / regex | + glob, + negation |
| Timeout | Fixed 250 ms | Configurable per matcher |

#### Test surface (TDD-first)

| Test task | Implementation task |
|---|---|
| `Matcher_Null_Empty_Star_MatchAll` | MatchAll mode |
| `Matcher_ExactName_IsCaseInsensitive` | ExactSet mode |
| `Matcher_PipeList_MatchesAnyMember` | Pipe split |
| `Matcher_Regex_MatchesAndRejects` | Regex mode |
| `Matcher_MalformedRegex_ThrowsAtCreate` | Build-time fail-fast |
| `Matcher_PathologicalPattern_TimesOut_NoMatch` | Match-timeout safety |

---

### 6.3 The Handler Layer (`IHookHandler`, `CommandHookHandler`, `HttpHookHandler`, factory)

#### Purpose

Execute the external side of a hook — spawn a process or call an HTTP endpoint — and normalise the result into a single `HookHandlerOutput` the registry can interpret.

#### Responsibilities

- Define the handler abstraction and the normalised output record.
- Implement the two V1 kinds (`Command`, `Http`).
- Provide a factory that maps `HookHandlerKind` → concrete handler, and a seam (`IHookHandlerFactory`) so tests can inject fakes.

#### Inputs / Outputs

| In | Out |
|---|---|
| `HookPayload` (§6.4), `CancellationToken` | `HookHandlerOutput(int ExitCode, JsonElement? Json, string? RawStdout, string? RawStderr)` |

#### Internal flow — `CommandHookHandler`

```
InvokeAsync(payload, ct):
  psi = new ProcessStartInfo {
     FileName = cfg.Command,
     RedirectStandardInput = true, RedirectStandardOutput = true,
     RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true }
  foreach a in cfg.Args: psi.ArgumentList.Add(a)
  using cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
  cts.CancelAfter(cfg.Timeout ?? DefaultTimeout)
  proc.Start()
  await proc.StandardInput.WriteAsync(JsonSerializer.Serialize(payload))
  proc.StandardInput.Close()                         // signal EOF
  var outTask = proc.StandardOutput.ReadToEndAsync(cts.Token)
  var errTask = proc.StandardError.ReadToEndAsync(cts.Token)
  try   await proc.WaitForExitAsync(cts.Token)
  catch OperationCanceledException:
        proc.Kill(entireProcessTree: true)
        return new HookHandlerOutput(NonBlockingTimeout, null, null, null)
  var (stdout, stderr) = (await outTask, await errTask)
  JsonElement? json = TryParseLeadingJson(stdout)     // tolerate non-JSON
  return new HookHandlerOutput(proc.ExitCode, json, stdout, stderr)
```

#### Internal flow — `HttpHookHandler`

```
InvokeAsync(payload, ct):
  using cts = linked token + CancelAfter(cfg.Timeout ?? DefaultTimeout)
  req = new HttpRequestMessage(POST, cfg.Url) { Content = JsonContent(payload) }
  foreach (k,v) in cfg.Headers: req.Headers.TryAddWithoutValidation(k, v)
  try resp = await httpClient.SendAsync(req, cts.Token)
  catch (timeout/transport) ⇒ return new HookHandlerOutput(NonBlockingError, null,...)
  exit = resp.IsSuccessStatusCode ? 0 : NonBlockingError
  json = TryParseJson(await resp.Content.ReadAsStringAsync())
  return new HookHandlerOutput(exit, json, body, null)
```

#### Implementation notes

- **Deadlock avoidance (P4):** stdout and stderr are read **concurrently**; stdin is closed after writing. Reading one stream to completion before the other risks a full-pipe deadlock when the child writes a lot to the unread stream.
- **Process-tree kill on timeout** prevents orphaned grandchildren (e.g. a script that spawns `node`).
- **Injection safety (P6):** `ArgumentList` (not a concatenated command line); `UseShellExecute = false`. `Command` must be a real executable. On Windows, `.ps1` scripts are **not** directly executable — they go in `Args` after `pwsh -File`, exactly as `McpServerConfig.Command` works today.
- **HTTP client** is injected via `IHttpClientFactory` / a named `HttpClient`, never `new`-ed per call, to avoid socket exhaustion.
- **Exit-code taxonomy** is centralised as named sentinels: `0` = ok, `2` = blocking deny, all others = non-blocking error; timeouts map to a dedicated non-blocking sentinel for telemetry.
- The **factory** is a `switch` expression on `HookHandlerKind` (mirroring `McpClientPool.CreateTransport`); reserved kinds throw `NotSupportedException` naming the kind.

#### Constraints

- No shell form, no environment-variable interpolation in args (V1).
- Handlers are stateless per call; no caching of process pools.
- A handler must complete within its timeout or be killed; there is no "detached/async" handler in V1 (even fire-and-forget events are awaited — see §6.5).

#### V1 vs V2

| Aspect | V1 | V2 |
|---|---|---|
| Kinds | Command, Http | + McpTool, Prompt, Agent |
| Command form | Exec (ArgumentList) | Optional shell form |
| HTTP auth | Literal headers | Secret interpolation, retries |
| Pooling | Per-call process | Optional warm worker pool |

#### Test surface (TDD-first)

| Test task | Implementation task |
|---|---|
| `Command_Exit0_DenyJson_ProducesDenyOutput` | stdout JSON parse |
| `Command_Exit2_ProducesDenyOutput` | exit-code mapping |
| `Command_Exit0_RewriteJson_ProducesRewriteOutput` | rewrite field parse |
| `Command_Exit0_NoJson_ProducesAllowOutput` | non-JSON tolerance |
| `Command_NonZeroNonTwo_ProducesNonBlockingError` | fail-open exit |
| `Command_Timeout_KillsProcessTree_NonBlocking` | timeout + kill |
| `Command_LargeStderr_NoDeadlock` | concurrent stream read |
| `Http_2xxDenyJson_ProducesDeny` | response parse |
| `Http_5xx_ProducesNonBlockingError` | status mapping |
| `Http_Timeout_NonBlocking` | timeout honoured |
| `Factory_ReservedKind_ThrowsNotSupported` | kind switch |

---

### 6.4 The Payload Contract (`HookPayload`, `HookPayloadFactory`)

#### Purpose

Build the JSON document handed to handlers (over stdin for command, as the POST body for HTTP), matching the Claude Code snake_case input contract.

#### Responsibilities

- Define a serialization record with snake_case fields and null-omission.
- Provide one builder per `HookContext` shape, populating only event-relevant fields from the universally-available `Context AgentContext`.

#### Inputs / Outputs

| In | Out |
|---|---|
| A specific `*HookContext` (e.g. `PreToolUseHookContext`) | `HookPayload` ready to serialize |

#### Schema

| Field (JSON) | Type | Populated for |
|---|---|---|
| `session_id` | string | all events (`ctx.AgentContext.Session.Id`) |
| `hook_event_name` | string | all events |
| `cwd` | string | all events (`Environment.CurrentDirectory`) |
| `iteration_count` | int | all events (`ctx.AgentContext.IterationCount`) |
| `total_cost_usd` | double | all events (`ctx.AgentContext.TotalCostUsd`) |
| `tool_name` | string? | PreToolUse, PostToolUse |
| `tool_input` | JsonElement? | PreToolUse, PostToolUse |
| `tool_response` | `{content, is_error}`? | PostToolUse |
| `prompt` | string? | UserPromptSubmit |
| `message` | string? | AssistantTurn, Stop |

#### Internal flow

```
ForPreToolUse(ctx) ⇒ { session_id, hook_event_name="PreToolUse", cwd,
                       tool_name=ctx.ToolName, tool_input=ctx.Input,
                       iteration_count, total_cost_usd }
ForPostToolUse(ctx) ⇒ above + tool_response={ content=ctx.Result.Content,
                                              is_error=ctx.Result.IsError }
ForSessionStarted / ForSessionEnded(ctx) ⇒ { session_id, hook_event_name, cwd }
ForUserPromptSubmit(ctx) ⇒ { ..., prompt=ctx.Query.Prompt }
ForAssistantTurn / ForStop(ctx) ⇒ { ..., message=Summarize(ctx.Message|ctx.Result) }
```

#### Implementation notes

- A dedicated `JsonSerializerOptions` carries `JsonNamingPolicy.SnakeCaseLower` and `DefaultIgnoreCondition = WhenWritingNull` so nulls are omitted and field names match the contract without per-property attributes.
- `tool_input` round-trips as a `JsonElement` so the handler sees byte-identical input to what the tool would receive — essential for the rewrite path (the handler echoes a modified `tool_input`).
- `cwd` uses `Environment.CurrentDirectory` in V1; if a stable per-session working directory is needed later, add a `Cwd` to the environmental context (out of scope).

#### Constraints

- The payload is **append-only** in spirit: adding fields is safe; renaming/removing is a contract break for operator scripts. Field names are part of the public contract.
- No secrets are injected into the payload; it carries only what the event already exposes.

#### V1 vs V2

| Aspect | V1 | V2 |
|---|---|---|
| `cwd` | `Environment.CurrentDirectory` | Per-session cwd |
| `message` | Best-effort summary | Full structured transcript slice |
| `permission_mode` | Omitted | Included for parity |
| `transcript_path` | Omitted (no file transcript) | Included if transcript persistence ships |

#### Test surface (TDD-first)

| Test task | Implementation task |
|---|---|
| `Payload_PreToolUse_HasSnakeCaseKeys` | Serializer options |
| `Payload_OmitsNullFields` | Null-ignore policy |
| `Payload_PostToolUse_ToolResponseIsErrorMapped` | tool_response shape |
| `Payload_ToolInput_RoundTripsJsonElement` | JsonElement fidelity |
| `Payload_UserPromptSubmit_CarriesPrompt` | prompt field |
| `Payload_SessionStart_MinimalFields` | minimal payload |

---

### 6.5 The Hook Registry & Projection (`HookRegistry`)

#### Purpose

Hold the compiled `event → (matcher, handlers)` map and project it onto a single `AgentHooks` instance — the heart of the subsystem and the one place that bridges declarative config to the loop's delegate model.

#### Responsibilities

- Compile matchers + handlers once in the constructor (P3).
- Expose `ToAgentHooks()` that wires a delegate **only** for events with config.
- Implement `AggregateDecision` for `PreToolUse` (deny-wins, deterministic).
- Drive fire-and-await execution for non-tool events.

#### Inputs / Outputs

| In | Out |
|---|---|
| `HooksOptions`, `IHookHandlerFactory`, `ILogger` | `HookRegistry`; `ToAgentHooks()` → `AgentHooks` |

#### Internal structure

```
_byEvent : Dictionary<HookEventName, List<CompiledGroup>>
CompiledGroup = (HookMatcher Matcher, IReadOnlyList<IHookHandler> Handlers)

ctor(options, factory, logger):
  foreach (event, groups) in options.Hooks:
     compiled = groups.Select(g => (
        HookMatcher.Create(g.Matcher),
        g.Hooks.Select(factory.Create).ToArray()))
     _byEvent[event] = compiled.ToList()

static HookRegistry Empty ⇒ no groups ⇒ ToAgentHooks() == AgentHooks.None
```

#### Internal flow — `ToAgentHooks()`

```
ToAgentHooks():
  return new AgentHooks {
    OnPreToolUse   = Has(PreToolUse)   ? BuildPreToolUse()      : null,
    OnPostToolUse  = Has(PostToolUse)  ? BuildFireAwaitTool(PostToolUse)   : null,
    OnSessionStarted = Has(SessionStart) ? BuildFireAwait(SessionStart)    : null,
    OnUserPromptSubmit = Has(UserPromptSubmit) ? ... : null,
    OnPreIteration = Has(PreIteration) ? ... : null,
    OnPostToolBatch = Has(PostToolBatch) ? ... : null,
    OnAssistantTurn = Has(AssistantTurn) ? ... : null,
    OnStop         = Has(Stop)         ? ... : null,
    OnSessionEnd   = Has(SessionEnd)   ? ... : null,
  }

BuildPreToolUse() ⇒ async (ctx, ct) => {
   handlers = MatchingHandlers(PreToolUse, ctx.ToolName)
   if handlers.Count == 0: return PreToolUseDecision.Allowed
   payload  = HookPayloadFactory.ForPreToolUse(ctx)
   outputs  = await Task.WhenAll(handlers.Select(h => h.InvokeAsync(payload, ct)))
   return AggregateDecision(outputs, ctx.Input)      // ordered fold
}

MatchingHandlers(event, subject) ⇒
   _byEvent[event].Where(g => g.Matcher.IsMatch(subject))
                  .SelectMany(g => g.Handlers).ToList()   // config-declared order
```

#### Internal flow — `AggregateDecision`

```
AggregateDecision(outputs, originalInput):
  decisions = outputs.Select(MapToDecision)             // preserve index order
  if any d is Deny    ⇒ return first Deny
  if any d is Rewrite ⇒ return first Rewrite
  return Allow

MapToDecision(output):
  if output.ExitCode == 2                         ⇒ Deny(reasonFrom(output))
  if output.Json?.permissionDecision == "deny"    ⇒ Deny(reasonFrom(output))
  if output.Json has rewritten tool_input         ⇒ Rewrite(thatElement)
  if output.ExitCode == 0 or "allow"/"ask"         ⇒ Allow
  else (non-zero non-2)                            ⇒ log; Allow   // fail-open (P4)
```

#### Implementation notes

- **Null-delegate preservation (P1):** absent events stay `null`, so the loop keeps its skip path and zero allocations are made for unconfigured events.
- **Deterministic fold (P5):** `MapToDecision` runs over `outputs` in the order the handlers were declared (`SelectMany` preserves group then handler order), not completion order. `Task.WhenAll` preserves input ordering in its result array, so the fold is reproducible.
- **Non-tool events** build the payload, `await Task.WhenAll(handlers…)`, and ignore the outputs for control flow — but they are **awaited**, not detached, so cancellation/timeouts are honoured and no process leaks (this is "fire-and-await", not "fire-and-forget").
- The **seam** `IHookHandlerFactory` lets projection/aggregation tests substitute fakes returning canned `HookHandlerOutput`, so these tests never spawn a process or open a socket — the single most valuable testability decision.

#### Constraints

- A single `HookRegistry` produces a single `AgentHooks`. It does **not** itself compose with baseline/user hooks — that is `AgentFactory`'s job (§6.6), reusing the existing `Compose`.
- The registry holds no per-invocation mutable state; it is safe to share as a singleton across sessions.

#### V1 vs V2

| Aspect | V1 | V2 |
|---|---|---|
| Decision events | PreToolUse only | + Stop/UserPromptSubmit block via `decision` |
| Output surfacing | Ignored for non-tool | Surface `systemMessage`/`additionalContext` |
| Reload | Built once | Rebuild on config change |
| Per-group order | Declared order | Optional priority numbers |

#### Test surface (TDD-first)

| Test task | Implementation task |
|---|---|
| `Project_NoConfig_AllDelegatesNull` | Null-delegate path |
| `Project_PreToolUseConfigured_DelegateNonNull` | Selective wiring |
| `Project_MatcherFiltersByToolName` | MatchingHandlers selection |
| `Project_NonMatchingTool_NoHandlerInvoked` | Fake-factory spy |
| `Aggregate_DenyWinsOverRewriteOverAllow` | Ordered fold |
| `Aggregate_OrderDeterministic_FirstDenyWins` | Index-stable fold |
| `Aggregate_NonBlockingError_FailsOpenAllow` | Fail-open |
| `NonToolEvent_AwaitsAllHandlers` | Fire-and-await |
| `Empty_ProducesAgentHooksNone` | Empty registry |

---

### 6.6 DI & Composition Wiring (`HookServiceCollectionExtensions`, `AgentFactory`)

#### Purpose

Bind the config, build the registry, project it into `AgentOptions`, and fold it into the effective `AgentHooks` alongside baseline (memory) and user hooks.

#### Responsibilities

- Provide `AddAgencyConfiguredHooks(IServiceCollection, IConfiguration, sectionName = "Hooks")`.
- Register `IHookHandlerFactory`, an `HttpClient`, and `HookRegistry` as singletons.
- Use `IPostConfigureOptions<AgentOptions>` to set `AgentOptions.ConfiguredHooks` without clobbering user hooks.
- Extend `AgentFactory` to fold three hook sources in a fixed order.

#### Internal flow — registration

```csharp
public static IServiceCollection AddAgencyConfiguredHooks(
    this IServiceCollection services, IConfiguration config, string sectionName = "Hooks")
{
    services.AddOptions<HooksOptions>().Bind(config.GetSection(sectionName));
    services.AddHttpClient();                                   // for HttpHookHandler
    services.AddSingleton<IHookHandlerFactory, HookHandlerFactory>();
    services.AddSingleton<HookRegistry>(sp => new HookRegistry(
        sp.GetRequiredService<IOptions<HooksOptions>>().Value,
        sp.GetRequiredService<IHookHandlerFactory>(),
        sp.GetRequiredService<ILogger<HookRegistry>>()));

    services.AddSingleton<IPostConfigureOptions<AgentOptions>>(sp =>
        new PostConfigureOptions<AgentOptions>(name: null, agentOpts =>
            agentOpts.ConfiguredHooks =
                sp.GetRequiredService<HookRegistry>().ToAgentHooks()));
    return services;
}
```

#### Internal flow — composition (`AgentFactory.CreateAgent`)

```csharp
AgentHooks? hooks = new[] {
        options.BaselineHooks,     // memory (P3 ordering: retrieval mutates Context first)
        options.ConfiguredHooks,   // operator policy
        options.UserHooks }        // ad-hoc, last word
    .Where(h => h is not null)
    .Aggregate((AgentHooks?)null,
               (acc, h) => acc is null ? h : acc.Compose(h!));
```

This subsumes the existing four-arm switch and yields `null` when all three are absent. Deny-wins across all three sources is guaranteed by `Compose`/`CombinePreToolUse`.

#### Implementation notes

- **Ordering is Baseline ▸ Configured ▸ User.** Memory baseline runs first because its `OnPreIteration` retrieval mutates `Context.Knowledge`/`Context.Memory` that later hooks may read; operator config is host policy; user hooks are the explicit escape hatch with the final say (subject to deny-wins).
- The `IPostConfigureOptions` pattern is copied verbatim from `MemoryServiceCollectionExtensions`, which already sets `BaselineHooks` this way — guaranteeing config hooks are applied *after* all `Configure<AgentOptions>` calls and never overwrite `UserHooks`.
- `Program.cs` calls `AddAgencyConfiguredHooks(builder.Configuration)` once, after the existing `AddOptions<AgentOptions>().BindConfiguration("Agent")` block.

#### Files modified (minimal)

| File | Change |
|---|---|
| `Agency.Harness/AgentOptions.cs` | Add `public AgentHooks? ConfiguredHooks { get; set; }` |
| `Agency.Harness.Console/AgentFactory.cs` | Replace 4-arm switch with the 3-source fold |
| `Agency.Harness.Console/Program.cs` | Call `AddAgencyConfiguredHooks` |
| `Agency.Harness.Console/appsettings.json` | Add example/empty `"Hooks"` section |
| `Agency.Harness/Agency.Harness.csproj` | Add `Microsoft.Extensions.{Options, Configuration.Binder, Http, DependencyInjection.Abstractions, Logging.Abstractions}` if not already transitive |

#### Constraints

- No change to `Agent.cs` (P1).
- The registry is a singleton; per-session state lives only in the contexts passed at fire time.

#### V1 vs V2

| Aspect | V1 | V2 |
|---|---|---|
| Source binding | `Bind` (static) | `IOptionsMonitor` rebuild |
| Ordering | Fixed Baseline▸Configured▸User | Configurable priority |
| HTTP client | Default named client | Per-handler policy (Polly, auth) |

#### Test surface (TDD-first)

| Test task | Implementation task |
|---|---|
| `Di_BindsHooksSection_BuildsRegistry` | Extension wiring |
| `Di_PostConfigure_SetsConfiguredHooks` | PostConfigure path |
| `Di_NoHooksSection_ConfiguredHooksEmptyNone` | Zero-config |
| `Factory_FoldsThreeSources_InOrder` | Composition fold |
| `Factory_AllNull_ReturnsNull` | Null collapse |
| `Compose_ConfigAllow_CannotOverrideCSharpDeny` | Deny-wins end-to-end |

---

## 7. Configuration & Contract Model

This subsystem has **no database**. Its "data model" is three artefacts: the bound config tree, the compiled in-memory registry, and the wire contract handlers see. They are specified here with the same rigour a storage layer would receive.

### 7.1 The `appsettings.json` `"Hooks"` schema

```json
"Hooks": {
  "PreToolUse": [
    {
      "matcher": "Bash|ExecutePowershell",
      "hooks": [
        { "type": "Command", "command": "pwsh",
          "args": ["-File", "./hooks/guard.ps1"], "timeout": 30 }
      ]
    }
  ],
  "PostToolUse": [
    {
      "matcher": "*",
      "hooks": [
        { "type": "Http", "url": "http://localhost:8080/audit",
          "headers": { "X-Source": "agency" }, "timeout": 10 }
      ]
    }
  ]
}
```

### 7.2 Event → delegate map

| Config event (`HookEventName`) | `AgentHooks` delegate | Decision-capable | Matcher subject |
|---|---|---|---|
| `SessionStart` | `OnSessionStarted` | No | (match-all) |
| `UserPromptSubmit` | `OnUserPromptSubmit` | No (V1) | (match-all) |
| `PreIteration` | `OnPreIteration` | No | (match-all) |
| `PreToolUse` | `OnPreToolUse` | **Yes** | tool name |
| `PostToolUse` | `OnPostToolUse` | No | tool name |
| `PostToolBatch` | `OnPostToolBatch` | No | (match-all) |
| `AssistantTurn` | `OnAssistantTurn` | No | (match-all) |
| `Stop` | `OnStop` | No (V1) | (match-all) |
| `SessionEnd` | `OnSessionEnd` | No | (match-all) |

### 7.3 The compiled registry structure (in-memory)

| Field | Type | Lifetime |
|---|---|---|
| `_byEvent` | `Dictionary<HookEventName, List<CompiledGroup>>` | Singleton, built in ctor |
| `CompiledGroup.Matcher` | `HookMatcher` (resolved mode) | Singleton |
| `CompiledGroup.Handlers` | `IReadOnlyList<IHookHandler>` | Singleton (stateless handlers) |

### 7.4 Configuration parameters

| Parameter | Location | Default | Meaning |
|---|---|---|---|
| `Hooks` | `appsettings.json` root | `{}` | The event→group map |
| `matcher` | per group | `"*"` | Tool-name matcher |
| `type` | per handler | `Command` | Handler kind |
| `command` | command handler | — (required) | Executable on PATH |
| `args` | command handler | `[]` | Argument vector |
| `url` | http handler | — (required) | POST target |
| `headers` | http handler | `{}` | Extra request headers |
| `timeout` | per handler | `30` (s) | Hard execution cap |
| *match timeout* | internal | `250` ms | Regex ReDoS cap |
| *non-blocking exit sentinels* | internal | `-1` (timeout), `1+` | Telemetry-only |

### 7.5 The stdin / POST-body contract (handler input)

See §6.4. snake_case keys; nulls omitted; `tool_input` is verbatim JSON.

### 7.6 The stdout / response-body contract (handler output)

| Channel | Meaning |
|---|---|
| Exit code `0` | Success; parse stdout JSON if present |
| Exit code `2` | Blocking deny |
| Exit code other | Non-blocking error → fail-open Allow |
| stdout JSON `hookSpecificOutput.permissionDecision` | `allow` / `deny` / `ask`(→allow) |
| stdout JSON `hookSpecificOutput.permissionDecisionReason` | Deny reason → `[Blocked] {reason}` |
| stdout JSON rewritten `tool_input` | → `PreToolUseDecision.Rewrite` |
| HTTP 2xx | maps to exit `0`; body parsed as above |
| HTTP non-2xx / transport error | non-blocking error |

### 7.7 Differences between handler backends

| Concern | Command | Http |
|---|---|---|
| Transport | stdin/stdout/exit code | POST body / response / status |
| Failure → exit | Process exit code | HTTP status (2xx⇒0) |
| Timeout action | Kill process tree | Cancel request |
| Injection surface | Argument vector (safe) | Fixed URL + literal headers |
| Latency | Process spawn (~10–50 ms cold) | Network round-trip |

---

## 8. Core Algorithms

### 8.1 Matcher resolution

Resolved once (§6.2). The classifier `^[A-Za-z0-9_|]+$` is the load-bearing rule: it cleanly separates "literal name / pipe-list" from "regex", matching Claude Code's documented behaviour. Anything containing a regex metacharacter (`.`, `^`, `*`, `(`, …) routes to the regex branch.

### 8.2 Decision aggregation (deny-wins, deterministic)

```
function AggregateDecision(outputs[]):           // index order == declared order
    deny = first output mapping to Deny
    if deny != null: return deny
    rewrite = first output mapping to Rewrite
    if rewrite != null: return rewrite
    return Allow
```

**Correctness invariant:** the fold must iterate `outputs` in declared order, and `Task.WhenAll`'s result array preserves the order of the input task list. Folding by completion order would make the chosen deny/rewrite nondeterministic — a silent bug that only manifests under handler-latency variance. **Test the ordering explicitly** with two handlers of differing simulated latency.

### 8.3 Output → decision mapping

The single source of mapping truth (§6.5 `MapToDecision`). Precedence inside one output: a non-zero exit `2` is a deny even if stdout also contains a rewrite — an explicit blocking exit dominates document content. This matches Claude Code's "exit 2 = blocking error, JSON not processed".

### 8.4 Handler execution (command)

Step-by-step in §6.3. The two correctness-critical steps are **(a)** closing stdin after writing (the child blocks on EOF otherwise) and **(b)** reading stdout/stderr concurrently (a one-stream-at-a-time read deadlocks on a full pipe).

### 8.5 Error handling — taxonomy

| Class | Example | Handling | Effect on loop |
|---|---|---|---|
| **Build-time config error** | Malformed regex, unknown event name | Throw at startup | Host fails fast |
| **Explicit deny** | exit 2 / `permissionDecision:"deny"` | Map to `Deny(reason)` | Tool blocked |
| **Rewrite** | stdout rewritten `tool_input` | Map to `Rewrite` | Input substituted |
| **Handler timeout** | script hangs | Kill tree; non-blocking | Allow + warn |
| **Handler crash / non-2 exit** | script throws | Log; non-blocking | Allow + warn |
| **Transport error (HTTP)** | connection refused | Log; non-blocking | Allow + warn |
| **Malformed handler JSON** | stdout not JSON | Treat as no-decision | Allow |

The dividing principle (P4): **only an intentional, explicit signal blocks; everything accidental fails open.**

---

## 9. Incremental vs Full Processing

This subsystem is **build-once, match-incrementally**:

| Phase | When | Cost |
|---|---|---|
| **Full compilation** | Startup (registry ctor) | O(groups × handlers): regex compile, list hashing, factory binding |
| **Incremental match** | Per fired event | O(groups for that event) matcher tests + O(matched handlers) dispatch |

There is no partial/incremental *config* processing in V1 — config is read once. A configuration change requires a host restart (V2 adds `IOptionsMonitor` reload, at which point the registry is rebuilt atomically and the new `AgentHooks` swapped into `AgentOptions.ConfiguredHooks`).

**Why this matters:** compile-once is what makes per-call matching cheap enough to sit on the `PreToolUse` path. Trading live reload for hot-path speed is the right call for V1; operators restart to change policy, exactly as they do for `appsettings` today.

---

## 10. Background Workers / Async Components

This subsystem has **no long-lived background service** (unlike the memory Distiller/Consolidator/Hygiene). Its asynchrony is per-invocation handler execution:

### 10.1 Execution model

- `PreToolUse` handlers run via `Task.WhenAll` and are **awaited** before the decision is returned — the loop blocks on them by design (a guard that returned before its handlers finished could not deny).
- Non-tool handlers run via `Task.WhenAll` and are **awaited** (fire-and-await) so timeouts and cancellation are honoured and no process leaks.

### 10.2 Concurrency model

- The registry is an immutable singleton; concurrent sessions share it safely (no mutable per-call state).
- Within one fired event, handlers run concurrently; across events/sessions, the harness's existing concurrency governs.
- Each command handler is its own OS process; each HTTP handler one request. There is no shared mutable handler state.

### 10.3 Resource bounds

| Bound | V1 | Rationale |
|---|---|---|
| Per-handler timeout | `timeout` (default 30 s) | Caps a single handler |
| Regex match timeout | 250 ms | ReDoS cap |
| Process tree kill | On timeout | No orphaned grandchildren |
| HTTP client | Pooled via `IHttpClientFactory` | No socket exhaustion |
| Concurrent handlers | Unbounded within a matched set (V1) | Sets are small; global throttle deferred to V2 |

**Operational note:** a `matcher: "*"` on `PostToolUse` with a slow handler adds its latency to *every* tool batch. Operators should keep wildcard handlers fast or move them to fire-and-await events that don't gate execution. This is documented, not enforced, in V1.

---

## 11. Performance Expectations

### 11.1 Zero-config path

| Operation | Budget |
|---|---|
| Unconfigured event | **0** — `null` delegate, loop skips |
| Empty `"Hooks"` section | `AgentHooks.None`; identical to no-feature build |

### 11.2 Hot-path (PreToolUse) budgets

| Step | Budget | Notes |
|---|---|---|
| Matcher test | < 1 µs/group | HashSet lookup or bounded regex |
| Payload build + serialize | < 100 µs | Small JSON |
| Command handler spawn | 10–50 ms cold | OS process start dominates |
| HTTP handler round-trip | network-bound | Operator's endpoint latency |
| Decision aggregation | < 10 µs | Linear fold over a few outputs |

The dominant cost is **handler execution**, which is operator-controlled. The framework overhead (match + build + aggregate) is negligible relative to a process spawn or a network call.

### 11.3 Throughput considerations

- Matching is O(groups-for-event); realistic configs have a handful of groups per event.
- Process-spawn cost is the practical ceiling on `PreToolUse` throughput; operators who need high tool-call rates should prefer HTTP handlers to a warm service over per-call script spawns, or keep guards on a narrow matcher.

### 11.4 Memory / allocation

- Registry: one-time allocation at startup proportional to config size.
- Per fired event: one payload object + one `JsonSerializer` buffer + the handler outputs. No retained allocations.
- Unconfigured events allocate nothing.

---

## 12. Edge Cases and Failure Modes

### 12.1 Concurrency

| Case | Handling |
|---|---|
| Same tool fired in parallel batch | Each call independently builds its payload and runs its handlers; decisions are per-call. |
| Two matcher groups match one tool | Handlers concatenated in declared order; deny-wins across both. |
| Handler mutates shared external state | Out of framework scope; operator's responsibility. |

### 12.2 Failure modes

| Failure | Handling |
|---|---|
| Handler binary missing | Process start throws → caught → non-blocking error → Allow + error log naming the command. |
| Handler hangs | Timeout → kill tree → non-blocking Allow. |
| Handler emits gigabytes to stdout | Bounded by timeout; consider a max-output cap in V2. Concurrent read avoids deadlock. |
| HTTP endpoint down | Transport exception → non-blocking Allow. |
| Malformed deny JSON (typo in `permissionDecision`) | Not recognised as deny → Allow. Operators must test their scripts; the smoke test (§13) exists for this. |
| Regex catastrophic backtracking | Match timeout → no match → warn. |

### 12.3 Contract hazards

| Hazard | Mitigation |
|---|---|
| Operator renames a payload field expectation | Field names are a documented contract (§7.5); additions are safe, renames are breaking. |
| Rewrite returns invalid `tool_input` for the tool | The tool's own input validation rejects it downstream; the rewrite path does not re-validate in V1. |
| Exit 2 *and* a rewrite in one handler | Exit 2 (deny) dominates (§8.3). |

### 12.4 Operational edge cases

| Case | Handling |
|---|---|
| `"Hooks"` present but every group empty | Delegates wire but match nothing → effectively no-op (minor wasted wiring; acceptable). |
| Unknown handler `type` string | Binder parses enum; unknown value is a build error (fail fast). |
| Reserved kind (`McpTool`/`Prompt`/`Agent`) configured | Factory throws `NotSupportedException` at registry build — fail fast, not at fire time. |
| Windows `.ps1` as `command` directly | Documented failure; must be `command:"pwsh", args:["-File","x.ps1"]`. |

---

## 13. End-to-end Flow

A worked trace of U1 (block `rm -rf` via a command hook):

```
STARTUP
  appsettings.json "Hooks":
    PreToolUse: [ { matcher:"Bash|ExecutePowershell",
                    hooks:[ {type:Command, command:"pwsh",
                             args:["-File","./hooks/guard.ps1"], timeout:30} ] } ]
  │
  ├─ AddAgencyConfiguredHooks ⇒ Bind HooksOptions
  ├─ new HookRegistry(options, factory, logger)
  │     PreToolUse ⇒ [ ( HookMatcher.ExactSet{"Bash","ExecutePowershell"},
  │                      [ CommandHookHandler(pwsh -File guard.ps1, 30s) ] ) ]
  ├─ ToAgentHooks() ⇒ AgentHooks { OnPreToolUse = <projection> ; others null }
  ├─ IPostConfigureOptions ⇒ AgentOptions.ConfiguredHooks = that
  └─ AgentFactory fold: Baseline(memory) ▸ Configured ▸ User ⇒ effective AgentHooks
                                  │
RUNTIME (agent decides to run ExecutePowershell with {command:"rm -rf /tmp/x"})
  │
  ├─ Agent.cs reaches OnPreToolUse(ctx: ToolName="ExecutePowershell", Input={command:"rm -rf /tmp/x"})
  │     │  (delegate from ToAgentHooks; composed deny-wins with memory baseline)
  │     ▼
  ├─ projection: MatchingHandlers(PreToolUse,"ExecutePowershell")
  │     ⇒ ExactSet matches ⇒ [ CommandHookHandler ]
  ├─ payload = { session_id, hook_event_name:"PreToolUse", cwd,
  │              tool_name:"ExecutePowershell",
  │              tool_input:{command:"rm -rf /tmp/x"}, iteration_count, total_cost_usd }
  ├─ CommandHookHandler:
  │     spawn  pwsh -File guard.ps1   (UseShellExecute=false)
  │     stdin ◄ payload JSON ► close
  │     guard.ps1 sees "rm -rf" ⇒ stdout:
  │        {"hookSpecificOutput":{"permissionDecision":"deny",
  │                               "permissionDecisionReason":"rm -rf blocked"}}
  │        exit 0
  │     ⇒ HookHandlerOutput(0, json, ...)
  ├─ AggregateDecision ⇒ permissionDecision=="deny" ⇒ Deny("rm -rf blocked")
  │     (Compose with baseline: still Deny — deny-wins)
  ▼
  Agent.cs: decision is Deny ⇒ ToolResult("[Blocked] rm -rf blocked", IsError:true)
            ⇒ FunctionResultContent appended; tool NEVER executes
            ⇒ agent observes the block, replans
```

Contrast U7 (zero config): `HookRegistry.Empty.ToAgentHooks() == AgentHooks.None`; `OnPreToolUse` is `null`; `Agent.cs` skips the hook entirely; the tool runs as if the feature were absent.

---

## 14. Design Notes / Rationale

### 14.1 Why project onto `AgentHooks` instead of a parallel firing path?

The loop's nine firing points already encode subtle, tested semantics: per-call deny/rewrite, parallel-tool batching, event ordering, null-skip. Re-implementing a second firing path for config hooks would duplicate that logic and risk divergence (e.g. a config deny behaving differently from a C# deny). Projection makes config hooks *indistinguishable* from compiled hooks at the loop, which is exactly Property 2.

### 14.2 Why mirror `McpClientPool`/`ToolRegistry` rather than invent a pattern?

The team already maintains "config POCO → runtime object via a ctor switch" (`McpClientPool.CreateTransport`) and "name-keyed registry compiled in ctor" (`ToolRegistry`). Reusing both shapes means reviewers recognise the structure on sight, and the `IPostConfigureOptions<AgentOptions>` trick is copied from the memory wiring that already sets `BaselineHooks`. Familiarity is a feature.

### 14.3 Why fail-open on everything except explicit deny?

External handlers are operator scripts and webhooks; they break in mundane ways (missing binary, transient 503, a typo). A guard system that *fails closed* would convert every broken hook into a wedged agent — a worse failure than the hook simply not firing. Only an intentional, unambiguous signal (exit 2, `permissionDecision:"deny"`) blocks. This is the same bias the harness already shows by catching tool exceptions and surfacing them as results rather than crashing the loop.

### 14.4 Why deterministic, declared-order folding?

`Task.WhenAll` returns results in input order, but if we folded by completion order the chosen deny/rewrite would vary with handler latency. Two runs of identical config could then differ — the worst kind of bug, because it hides under load. Folding by declared index makes the verdict a pure function of config + payload, which is auditable and testable.

### 14.5 Why Baseline ▸ Configured ▸ User ordering?

Memory's `OnPreIteration` retrieval *mutates* `Context.Knowledge`/`Context.Memory`; hooks that run later can read enriched context, so memory baseline must be first. Operator config is platform policy and sits in the middle. User hooks are the explicit, code-level escape hatch and get the last word — but deny-wins means "last word" cannot *weaken* an upstream deny, only add denies/rewrites. This preserves the security property that neither config nor user code can widen a first-party block.

### 14.6 Why an `IHookHandlerFactory` seam?

Without it, every projection/aggregation test would spawn real processes or open sockets — slow, OS-dependent, flaky. Injecting the factory lets the bulk of the test suite use canned `HookHandlerOutput`s and assert pure logic (matching, folding, null-wiring) deterministically. The real handlers get their own focused, `[Trait("Category","Process")]`-tagged tests. This is the single highest-leverage testability decision in the design.

### 14.7 Why `Command` *and* `Http` in V1 (not command only)?

Confirmed with the requester. Command covers local guards/scripts; HTTP covers centralised policy/audit services without shipping a script to every host. The two share the entire pipeline (matcher, payload, projection, aggregation) and differ only in the handler body, so adding HTTP is a small marginal cost (one class + one factory arm) for a large coverage gain. The heavier kinds (`mcp_tool`, `prompt`, `agent`) are deferred because they need the MCP pool / an LLM client and a richer execution context.

### 14.8 Why class-based POCOs, not records?

`IConfiguration` binding wants parameterless ctors and settable properties; positional records fight the binder. `McpServerConfig` is a class for exactly this reason. The *decision* types stay records (`PreToolUseDecision`), because those are produced in code, not bound from config.

### 14.9 Why no `Agent.cs` change at all?

Property 1 (loop transparency) is the design's spine. Every capability — match, deny, rewrite, audit — is achievable purely by *producing* an `AgentHooks`. Touching the loop would expand the blast radius from "a new folder + 3 small edits" to "the core execution path", inviting regressions in memory/audit/block-list behaviour. The constraint is also a forcing function: anything that *can't* be done by producing `AgentHooks` is correctly out of scope for V1 (e.g. non-tool loop-abort).

---

## 15. Implementation Task Breakdown (TDD-First)

Per the TDD discipline, **every implementation task is preceded by its test task**. Tests capture the spec; implementation makes them pass; refactor follows. Tasks are ordered to keep the suite green and dependencies satisfied.

| # | Test task (write first) | Implementation task | Depends on |
|---|---|---|---|
| 1 | `HookConfigBindingTests` (§6.1) | Config POCOs + enum types + binding | — |
| 2 | `HookMatcherTests` (§6.2) | `HookMatcher` (3 modes, regex timeout) | — |
| 3 | `HookPayloadFactoryTests` (§6.4) | `HookPayload` + `HookPayloadFactory` + snake_case options | — |
| 4 | `CommandHookHandlerTests` (§6.3, `[Trait Process]`) | `CommandHookHandler` + `HookHandlerOutput` | 3 |
| 5 | `HttpHookHandlerTests` (§6.3, stub `HttpMessageHandler`) | `HttpHookHandler` | 3 |
| 6 | `Factory_ReservedKind_ThrowsNotSupported` (§6.3) | `IHookHandlerFactory` + `HookHandlerFactory` | 4,5 |
| 7 | `HookRegistryProjectionTests` (§6.5, fake factory) | `HookRegistry` + `ToAgentHooks` + `MatchingHandlers` | 1,2,3,6 |
| 8 | `Aggregate*` decision tests (§8.2) | `AggregateDecision` + `MapToDecision` | 7 |
| 9 | `Di_*` tests (§6.6) | `HookServiceCollectionExtensions` + `AgentOptions.ConfiguredHooks` | 7 |
| 10 | `Factory_FoldsThreeSources` / `Compose_ConfigAllow_CannotOverrideCSharpDeny` (§6.6) | `AgentFactory` 3-source fold | 9 |
| 11 | (manual smoke, §13) | `appsettings.json` example + `guard.ps1` sample | 10 |

**Test placement:** `src/Harness/Agency.Harness.Test/Hooks/Configuration/`, reusing the `MakeCtx` idiom from the existing `Hooks/BlockListHooksTests.cs`. Process/HTTP-touching tests carry `[Trait("Category","Process")]` so CI can skip them where a runtime is unavailable; everything else is pure and OS-independent thanks to the factory seam.

**Verification gates:**
1. `dotnet build src/Agency.slnx` — clean (confirms package refs + binding compile).
2. `dotnet test src/Agency.slnx --filter "Category!=Functional"` — all unit tests green.
3. Manual smoke (§13): a `PreToolUse` command hook that denies; confirm `[Blocked]` surface.
4. Zero-config run: absent/empty `"Hooks"` leaves behaviour identical (registry `Empty` → `AgentHooks.None`).

---

## 16. Open Items Tracked for V2

| # | Item | Why deferred |
|---|---|---|
| 1 | `mcp_tool` / `prompt` / `agent` handler kinds | Need MCP pool / LLM client + richer context |
| 2 | Dedicated layered config file (`.claude/settings.json` shape) | `appsettings` binding covers V1; file loader is convenience |
| 3 | `IOptionsMonitor` hot-reload (rebuild registry, swap atomically) | Compile-once trades reload for hot-path speed |
| 4 | Non-tool event blocking (`Stop`/`UserPromptSubmit` `decision:"block"`) | Existing non-tool delegates have no decision channel |
| 5 | Surface `systemMessage` / `additionalContext` from handler output | No host UI channel wired in V1 |
| 6 | Matcher subjects beyond tool name (session id, start-reason, file path) | V1 matches tool name only |
| 7 | Secret interpolation for HTTP headers / env vars in args | Literal values only in V1 |
| 8 | Global handler concurrency throttle / warm worker pool | Sets are small; per-call spawn acceptable in V1 |
| 9 | Max handler stdout size cap | Timeout bounds it adequately for V1 |
| 10 | `ask` / `defer` permission honoured interactively | No interactive permission UI in the loop |

---

## 17. Glossary

| Term | Definition |
|---|---|
| **Handler** | The external executor of a hook — a spawned process (`Command`) or an HTTP endpoint (`Http`) — wrapped by `IHookHandler`. |
| **Matcher group** | A `(matcher, handlers[])` config entry; one element of an event's array. |
| **Event** | One of the nine lifecycle points, named by `HookEventName`, each mapping to one `AgentHooks` delegate. |
| **Decision** | A `PreToolUseDecision` (`Allow` / `Deny(reason)` / `Rewrite(JsonElement)`) — the only control-flow output, produced only by `PreToolUse`. |
| **Projection** | The act of converting the declarative registry into an `AgentHooks` instance (`HookRegistry.ToAgentHooks()`). |
| **Compile-once** | Resolving matchers and handlers a single time in the registry ctor, never per fired event. |
| **Deny-wins** | The precedence `Deny > Rewrite > Allow`, applied across handlers, groups, and composed C# hooks, folded in declared order. |
| **Fire-and-await** | Running non-tool handlers concurrently and awaiting them (honouring cancellation/timeouts) while ignoring their output for control flow — distinct from fire-and-forget. |
| **Fail-open** | Treating any non-explicit-deny outcome (crash, timeout, malformed output, non-2 exit) as `Allow`. |
| **Seam** | The `IHookHandlerFactory` injection point that lets tests substitute fake handlers. |
| **Payload** | The snake_case JSON document (`HookPayload`) handed to a handler over stdin / as the POST body. |
| **Baseline / Configured / User hooks** | The three composed `AgentHooks` sources (memory first-party / operator config / ad-hoc code), folded in that order by `AgentFactory`. |

---

*End of specification.*
