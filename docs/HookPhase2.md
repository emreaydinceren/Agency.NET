# Hook Phase 2 — Config-Hydrated, Claude-Code-Style Hooks for Agency

## Context

`Agency.Harness` already has a capable **in-process** hook system: `AgentHooks` (an immutable
record of 9 delegate properties — `OnSessionStarted`, `OnUserPromptSubmit`, `OnPreIteration`,
`OnPreToolUse`, `OnPostToolUse`, `OnPostToolBatch`, `OnAssistantTurn`, `OnStop`, `OnSessionEnd`),
fired at the right points in `Agent.cs`, composed via `AgentHooksExtensions.Compose` (where for
`OnPreToolUse` the most-restrictive decision wins: Deny > Rewrite > Allow). But **every hook must be
compiled C#** — there is no way to declare a hook from configuration the way Claude Code does
(`event → matcher → external handler` in JSON).

This change adds a **declarative, config-hydrated** hook layer that mirrors the Claude Code hooks
shape and **projects it onto the existing `AgentHooks` delegate model**. The agent loop is unchanged:
it still only consumes an `AgentHooks` record. The new machinery is purely a *producer* of
`AgentHooks`, composed into `AgentOptions` exactly like `BaselineHooks`/`UserHooks` today.

**Scope (confirmed):** config in the `appsettings.json` `"Hooks"` section; **Command + HTTP**
handler types; full implementation + unit tests.

## Approach

Build a `HookRegistry` (analogous to `Tools/ToolRegistry.cs`) hydrated from a `"Hooks"` options
section. Its `ToAgentHooks()` produces an `AgentHooks` instance; `AgentFactory` composes it with the
existing baseline/user hooks. The two closest existing patterns to follow:
- **MCP config → runtime objects:** `Tools/McpClientOptions.cs` (POCO config classes) +
  `Tools/McpClientPool.CreateTransport` (a `switch` expression that throws on unknown kinds).
- **PostConfigure DI:** `Memory/Agency.Memory.Distiller/DependencyInjection/MemoryServiceCollectionExtensions.cs`
  uses `IPostConfigureOptions<AgentOptions>` to set `BaselineHooks` without overwriting user hooks.

### Config records — `src/Harness/Agency.Harness/Hooks/Configuration/`
Plain mutable POCOs (`class` with `{ get; set; }`, like `McpServerConfig` — **not** positional
records; `IConfiguration` binding needs settable props):
- `HookEventName.cs` — enum: `SessionStart, UserPromptSubmit, PreIteration, PreToolUse, PostToolUse, PostToolBatch, AssistantTurn, Stop, SessionEnd` (maps 1:1 to the 9 delegates).
- `HookHandlerKind.cs` — enum: `Command, Http` (+ `McpTool, Prompt, Agent` reserved → NotSupported).
- `HookHandlerConfig.cs` — `Type` (kind), `Command`, `Args[]`, `Timeout` (seconds), `Url`, `Headers`.
- `HookMatcherGroupConfig.cs` — `Matcher` (string?) + `Hooks` (`HookHandlerConfig[]`).
- `HooksOptions.cs` — root: `Dictionary<HookEventName, HookMatcherGroupConfig[]> Hooks`.

JSON shape bound from appsettings:
```json
"Hooks": {
  "PreToolUse": [
    { "matcher": "Bash|ExecutePowershell",
      "hooks": [ { "type": "Command", "command": "pwsh", "args": ["-File","./hooks/guard.ps1"], "timeout": 30 } ] }
  ],
  "PostToolUse": [
    { "matcher": "*",
      "hooks": [ { "type": "Http", "url": "http://localhost:8080/audit", "timeout": 10 } ] }
  ]
}
```

### Matcher — `HookMatcher.cs`
Resolve the kind **once** at construction (compile-once), expose `bool IsMatch(string candidate)`:
- `null` / `""` / `"*"` → match all.
- matches `^[A-Za-z0-9_|]+$` → split on `|` into an `OrdinalIgnoreCase` `HashSet` → exact membership.
- otherwise → `Regex` with `RegexOptions.Compiled | CultureInvariant` and a **matchTimeout** (~250 ms).
  Fail fast on `RegexParseException` at build; swallow `RegexMatchTimeoutException` as no-match + log.
Matcher applies to tool name for tool events; ignored (match-all) for non-tool events in phase 1.

### Handlers — `Hooks/Configuration/Handlers/`
- `IHookHandler.cs` — `Task<HookHandlerOutput> InvokeAsync(HookPayload payload, CancellationToken ct)`.
- `HookHandlerOutput.cs` — record `(int ExitCode, JsonElement? Json, string? RawStdout, string? RawStderr)`.
- `CommandHookHandler.cs` — spawn process: `ProcessStartInfo` with `UseShellExecute=false`,
  `RedirectStandardInput/Output/Error`, `ArgumentList` for args (no shell interpolation → injection-safe).
  Write payload JSON to stdin then **close stdin**; read stdout+stderr **concurrently**
  (`ReadToEndAsync`) to avoid pipe deadlock; `WaitForExitAsync`. Timeout via linked CTS +
  `CancelAfter`; on timeout `Kill(entireProcessTree:true)` → non-blocking error. Parse stdout as JSON
  if it starts with `{`; tolerate non-JSON. *(Windows note: `Command` must be a real executable —
  `.ps1` scripts go in `Args` after `pwsh -File`, matching how `McpServerConfig.Command` works.)*
- `HttpHookHandler.cs` — POST payload JSON to `Url` (with optional `Headers`), via an injected
  `HttpClient`/`IHttpClientFactory`; map response body → `HookHandlerOutput.Json`, HTTP status → ExitCode
  (2xx→0, else non-blocking). Honor `Timeout`.
- `IHookHandlerFactory.cs` + `HookHandlerFactory.cs` — `switch` on `HookHandlerKind` (mirror
  `McpClientPool.CreateTransport`), `NotSupportedException` for reserved kinds. Injected into the
  registry as a **seam** so projection tests can substitute fakes (no real process/HTTP).

### Payload — `HookPayload.cs` + `HookPayloadFactory.cs`
Snake_case serialization record (Claude Code contract): `session_id`, `hook_event_name`, `cwd`,
`tool_name?`, `tool_input?` (JsonElement), `tool_response?` (`{content, is_error}`), `prompt?`,
`message?`, `iteration_count`, `total_cost_usd`; nulls omitted (`WhenWritingNull`). `HookPayloadFactory`
has one builder per HookContext, all pulling from the universally-available `Context AgentContext`
(`ctx.AgentContext.Session.Id`, iteration count, cost; `cwd = Environment.CurrentDirectory`).

### Registry — `HookRegistry.cs`
`public sealed class HookRegistry`, ctor `(HooksOptions options, IHookHandlerFactory factory, ILogger logger)`.
Builds `Dictionary<HookEventName, List<(HookMatcher Matcher, IReadOnlyList<IHookHandler> Handlers)>>`
once (compile matchers + handlers in ctor, like `ToolRegistry`). `static HookRegistry Empty`.

`ToAgentHooks()` returns one `AgentHooks`, wiring a delegate **only for events that have config**
(absent events stay `null` → loop keeps its "null = skip" fast path):
- `OnPreToolUse`: gather handlers whose matcher matches `ctx.ToolName`; run via `Task.WhenAll`;
  `AggregateDecision` folds outputs in **config-declared order** (not completion order) with the same
  precedence as `CombinePreToolUse`: exit 2 or `permissionDecision:"deny"` → `Deny`; rewritten
  `tool_input` → `Rewrite(JsonElement)`; else `Allow`; other non-zero exit → log + Allow (non-blocking).
  Deny-wins, then Rewrite, else Allow.
- All other events: build payload, await all matching handlers, ignore JSON for control flow (await,
  not true fire-and-forget, so cancellation/timeouts are honored and processes don't leak).

### DI wiring — `HookServiceCollectionExtensions.cs`
`AddAgencyConfiguredHooks(this IServiceCollection, IConfiguration, sectionName = "Hooks")`:
`AddOptions<HooksOptions>().Bind(section)`; register `IHookHandlerFactory`, `HttpClient`, and
`HookRegistry` singletons; add `IPostConfigureOptions<AgentOptions>` (via `PostConfigureOptions<>`)
that sets `agentOpts.ConfiguredHooks = registry.ToAgentHooks()`.

### Files to modify (minimal)
- `src/Harness/Agency.Harness/AgentOptions.cs` — add `public AgentHooks? ConfiguredHooks { get; set; }`.
- `src/Harness/Agency.Harness.Console/AgentFactory.cs` — fold three sources in order
  **Baseline → Configured → User**:
  `new[]{ options.BaselineHooks, options.ConfiguredHooks, options.UserHooks }.Where(h => h is not null)
  .Aggregate((AgentHooks?)null, (acc, h) => acc is null ? h : acc.Compose(h!))` (replaces the current
  4-arm switch; deny-wins preserved by `Compose`).
- `src/Harness/Agency.Harness.Console/Program.cs` — call `builder.Services.AddAgencyConfiguredHooks(builder.Configuration)`
  after the `AddOptions<AgentOptions>().BindConfiguration("Agent")` block (~line 48–50).
- `src/Harness/Agency.Harness.Console/appsettings.json` — add an example/empty `"Hooks"` section.
- `src/Harness/Agency.Harness/Agency.Harness.csproj` — add
  `Microsoft.Extensions.{Options, Configuration.Binder, Http, DependencyInjection.Abstractions, Logging.Abstractions}`
  only if not already transitive (verify first; versions come from `src/Directory.Build.props`).

## Conventions
File-scoped namespaces; `sealed`; impl types `internal` (project already has
`InternalsVisibleTo("Agency.Harness.Test")`); config POCOs are `class`/`{get;set;}` not positional
records; build-from-config in ctors via `switch` throwing `NotSupportedException`/`InvalidOperationException`
with the offending name; reuse the existing `PreToolUseDecision.Allow/Deny/Rewrite` (do not invent a new
decision type).

## Tests — `src/Harness/Agency.Harness.Test/Hooks/Configuration/`
Reuse the `MakeCtx` idiom from the existing `Hooks/BlockListHooksTests.cs`.
- `HookMatcherTests` — null/""/"*" all; exact (case-insensitive); pipe-list; regex match/non-match;
  malformed regex throws at build; pathological pattern + adversarial input hits matchTimeout → no-match.
- `HookConfigBindingTests` — bind a JSON stream → `HooksOptions`; enum-keyed dict populated; `timeout`→`Timeout`;
  unknown event name → clear error.
- `HookPayloadFactoryTests` — snake_case keys; nulls omitted; `tool_response.is_error` correct;
  `tool_input` JsonElement round-trips.
- `CommandHookHandlerTests` — spawn a real cross-platform child (`pwsh`/`cmd` chosen by
  `OperatingSystem.IsWindows()`), `[Trait("Category","Process")]`: exit 0 + `{permissionDecision:"deny"}`→Deny;
  exit 2→Deny; rewritten `tool_input`→Rewrite; no JSON→Allow; non-zero non-2→Allow+logged; timeout→killed.
- `HttpHookHandlerTests` — stub `HttpMessageHandler`: 2xx + deny JSON→Deny; 2xx + rewrite→Rewrite; 5xx→non-blocking Allow; timeout honored.
- `HookRegistryProjectionTests` — inject a **fake `IHookHandlerFactory`** (canned `HookHandlerOutput`):
  no config → null delegate; configured PreToolUse → non-null `OnPreToolUse`; matcher filters by tool name;
  deny-wins + rewrite-wins aggregation is order-deterministic.
- `HookComposeInteropTests` — compose `HookRegistry.ToAgentHooks()` with `BlockListHooks.Dangerous` via
  `Compose`; assert a config `Allow` cannot override a C# `Deny` (deny-wins end-to-end).

## Verification
1. `dotnet build src/Agency.slnx` — clean build (confirms package refs + binding compile).
2. `dotnet test src/Agency.slnx --filter "Category!=Functional"` — all unit tests green.
3. Manual smoke: add a `PreToolUse` command hook to `appsettings.json` pointing at a `guard.ps1`
   that emits `{"hookSpecificOutput":{"permissionDecision":"deny","permissionDecisionReason":"blocked"}}`;
   run `Agency.Harness.Console`, ask the agent to run a shell tool, confirm it is blocked with the reason.
4. Confirm zero-config (`"Hooks"` absent/empty) leaves agent behavior identical (registry `Empty` →
   `AgentHooks.None`).

## Risks / tricky bits
1. **Process I/O deadlock** — read stdout/stderr concurrently, close stdin after writing, `WaitForExitAsync`.
2. **Cross-platform spawn** — `Command` must be a real executable; scripts go in `Args`; no shell interpolation.
3. **Regex safety** — compile once with matchTimeout; fail fast on parse errors; swallow match timeouts.
4. **Deny-wins determinism** — fold in config-declared order, not `WhenAll` completion order.
5. **Null-delegate preservation** — wire a delegate only when its event has config.
6. **Testability seam** — inject `IHookHandlerFactory` so projection tests avoid real process/HTTP.
