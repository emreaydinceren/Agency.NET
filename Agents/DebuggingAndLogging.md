# Debugging & Logging

How to get more out of the logs than the defaults show — turning up verbosity, and specifically
getting full LLM request/response content into the log file — when the standard trace/metric/log
output isn't enough to diagnose a bug in `Agency.Harness.Console`. Read this **before** assuming a
config knob doesn't exist; the logging setup has a few non-obvious gotchas that already cost a
debugging session.

For the architecture behind these knobs (what OpenTelemetry captures, why logs are Serilog and not
the OTel logs pipeline, what each signal is for), see
[`docs/observability-what-it-did-what-it-cost.md`](../docs/observability-what-it-did-what-it-cost.md).
This doc only covers the practical "how do I see more" recipe and its gotchas.

## The recipe: verbose logging + full LLM content

This is entirely config-driven — no code edit or rebuild required.

1. In `src/Harness/Agency.Harness.Console/appsettings.json`, under `OpenTelemetry.FileExport.Logs`,
   set the global floor and add a category override to carve `Microsoft.Extensions.AI` out of the
   default `Microsoft → Warning` suppression (see "Why `Verbose` alone doesn't show LLM content"
   below for why the override is needed):

   ```json
   "Logs": {
     "MinimumLevel": "Verbose",
     "CategoryOverrides": {
       "Microsoft.Extensions.AI": "Verbose"
     }
   }
   ```

   `CategoryOverrides` binds to `LogFileOptions.CategoryOverrides` in
   `Telemetry/TelemetryOptions.cs` and merges with (doesn't replace) the built-in defaults
   (`Microsoft`/`System` → `Warning`), so framework noise stays suppressed while you add just the
   category you need. Each entry is applied to **both** filter layers a log record has to survive —
   Serilog's `MinimumLevel.Override(...)` and a Microsoft.Extensions.Logging `AddFilter(...)` rule —
   which is the only reason the config-only recipe works at all; see "Two filter layers" below.
   Optionally also set `Agent.LogToolPayloads` to `true` (see "A separate knob" below).
2. Run a repro — `dotnet run` (or just relaunch the console) picks up the JSON change immediately;
   no recompile needed since this only touches config. The log lands at
   `{RepoRoot}/logs/app-<timestamp>.log` (a fresh file per process start).
3. Verify before you trust it: the log should contain `Microsoft.Extensions.AI...` lines at `VRB`.
   If it holds only `INF` lines despite `MinimumLevel: Verbose`, the override isn't reaching the
   Microsoft.Extensions.Logging layer — go read "Two filter layers" below rather than tweaking
   levels blindly. That symptom already cost one session several hours.

Revert the `CategoryOverrides`/`MinimumLevel` entries once you're done — `Verbose` is noisy and
full LLM content logging is a diagnostic carve-out, not something that should stay on by default.

## Seeing the exact wire request: LM Studio server logs

When the backend is a local LM Studio instance and the question is about the *exact* request Agency
sent ("is the `tools` array actually there?", "what did the system prompt end up as?"), read it from
LM Studio instead of from Agency. This sidesteps Agency's whole logging stack and is both faster and
strictly more informative than `UseLogging()`, which only logs at the parsed `ChatMessage` level.

The CLI lives at `%USERPROFILE%\.lmstudio\bin\lms.exe` and is often **not** on `PATH` — invoke it by
full path:

```powershell
& "$env:USERPROFILE\.lmstudio\bin\lms.exe" log stream --source server --json
```

Every inbound HTTP request appears as a log line containing the **complete request body** — the full
`tools` array, `messages`, `temperature`, everything on the wire. Useful options:
`--source model|server|runtime`, `--filter input|output` (model source only), `--stats`, `--json`.

To capture for later inspection, redirect to a file:

```powershell
& "$env:USERPROFILE\.lmstudio\bin\lms.exe" log stream --source server --json > capture.jsonl
```

**Confirm the capture is live before you rely on it.** Send a request carrying a distinctive tool
name and grep the file for it. A capture that silently isn't recording burns a repro you may not be
able to reproduce cheaply.

**Don't wait on the on-disk log files.** `%APPDATA%\LM Studio\logs\main.log` is the desktop app's own
log (GPU config, model load estimates, updater) and contains no requests. Per-day server logs live at
`%USERPROFILE%\.lmstudio\server-logs\YYYY-MM\YYYY-MM-DD.N.log`, but when LM Studio runs in **headless
mode** today's file stays 0 bytes because output goes to the stream instead. `lms log stream` is the
reliable path.

**Treat captures as sensitive.** The bodies contain the full conversation and system prompt verbatim.
Keep them out of the repo and delete them when you're done.

## Gotchas

### Two filter layers: Microsoft.Extensions.Logging *and* Serilog

A log record has to survive **two independent filters** before it reaches the file:

1. **Microsoft.Extensions.Logging** — `LoggerFilterOptions`, populated from
   `b.SetMinimumLevel(...)`, any `b.AddFilter(...)` rules, **and** the `Logging:LogLevel` section of
   `appsettings.json`, which `Host.CreateApplicationBuilder` (`Program.cs`) binds automatically.
2. **Serilog** — the `LoggerConfiguration`'s `MinimumLevel.Is(...)` plus its per-category
   `MinimumLevel.Override(...)` calls, inside the registered `SerilogLoggerProvider`.

M.E.L runs first. If it drops a record, Serilog never sees it, and no amount of Serilog
configuration can bring it back. The trap: **`b.ClearProviders()` removes logging *providers*, not
filter rules.** `Logging:LogLevel` in `src/Harness/Agency.Harness.Console/appsettings.json` contains
`"Microsoft": "Warning"`, and that rule survived `ClearProviders()` and discarded every
`Microsoft.Extensions.AI.*` Trace record before Serilog was consulted — so a Serilog-only
`CategoryOverrides` entry could not work no matter what it was set to. That is precisely the failure
that cost a debugging session.

`AddLogging` now mirrors every `CategoryOverrides` entry onto the M.E.L layer as well, via
`b.AddFilter(category, ToMsLogLevel(level))`. M.E.L resolves rules by longest-prefix match, so the
specific `Microsoft.Extensions.AI` → `Trace` rule beats the broader `Microsoft` → `Warning` rule
and content flows through. If you ever add a new filter layer or touch `AddLogging`, keep both
layers in sync — configuring only one silently produces an empty-looking `Verbose` log.

### `Logging:LogLevel` in appsettings.json is a live filter — don't assume it's inert

It is bound by the Generic Host into `LoggerFilterOptions` and applies to **every** provider,
including the Serilog one. Far from being dead, its `"Microsoft": "Warning"` entry was the exact
thing suppressing the LLM content described above.

That said, it isn't the knob to reach for when turning verbosity up. The primary minimum-level knob
for the file log is `OpenTelemetry:FileExport:Logs:MinimumLevel`, bound to
`LogFileOptions.MinimumLevel` (`Telemetry/TelemetryOptions.cs`). Valid values (case-insensitive):
`Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal` (Serilog's `LogEventLevel`, mapped to
`Microsoft.Extensions.Logging.LogLevel` via `ToMsLogLevel`). Defaults to `Information` if the `Logs`
section is omitted.

Because `CategoryOverrides` entries are now mirrored as M.E.L filter rules, a specific category you
add there outranks a broader `Logging:LogLevel` rule by longest-prefix match — you don't need to
edit `Logging:LogLevel` to carve a category out. Don't list the same category in both sections at
conflicting levels; keep each category's intent in one place.

### Why `Verbose` alone doesn't show LLM content

`AddLogging` builds its Serilog `LoggerConfiguration` with a per-category override loop driven by
`LogFileOptions.CategoryOverrides`, which **defaults** to `{ "Microsoft": "Warning", "System":
"Warning" }`. The chat client pipeline (`OpenAIClient.CreateChatClient` in
`src/Llm/Agency.Llm.OpenAI/OpenAIClient.cs`) calls `.UseLogging(loggerFactory)` —
Microsoft.Extensions.AI's built-in `LoggingChatClient` middleware, the only thing in the codebase
that logs full LLM request/response message content (at Trace level). Its logger category starts
with `Microsoft.Extensions.AI`, so the default `"Microsoft" → Warning` override silences it
completely, even with the global minimum level set to `Verbose`.

The same category is also caught by the `"Microsoft": "Warning"` rule on the
Microsoft.Extensions.Logging side (from `Logging:LogLevel`), which filters *earlier* — see "Two
filter layers" above. Both layers have to be carved out, or the log stays silent.

Both resolve per-category rules by longest-prefix match, so adding a more specific
`"Microsoft.Extensions.AI": "Verbose"` entry to `CategoryOverrides` in `appsettings.json` (see the
recipe above) carves out just that category on both layers at once — `AddLogging` applies each entry
as a Serilog `MinimumLevel.Override(...)` *and* an M.E.L `AddFilter(...)` rule — without weakening
the default Microsoft/System suppression for everything else (ASP.NET/EF/etc. infrastructure logs
stay quiet at Warning, which is still useful noise reduction). No code change needed — this was
originally a hardcoded `.MinimumLevel.Override(...)` call requiring a rebuild per category, moved to
config specifically so the next debugging session doesn't have to touch code for this.

### No raw HTTP request/response body or header logging exists anywhere

Checked `src/Llm/Agency.Llm.OpenAI/OpenAIClient.cs` and
`src/Llm/Agency.Llm.OpenAI/FailedRequestLoggingPipelinePolicy.cs`. The OpenAI SDK client is built
directly via `System.ClientModel` (`BuildOpenAIClient`), not via `IHttpClientFactory`/
`AddHttpClient`, so `Microsoft.Extensions.Http.Logging`'s HTTP-trace handlers are never in the
pipeline. The only existing `PipelinePolicy`s are `SuppressThinkingPipelinePolicy` (strips content,
doesn't log) and `FailedRequestLoggingPipelinePolicy` (logs only URL/status/reason on non-2xx
responses, never bodies). Microsoft.Extensions.AI's `UseLogging()` middleware (above) is the
closest thing to request/response content visibility, and it logs at the parsed-`ChatMessage`
level, not raw HTTP bytes.

This is true of Agency's own pipeline only. When the backend is local LM Studio, the practical
substitute is to read the request from the server side — `lms log stream --source server --json`
gives you the complete request body without touching Agency at all (see "Seeing the exact wire
request" above). Reach for that first.

If raw HTTP bytes are genuinely needed from inside Agency — a remote backend, say — a new
`PipelinePolicy` has to be added, and it **must redact the `Authorization`/API-key header** before
logging. Don't log headers verbatim.

### `Agent:LogToolPayloads` is a separate, unrelated knob

`Agent:LogToolPayloads` (`src/Harness/Agency.Harness.Console/appsettings.json`, consumed in
`src/Harness/Agency.Harness/Agent.cs`) only gates tool-call input/error payload logging — it has
nothing to do with LLM chat request/response bodies. Flip it on alongside the recipe above if
you're debugging a tool-call issue specifically, but don't expect it to surface LLM content, and
don't conflate the two when reading logs.

### OpenTelemetry traces/metrics never carry request/response content

The `FileExport` traces and metrics pipelines capture span/tag data only (`agent.model`, token
counts, tool names as `Activity` tags) — no request/response body content ever flows through spans
or metrics. Logs (via the Serilog provider, per the gotchas above) are the only channel for LLM
content visibility.

### Where the log file lands

`{RepoRoot}/logs/app-<timestamp>.log` — a Serilog rolling file sink (`rollingInterval: Infinite`,
100MB roll-on-size, 30 retained files), configured in the same `AddLogging` method. A fresh file
per process start; it does not append across runs.
