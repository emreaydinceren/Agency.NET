# Agency.Harness.Console

#console #repl #chat #agentic #interactive #telemetry #mcp #skills

## What It Is

`Agency.Harness.Console` is the terminal entry point that wires [[Agency.Harness]]'s `Agent` and `ChatSession` into an interactive Spectre.Console REPL, handling multi-turn input, slash-command dispatch, inline model switching, streaming Markdown rendering (including GFM pipe tables), permission prompts, Ctrl+C interruption, optional memory, MCP-server tool discovery, skill `/commands`, and structured OpenTelemetry file export through the .NET Generic Host.

**Namespace:** `Agency.Harness.Console`

## Prerequisites

- An LLM endpoint must be configured under `Agent:LLmClients` (the shipped defaults target a local LM Studio instance at `http://llm-host.example:1234`). `Agent:DefaultClientName` and `Agent:DefaultModel` must resolve to one of those clients.
- A stable per-installation **user id** (`Agent:UserId`) partitions memory. When absent it is generated once and persisted back into `appsettings.json` on first run (see `UserIdConfiguration`). The id is substituted for the `{userId}` placeholder in tool calls by `UserIdPlaceholderHook`.
- **Memory** is opt-in via `Memory:Enabled` (default `false`). When enabled, the configured `Memory:Provider` backend (`postgres` — requires its Docker container — or `sqlite`) and the embeddings endpoint (`Embedding`) must be reachable; schema init fails fast at startup otherwise.
- **MCP servers** are opt-in via the `Mcp` section. Server paths may use the `${RepoRoot}` and `${Configuration}` portability tokens (see `McpConfigResolver`). MCP startup is skipped entirely under `DOTNET_ENVIRONMENT=Test`.
- **Skills** are discovered from `Skills:Directories` (defaults to `./.agency/skills` then `~/.agency/skills`, project-first). Skill shell execution can be turned off with `Skills:DisableShellExecution`.

## API Surface

This is an executable project (`<OutputType>Exe</OutputType>`). All types are `internal`, exposed to the test project via `[assembly: InternalsVisibleTo("Agency.Harness.Console.Test")]`. There are no public APIs for consumption by other libraries.

### Rendering & agent creation

`IChatOutput` is the primary internal abstraction for all console rendering (implemented by `ConsoleOutput` for Spectre.Console and `TextWriterChatOutput` for plain text):

```csharp
// File: src/Harness/Agency.Harness.Console/IChatOutput.cs
internal interface IChatOutput
{
    void WriteLine();
    void WriteLine(string? colorName, string text);
    void WriteLine(string text);
    void Write(string? colorName, string text);
    void Write(string text);
    void WriteLineMarkdown(string text);
    void WriteMarkup(string text);
    void WriteLineMarkup(string text);
    void StartSpinner(string markup = "[yellow]Thinking...[/]");
    void StopSpinner();
    void WriteMarkdownInBorderedPanel(string header, string text);
}
```

Agent construction (`IAgentFactory` / `AgentFactory`) lives in [[Agency.Harness]] — the host only calls `AddAgencyAgent()` to register it (see Registration below).

`MarkdownRenderer` translates Markdown to Spectre markup, including GFM pipe tables:

```csharp
// File: src/Harness/Agency.Harness.Console/MarkdownRenderer.cs
internal static class MarkdownRenderer
{
    internal static void Print(string text);

    internal sealed record ParsedTable(
        IReadOnlyList<string> Headers,
        IReadOnlyList<IReadOnlyList<string>> Rows);

    // Parses a GFM pipe table starting at `start`; `next` is the first line after the table.
    internal static bool TryParseTable(string[] lines, int start, out ParsedTable? table, out int next);
    internal static Spectre.Console.Table BuildTable(ParsedTable table);
}
```

### Host-owned identity, portability & determinism

```csharp
// File: src/Harness/Agency.Harness.Console/UserIdConfiguration.cs
using Microsoft.Extensions.Configuration;

internal static class UserIdConfiguration
{
    internal const string ConfigKey = "Agent:UserId";

    // Returns the existing Agent:UserId, or generates one via idFactory, persists it to
    // appsettings.json, and writes it into the in-memory configuration for this run.
    internal static string EnsureUserId(IConfiguration configuration, string appSettingsPath, Func<string> idFactory);
}
```

```csharp
// File: src/Harness/Agency.Harness.Console/UserIdPlaceholderHook.cs
using Agency.Harness.Hooks;

// OnPreToolUse hook: rewrites the literal "{userId}" placeholder in tool arguments to the
// session's resolved user id. No-op when the placeholder is absent.
internal static class UserIdPlaceholderHook
{
    internal const string Placeholder = "{userId}";
    internal static AgentHooks Hooks { get; }
}
```

```csharp
// File: src/Harness/Agency.Harness.Console/McpConfigResolver.cs
using Agency.Harness.Tools;

// Expands ${RepoRoot}/${Configuration} tokens in MCP server config so committed paths stay portable.
internal static class McpConfigResolver
{
    public static void Expand(McpClientOptions options, string repoRoot, string configuration);
    public static string? FindRepoRoot(string startDirectory);   // nearest ancestor containing .git
    public static string ResolveConfiguration(string baseDirectory); // Debug/Release from a bin/<cfg>/ path
}
```

```csharp
// File: src/Harness/Agency.Harness.Console/FixedTimeProvider.cs
// A TimeProvider that always returns a fixed instant; registered only under DOTNET_ENVIRONMENT=Test.
internal sealed class FixedTimeProvider(DateTimeOffset instant) : TimeProvider
{
    public override DateTimeOffset GetUtcNow();
}
```

### Commands

```csharp
// File: src/Harness/Agency.Harness.Console/Commands/Command.cs
internal class Command(string Name, string Description, string? ArgumentHint = null)
{
    public string CommandText { get; }
    public string Description { get; }
    public string? ArgumentHint { get; }   // autocomplete hint shown in the "/" picker
    public required Func<string, ConsoleChatSession, Task<CommandContinuation>> Execute { get; set; }
}

// File: src/Harness/Agency.Harness.Console/Commands/CommandContinuation.cs
internal enum CommandContinuation { Continue, Clear, ExitSession }
```

```csharp
// File: src/Harness/Agency.Harness.Console/Commands/CommandRegistry.cs
using Agency.Harness.Skills;

internal static class CommandRegistry
{
    internal static IReadOnlyList<Command> Commands { get; }
    internal static void RegisterCommand(string commandText, string description,
        Func<string, ConsoleChatSession, CommandContinuation> executeFunc, string? argumentHint = null);
    internal static void RegisterAsyncCommand(string commandText, string description,
        Func<string, ConsoleChatSession, Task<CommandContinuation>> executeFunc, string? argumentHint = null);
    // Registers each UserInvocable skill as a "/skill-name" command.
    internal static void RegisterSkillCommands(ISkillCatalog catalog);
}
```

`CommandManager` matches typed input against the registered commands and dispatches; `DumpContextCommand.Run` and `ModelsCommand.RunSelectModelCommandAsync` implement the `/dump-context` and `/model` commands respectively.

## Registration

`Program.Main` builds a Generic Host and registers all services before creating a DI scope to run the session. Memory, MCP, and skills are conditionally registered.

```csharp
// File: src/Harness/Agency.Harness.Console/Program.cs
using Agency.Harness.Console.Commands;
using Agency.Harness.Console.Telemetry;
using Agency.Harness.Hooks;
using Agency.Harness.Hooks.Configuration;
using Agency.Harness.Permissions;
using Agency.Harness.Skills;
using Agency.Harness.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddTelemetry(builder.Configuration);          // traces, metrics, Serilog
builder.Services.AddSingleton<IChatOutput, ConsoleOutput>();
builder.Services.AddTransient<Models>();

// Deterministic clock under Test so agent turns are byte-stable for HTTP-cache replay.
if (builder.Environment.IsEnvironment("Test"))
{
    builder.Services.AddSingleton<TimeProvider>(
        new FixedTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
}

// Resolve/persist Agent:UserId before binding (skipped under Test).
if (builder.Environment.IsEnvironment("Test") == false)
{
    string appSettingsPath = Path.Combine(builder.Environment.ContentRootPath, "appsettings.json");
    UserIdConfiguration.EnsureUserId(builder.Configuration, appSettingsPath, static () => Guid.NewGuid().ToString());
}

builder.Services.AddOptions<AgentOptions>().BindConfiguration("Agent").ValidateOnStart();

// Compose the {userId}-substitution hook onto any user-supplied hooks.
builder.Services.PostConfigure<AgentOptions>(options =>
    options.UserHooks = options.UserHooks is { } existing
        ? existing.Compose(UserIdPlaceholderHook.Hooks)
        : UserIdPlaceholderHook.Hooks);

builder.Services.AddAgencyConfiguredHooks(builder.Configuration);
builder.Services.AddAgencyPermissions(builder.Configuration);

// Memory (opt-in via Memory:Enabled) registers embeddings, the chosen store provider, the
// consolidator/distiller/hygiene background services, and a baseline AgentHooks singleton.

// MCP (opt-in, skipped under Test): expand ${RepoRoot}/${Configuration} tokens, then connect.
McpClientOptions? mcpOptions = builder.Environment.IsEnvironment("Test")
    ? null
    : builder.Configuration.GetSection("Mcp").Get<McpClientOptions>();
if (mcpOptions is { Servers.Length: > 0 })
{
    string repoRoot = McpConfigResolver.FindRepoRoot(AppContext.BaseDirectory) ?? AppContext.BaseDirectory;
    string configuration = McpConfigResolver.ResolveConfiguration(AppContext.BaseDirectory);
    McpConfigResolver.Expand(mcpOptions, repoRoot, configuration);
    var pool = await McpClientPool.CreateAsync(mcpOptions);
    builder.Services.AddSingleton(pool);
}

// Skills: discover roots, expose a ReloadableSkillCatalog + SkillContext + SkillWatcher, and
// register each user-invocable skill as a "/skill-name" command.
var reloadableCatalog = new ReloadableSkillCatalog(skillRoots);
builder.Services.AddSingleton<ISkillCatalog>(reloadableCatalog);
builder.Services.AddSingleton(new SkillContext { Catalog = reloadableCatalog });
builder.Services.AddSingleton(new SkillWatcher(skillRoots, reloadableCatalog.Reload));
CommandRegistry.RegisterSkillCommands(reloadableCatalog);

// Models + IAgentFactory + scoped default Agent (all from Agency.Harness):
builder.Services.AddAgencyAgent();

// ToolContext with built-in tools (ExecutePowershell/ReadFile/WriteFile, recursive AgentTool,
// SkillTool with a fork runner), MCP tools, and optional progressive disclosure.
builder.Services.AddScoped(sp => /* ... builds ToolRegistry / ProgressiveDiscoveryToolRegistry ... */);
builder.Services.AddScoped<ConsoleChatSession>();

await Log.CloseAndFlushAsync();   // after the session finishes
```

- `AddTelemetry(IConfiguration)` — the project's own extension; registers OTel file exporters and Serilog (see Observability).
- `FixedTimeProvider` / `UserIdConfiguration` / the `{userId}` hook are wired only conditionally (Test vs. production).
- Memory services (`AddAgencyEmbeddingsOpenAI`, `AddAgencyMemoryPostgres`/`AddAgencyMemorySqlite`, `AddAgencyMemory`, `AddAgencyConsolidator`, `AddAgencyHygiene`, `AddAgencyDistillerLlm`) are registered only when `Memory:Enabled` is `true`.

### Configuration

`appsettings.json` ships defaults for a local LM Studio instance. Key sections: `Agent` (clients, `DefaultClientName`/`DefaultModel`, `ProgressiveDiscovery` — **defaults to `true`** — `LogToolPayloads`, persisted `UserId`), `Memory` (`Enabled`, `Provider`), `Mcp` (`Servers` with `${RepoRoot}`/`${Configuration}` tokens), `ConnectionStrings`, `Embedding`, `Skills` (`Directories`, `DisableShellExecution`), `Permissions`, `Hooks`, and `OpenTelemetry`.

```json
{
  "Agent": {
    "DefaultClientName": "LocalVia-OpenAI-API",
    "DefaultModel": "google/gemma-4-e2b",
    "ProgressiveDiscovery": true,
    "LogToolPayloads": false,
    "LLmClients": [
      { "Name": "LocalVia-OpenAI-API", "ClientType": "OpenAI", "BaseUrl": "http://llm-host.example:1234/v1", "ApiKey": "lm-studio", "Timeout": "00:10:00", "SuppressThinking": true },
      { "Name": "LocalVia-Claude-API", "ClientType": "Claude", "BaseUrl": "http://llm-host.example:1234", "ApiKey": "lm-studio" }
    ]
  },
  "Memory": { "Enabled": false, "Provider": "postgres" },
  "Mcp": {
    "Servers": [
      {
        "Name": "memory", "Transport": "Stdio", "Command": "dotnet",
        "Arguments": [ "${RepoRoot}/src/Mcp/Agency.Mcp.Memory/bin/${Configuration}/net10.0/Agency.Mcp.Memory.dll" ],
        "EnvironmentVariables": { "Memory__Provider": "sqlite", "Memory__ConnectionString": "Data Source=agency-mcp-memory.db" }
      },
      { "Name": "notion", "Transport": "Stdio", "Command": "npx", "Arguments": [ "-y", "@notionhq/notion-mcp-server" ] }
    ]
  },
  "ConnectionStrings": {
    "PostgreSql": "Host=localhost;Port=5432;Database=dev_db;Username=dev_user;Password=dev_password",
    "Sqlite": "Data Source=agency-memory.db"
  },
  "Embedding": { "BaseUrl": "http://llm-host.example:1234/v1", "ApiKey": "lm-studio", "ModelId": "local-embedding-model", "Dimensions": 1024 },
  "Skills": { "Directories": [], "DisableShellExecution": false },
  "Permissions": { "Enabled": true, "Allow": [ "ReadFile" ], "Deny": [], "OnUnresolved": "Ask" },
  "OpenTelemetry": { "ServiceName": "Agency.Harness.Console", "FileExport": { "OutputDirectory": "./logs" } }
}
```

`appsettings.Test.json` overrides values for the functional-test environment, where MCP startup and user-id persistence are skipped and the clock is frozen.

## How It Works

`Program.Main` resolves `ConsoleChatSession` from the DI scope and calls `RunAsync`. The session prints a header, creates a `ChatSession` (with a `UserSpecificContext` carrying the resolved user id), and enters a REPL until the user exits.

```csharp
using Agency.Harness.Console;
using Microsoft.Extensions.DependencyInjection;

using var scope = host.Services.CreateScope();
var app = scope.ServiceProvider.GetRequiredService<ConsoleChatSession>();
await app.RunAsync();
```

```text
❯ [user types a message]
● response rendered as Markdown (headings, lists, code fences, GFM tables) here
  ↳ +230 in, +47 out  12.4 tok/s  [Success]

❯ /exit
Session ended  ·  3 turns  ·  1,234 in, 321 out total
```

### REPL loop

Each iteration:
1. `ConsoleInputReader.ReadLineAsync` renders a bordered prompt and reads input with history navigation, multi-line entry (Ctrl+Enter), and `/`-autocomplete via `ConsolePicker`.
2. Bare `exit`/`quit` text terminates the session immediately.
3. Input starting with `/` is dispatched to `CommandManager.ExecuteCommandAsync`, returning a `CommandContinuation`.
4. All other input is forwarded to `ChatSession.SendAsync`, whose `IAsyncEnumerable<AgentEvent>` is drained by `ProcessStreamAsync`.

### Event handling and permission parking

`ProcessStreamAsync` renders each `AgentEvent`: `AssistantTurnEvent` prints message text as Markdown and tool-call panels; `ToolInvokedEvent` prints a rounded bordered panel with a truncated result; `IterationCompletedEvent` records the LLM duration for the `tok/s` readout; `AgentResultEvent` prints the per-turn `↳ +N in, +N out [Status]` delta. When a turn ends with `AwaitingPermission` the stream parks: `CollectPermissionResponses` renders a permission panel + picker (Allow once/always, Deny once/always — "Allow always" is hidden for hook-sourced asks), then `ResumeWithPermissionsAsync` continues the turn. Cancelling the picker (Escape) abandons the parked turn.

### Ctrl+C handling

A per-turn `CancellationTokenSource` is created each loop iteration. `Console.CancelKeyPress` cancels the current turn (sets `e.Cancel = true` to suppress process exit); a second Ctrl+C with no active turn exits the session. An interrupted turn prints `[interrupted]` and is not counted.

### Slash commands

Built-in commands are registered in `CommandRegistry`'s static constructor; skill commands are registered at startup from the catalog snapshot.

| Command | Behaviour |
|---|---|
| `/clear` | Clears the console and resets the `ChatSession` |
| `/exit`, `/quit` | Exit the session (`CommandContinuation.ExitSession`) |
| `/help` | No-op (`CommandContinuation.Continue`) |
| `/model` | Opens `ConsolePicker` (via `ModelsCommand`) to hot-swap the active `Agent` |
| `/dump-context` | Prints the exact next-turn context — system prompt, messages, and tools — without adding it to history |
| `/<skill-name>` | Renders a user-invocable skill body and submits it as a user turn |

### `/dump-context`

`DumpContextCommand.Run` calls `ChatSession.PreviewContext()` and prints the system prompt, conversation messages (text, tool-calls, tool-results), and tools. Tools are grouped by origin — *Built-in* first, then each MCP server in configured order — by reconstructing provenance from `McpClientPool.ToolNamesByServer` (the flat registry and `ToolDefinition` carry none). Single-line descriptions render inline as `name: summary`; the progressive-discovery `{"type":"object"}` placeholder schema is suppressed. Display-only — it changes nothing sent to the model.

### MCP config resolution

When the `Mcp` section lists servers, `McpConfigResolver.Expand` substitutes `${RepoRoot}` (nearest ancestor containing `.git`) and `${Configuration}` (derived from the `bin/<cfg>/` output path) in each server's command, arguments, and environment variables before `McpClientPool.CreateAsync` connects. This keeps committed server paths portable across machines, drives, OSes, and build configurations. MCP is skipped entirely under `DOTNET_ENVIRONMENT=Test`.

### Model switching

`/model` lists all configured clients/models via `Models.GetAllAsync()`, presents them in a searchable picker grouped by client, and calls `ConsoleChatSession.SetAgent(newAgent)` to hot-swap the active `Agent` (and the live `ChatSession`'s agent) without restarting.

## Agent Tools

The `ToolContext` registered in DI includes built-in tools sourced from [[Agency.Harness]], plus discovered MCP tools:

| Tool | Purpose |
|---|---|
| `ExecutePowershellTool` | Executes PowerShell commands and returns stdout/stderr |
| `ReadFileTool` | Reads file content from the local file system |
| `WriteFileTool` | Writes content to the local file system |
| `AgentTool` | Spawns a sub-`Agent` sharing the outward `ToolRegistry`, enabling recursive agent calls |
| `SkillTool` | Lets the model invoke skills; runs skill shell steps (unless disabled) and forks sub-agents for skill turns |

When MCP servers are configured their tools are folded into the same registry. When `Agent:ProgressiveDiscovery` is `true` (the default), the registry is wrapped in a `ProgressiveDiscoveryToolRegistry` that withholds **MCP** tool schemas behind a `tool_help` tool while revealing native/internal tools in full. `AgentTool` captures the *outward* registry so sub-agents share the same tool set and disclosure mode.

## Observability

`ConsoleChatSession` declares its own `ActivitySource` and `Meter`, both named `"Agency.Harness.Console"`.

**Traces**

| Span | Tags |
|---|---|
| `ConsoleChatSession.RunAsync` | `agent.client_type`, `agent.model`, `agent.usage.input_tokens`, `agent.usage.output_tokens` |

**Metrics**

| Instrument | Type | Description |
|---|---|---|
| `agent.console.sessions` | Counter | Total sessions started |
| `agent.console.errors` | Counter | Sessions that ended with an unhandled exception |
| `agent.console.commands` | Counter | Slash commands executed |
| `agent.console.turns` | Counter | Successful (non-interrupted) turns |
| `agent.console.tokens` | Counter | Tokens observed; tagged `agent.token.type` = `input` or `output` |
| `agent.console.session.duration` | Histogram (ms) | Wall-clock duration of the session |

All instruments carry `agent.client_type` and `agent.model` tags.

`AddTelemetry(IConfiguration)` reads the `OpenTelemetry` section and wires up to three independently-disableable signal pipelines:

- **Traces** — `FileSpanExporter` (over `DailyRollingFileWriter`) writes one span per line to a UTC date-stamped file (`traces-yyyy-MM-dd.log`), rolling at midnight; `SamplingRatio` is configurable (default `1.0`).
- **Metrics** — `FileMetricExporter` appends timestamped metric blocks (`metrics-yyyy-MM-dd.log`) on a periodic cycle (default 15 s, via `ExportIntervalMs`).
- **Logs** — Serilog writes to a **per-session** stamped file (`app-yyyy-MM-dd-HHmmss.log`) with `RollingInterval.Infinite`; `MinimumLevel` default `Information`. When disabled, a `NullLoggerProvider` is registered so the `ILogger<T>` chain stays valid.

All files live under `FileExport.OutputDirectory` (default `./logs`, created at startup). The `host.name` attribute is added to the OTel resource for all signals.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Harness]] | Consumes `Agent`, `ChatSession`, `AgentEvent` subtypes, `AgentOptions`, `AgentHooks`, `Models`/`IAgentFactory` (registered via `AddAgencyAgent`), `ToolContext`/`ToolRegistry`/`ProgressiveDiscoveryToolRegistry`, `McpClientPool`/`McpClientOptions`, permission types, and built-in tools (`ExecutePowershellTool`, `ReadFileTool`, `WriteFileTool`, `AgentTool`, `SkillTool`) |
| [[Agency.Harness.Skills]] | Loads `ISkillCatalog`/`ReloadableSkillCatalog`, `SkillContext`, `SkillWatcher`, `SkillRenderer`; registers each user-invocable skill as a `/command` |
| [[Agency.Llm.Common]] | `Models` enumerates configured LLM clients; the library's `AgentFactory` calls `Models.CreateChatClient` to resolve the `IChatClient`; binds `LlmClientOptions` |
| [[Agency.Llm.OpenAI]] | Instantiated by `Models.CreateChatClient` when `ClientType = "OpenAI"`; also used directly to build consolidator/distiller chat clients when memory is enabled |
| [[Agency.Llm.Claude]] | Instantiated by `Models.CreateChatClient` when `ClientType = "Claude"` |
| [[Agency.Embeddings.OpenAI]] | Registered (`AddAgencyEmbeddingsOpenAI`) when memory is enabled |
| [[Agency.Memory.Sql.Postgres]] / [[Agency.Memory.Sql.Sqlite]] | One is registered as the memory store based on `Memory:Provider` |
| [[Agency.Memory.Distiller]] / [[Agency.Memory.Consolidator]] / [[Agency.Memory.Hygiene]] | Background memory services registered when memory is enabled |

## Design Notes

- **`${RepoRoot}`/`${Configuration}` tokens for MCP portability** — committed `appsettings.json` must reference an MCP server binary whose absolute path differs per machine, drive, OS, and build configuration. Storing literal paths would break on every other checkout. `McpConfigResolver` resolves the repo root from the nearest `.git` ancestor and the configuration from the running `bin/<cfg>/` path, so the *same* committed config works everywhere a working tree exists.
- **MCP startup skipped under Test** — MCP servers are external processes absent from the functional-test/CI environment, and their discovered tools would be injected into the agent's tool list, changing the LLM request body and breaking offline HTTP-cache replay. The Test environment therefore never constructs `McpClientOptions`.
- **`FixedTimeProvider` for deterministic replay** — the agent's "Current date/time (UTC)" system-prompt line would otherwise vary per run, changing request bodies and the resulting HTTP-cache key. Under `DOTNET_ENVIRONMENT=Test` a frozen clock makes console turns byte-identical between local record and CI replay; production registers no `TimeProvider`, so the live clock is used.
- **Host-owned user identity via a placeholder hook** — the host, not the model, owns the user id. Tools (e.g. the memory MCP server) advertise a required `UserId`, and the model is instructed to pass the literal `{userId}`; `UserIdPlaceholderHook` rewrites it to the real GUID at `OnPreToolUse`. This makes it impossible for the model to fabricate or leak a wrong id, while remaining a no-op for tools that don't use the placeholder.
- **`Agent:UserId` is generated once and persisted** — `UserIdConfiguration.EnsureUserId` writes a new id back into `appsettings.json` and into the in-memory config for the current run, so memory partitions are stable across restarts without manual setup. Persistence is skipped under Test to keep the test config untouched and replay deterministic.
- **`MarkdownRenderer` is a line-based translator, not a CommonMark parser** — `Print` walks one line at a time matching prefix constructs (code fences, headings, blockquotes, lists, rules) and converting inline spans to Spectre markup. GFM pipe tables are the one *multi-line* construct: `TryParseTable` keys off the delimiter row (`| --- | :--: |`) following a header — keying on the delimiter, not the mere presence of `|`, stops ordinary prose containing a pipe from being mis-parsed. `BuildTable` emits a Spectre `Table`, normalising ragged rows to the header column count (Spectre throws when cell count ≠ column count). Unrecognised lines fall through to verbatim text.
- **`/dump-context` reconstructs tool grouping from the MCP pool** — `ToolDefinition` carries no origin and the registry is a flat name-keyed map, so the command resolves `McpClientPool` and uses `ToolNamesByServer` to bucket tools into *Built-in* vs each *server · MCP* group. It is display-only and reconstructs, for human readability, provenance the wire format discards.
- **Progressive-discovery wrap uses a deferred closure capture** — `AgentTool` is registered *during* population of the inner `ToolRegistry`, yet must hand sub-agents the *outward* registry (the progressive wrapper, decided only *after* population). The block declares `IToolRegistry outward = null!`, the `AgentTool`/`SkillTool` closures capture the **variable** `outward`, and it is assigned the final registry just before `ToolContext` is returned — long before any closure runs.
- **Two `IChatOutput` implementations** — `ConsoleOutput` (Spectre.Console, with a background spinner thread) drives the real terminal; `TextWriterChatOutput` writes plain text and is used by `Agency.Harness.Console.Test` to assert on rendered output without a terminal.
