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
   category you need. Optionally also set `Agent.LogToolPayloads` to `true` (see "A separate knob"
   below).
2. Run a repro — `dotnet run` (or just relaunch the console) picks up the JSON change immediately;
   no recompile needed since this only touches config. The log lands at
   `{RepoRoot}/logs/app-<timestamp>.log` (a fresh file per process start).

Revert the `CategoryOverrides`/`MinimumLevel` entries once you're done — `Verbose` is noisy and
full LLM content logging is a diagnostic carve-out, not something that should stay on by default.

## Gotchas

### `Logging:LogLevel` in appsettings.json is largely dead for this app

`AddLogging` in `TelemetryServiceCollectionExtensions.cs` calls `services.AddLogging(b => {
b.ClearProviders(); ... b.AddProvider(new SerilogLoggerProvider(...)); })`. Serilog is the only
provider that ends up registered. The standard ASP.NET-style `Logging:LogLevel` section in
`appsettings.json` has no effect here — the real minimum-level knob is
`OpenTelemetry:FileExport:Logs:MinimumLevel`, bound to `LogFileOptions.MinimumLevel`
(`Telemetry/TelemetryOptions.cs`). Valid values (case-insensitive): `Verbose`, `Debug`,
`Information`, `Warning`, `Error`, `Fatal` (Serilog's `LogEventLevel`, mapped to
`Microsoft.Extensions.Logging.LogLevel` via `ToMsLogLevel`). Defaults to `Information` if the
`Logs` section is omitted.

### Why `Verbose` alone doesn't show LLM content

`AddLogging` builds its Serilog `LoggerConfiguration` with a per-category override loop driven by
`LogFileOptions.CategoryOverrides`, which **defaults** to `{ "Microsoft": "Warning", "System":
"Warning" }`. The chat client pipeline (`OpenAIClient.CreateChatClient` in
`src/Llm/Agency.Llm.OpenAI/OpenAIClient.cs`) calls `.UseLogging(loggerFactory)` —
Microsoft.Extensions.AI's built-in `LoggingChatClient` middleware, the only thing in the codebase
that logs full LLM request/response message content (at Trace level). Its logger category starts
with `Microsoft.Extensions.AI`, so the default `"Microsoft" → Warning` override silences it
completely, even with the global minimum level set to `Verbose`.

Serilog resolves per-category overrides by longest-prefix match, so adding a more specific
`"Microsoft.Extensions.AI": "Verbose"` entry to `CategoryOverrides` in `appsettings.json` (see the
recipe above) carves out just that category, without weakening the default Microsoft/System
suppression for everything else (ASP.NET/EF/etc. infrastructure logs stay quiet at Warning, which is
still useful noise reduction). No code change needed — this was originally a hardcoded
`.MinimumLevel.Override(...)` call requiring a rebuild per category, moved to config specifically so
the next debugging session doesn't have to touch code for this.

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

If raw HTTP bytes are ever genuinely needed, a new `PipelinePolicy` has to be added — and it
**must redact the `Authorization`/API-key header** before logging. Don't log headers verbatim.

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
