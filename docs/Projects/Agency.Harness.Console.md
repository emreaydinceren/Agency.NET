# Agency.Harness.Console

#console #repl #chat #agentic #interactive #telemetry

## What It Is

`Agency.Harness.Console` is the terminal entry point that wires [[Agency.Harness]]'s `Agent` and `ChatSession` into an interactive Spectre.Console REPL, handling multi-turn input, slash-command dispatch, inline model switching, streaming Markdown rendering, Ctrl+C interruption, and structured OpenTelemetry file export through the .NET Generic Host.

**Namespace:** `Agency.Harness.Console`

## API Surface

This is an executable project (`<OutputType>Exe</OutputType>`). All types are `internal`, exposed to the test project via `[assembly: InternalsVisibleTo("Agency.Harness.Console.Test")]`. There are no public APIs for consumption by other libraries.

`IChatOutput` is the primary internal abstraction for all console rendering:

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

`IAgentFactory` is the internal abstraction for creating `Agent` instances by client name and model:

```csharp
// File: src/Harness/Agency.Harness.Console/IAgentFactory.cs
using Agency.Harness;

internal interface IAgentFactory
{
    Agent CreateAgent(string? clientName, string? modelName, bool stream);
}
```

## Registration

`Program.Main` builds a Generic Host and registers all services before creating a single DI scope to run the session:

```csharp
// File: src/Harness/Agency.Harness.Console/Program.cs
using Agency.Harness;
using Agency.Harness.Console.Telemetry;
using Agency.Harness.Contexts;
using Agency.Harness.Hooks.Configuration;
using Agency.Harness.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

System.Console.OutputEncoding = System.Text.Encoding.UTF8;
System.Console.InputEncoding = System.Text.Encoding.UTF8;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddTelemetry(builder.Configuration);          // traces, metrics, Serilog
builder.Services.AddSingleton<IChatOutput, ConsoleOutput>();
builder.Services.AddTransient<Models>();
builder.Services.AddOptions<AgentOptions>().BindConfiguration("Agent").ValidateOnStart();
builder.Services.AddAgencyConfiguredHooks(builder.Configuration);
builder.Services.AddScoped<IAgentFactory, AgentFactory>();

// Default Agent resolved from config
builder.Services.AddScoped(sp =>
{
    var agentFactory = sp.GetRequiredService<IAgentFactory>();
    var options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
    return agentFactory.CreateAgent(null, null, options.Stream);
});

// ToolContext with pre-registered tools (including recursive AgentTool)
builder.Services.AddScoped(sp =>
{
    var agentFactory = sp.GetRequiredService<IAgentFactory>();
    var options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
    var registry = new ToolRegistry();
    registry.Register(new ExecutePowershellTool());
    registry.Register(new ReadFileTool());
    registry.Register(new WriteFileTool());
    registry.Register(new AgentTool((clientName, modelName, stream) =>
        (options, agentFactory.CreateAgent(clientName, modelName, stream), registry)));
    return new ToolContext { Registry = registry };
});

builder.Services.AddScoped<ConsoleChatSession>();

// After the session finishes, flush Serilog before process exit
await Log.CloseAndFlushAsync();
```

### Configuration

`appsettings.json` ships with defaults for a local LM Studio instance. The `OpenTelemetry` section maps to `TelemetryOptions` / `FileExportOptions` and its nested signal options. All sub-keys are optional — omitting them keeps the defaults shown below.

```json
{
  "Agent": {
    "DefaultClientName": "LocalVia-OpenAI-API",
    "DefaultModel": "google/gemma-4-e2b",
    "LLmClients": [
      {
        "Name": "LocalVia-OpenAI-API",
        "ClientType": "OpenAI",
        "BaseUrl": "http://llm-host.example:1234/v1",
        "ApiKey": "lm-studio",
        "Timeout": "00:10:00"
      },
      {
        "Name": "LocalVia-Claude-API",
        "ClientType": "Claude",
        "BaseUrl": "http://llm-host.example:1234",
        "ApiKey": "lm-studio"
      }
    ]
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning",
      "System": "Warning"
    }
  },
  "OpenTelemetry": {
    "ServiceName": "Agency.Harness.Console",
    "FileExport": {
      "OutputDirectory": "./logs",
      "Traces": {
        "Enabled": true,
        "FilePrefix": "traces",
        "SamplingRatio": 1.0
      },
      "Metrics": {
        "Enabled": true,
        "FilePrefix": "metrics",
        "ExportIntervalMs": 15000
      },
      "Logs": {
        "Enabled": true,
        "FilePrefix": "app",
        "MinimumLevel": "Information"
      }
    }
  },
  "Hooks": {}
}
```

## How It Works

`Program.Main` resolves `ConsoleChatSession` from the DI scope and calls `RunAsync`. The session enters a REPL until the user exits.

```
❯ [user types a message]
● response rendered as Markdown here
  ↳ +230 in, +47 out  [Success]

❯ /exit
Session ended  ·  3 turns  ·  1,234 in, 321 out total
```

### REPL loop

Each iteration:
1. `ConsoleInputReader.ReadLineAsync` renders a bordered Spectre.Console prompt and reads input with history navigation and `/`-autocomplete.
2. Bare `exit` or `quit` text terminates the session immediately.
3. Input starting with `/` is dispatched to `CommandManager.ExecuteCommandAsync`, which matches a `Command` from `CommandRegistry.Commands` and returns a `CommandContinuation`.
4. All other input is forwarded to `ChatSession.SendAsync`, which yields `IAsyncEnumerable<AgentEvent>`.

### Event handling

| Event | Console behaviour |
|---|---|
| `TextDeltaEvent` | Appended to `streamingBuffer` (`StringBuilder`) |
| `AssistantTurnEvent` (streaming) | Flushes `streamingBuffer` through `MarkdownRenderer.Print`; stops spinner |
| `AssistantTurnEvent` (batch) | Prints message content directly; stops spinner |
| `ToolInvokedEvent` | Prints a rounded bordered panel with tool name and truncated result (100 chars, 3 lines); errors printed in red |
| `AgentResultEvent` | Prints per-turn `↳ +N in, +N out [Status]` delta |

### Ctrl+C handling

A single `CancellationTokenSource` (`sessionCts`) covers the whole session. `Console.CancelKeyPress` cancels the token but sets `e.Cancel = true` to suppress process termination. The REPL checks `sessionCts.IsCancellationRequested` after each turn and breaks out if set. Once cancelled, the session ends; `sessionCts` is never recreated.

### Slash commands

Built-in commands are registered in `CommandRegistry`'s static constructor and shared across all `CommandManager` instances:

| Command | Behaviour |
|---|---|
| `/clear` | Clears the console and resets `ChatSession` history |
| `/exit` | Exits the session (`CommandContinuation.ExitSession`) |
| `/quit` | Exits the session (`CommandContinuation.ExitSession`) |
| `/help` | Returns `CommandContinuation.Continue` (no-op currently) |
| `/model` | Opens `ConsolePicker` to hot-swap the active `Agent` |

New commands can be added via `CommandRegistry.RegisterCommand` or `RegisterAsyncCommand` without modifying `CommandManager`.

### Model switching

`/model` invokes `ModelsCommand.RunSelectModelCommandAsync`, which calls `Models.GetAllAsync()` to list all configured LLM clients and their models, presents them in a searchable `ConsolePicker` grouped by client, and calls `ConsoleChatSession.SetAgent(newAgent)` to hot-swap the active `Agent` without restarting the session.

## Agent Tools

The `ToolContext` registered in DI includes four tools sourced from [[Agency.Harness]]:

| Tool | Purpose |
|---|---|
| `ExecutePowershellTool` | Executes PowerShell commands and returns stdout/stderr |
| `ReadFileTool` | Reads file content from the local file system |
| `WriteFileTool` | Writes content to the local file system |
| `AgentTool` | Spawns a sub-`Agent` with the shared `ToolRegistry`, enabling recursive agent calls |

`AgentTool` receives a factory lambda that captures both `AgentOptions` and the current `ToolRegistry`, so sub-agents share the same tool set as the parent.

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

`AddTelemetry(IConfiguration)` reads the `OpenTelemetry` config section and wires up to three signal pipelines — each can be independently disabled via its `Enabled` flag:

- **Traces** — `FileSpanExporter` writes one span per line to a UTC date-stamped file (`traces-yyyy-MM-dd.log`). The file rolls at midnight. Sampling ratio is configurable via `SamplingRatio` (default `1.0` = always on).
- **Metrics** — `FileMetricExporter` appends timestamped metric blocks to a UTC date-stamped file (`metrics-yyyy-MM-dd.log`) on a periodic cycle (default every 15 seconds, configurable via `ExportIntervalMs`).
- **Logs** — Serilog writes to a **per-session** stamped file (`app-yyyy-MM-dd-HHmmss.log`) using `RollingInterval.Infinite`. The minimum level is configurable via `MinimumLevel` (default `Information`). When disabled, a `NullLoggerProvider` is registered instead.

All files are written under the directory specified by `FileExport.OutputDirectory` (default `./logs`), which is created automatically at startup. The `host.name` attribute is added to the OTel resource for all signals.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Harness]] | Consumes `Agent`, `ChatSession`, `AgentEvent` subtypes, `AgentOptions`, `ToolContext`, `ToolRegistry`, and all built-in tool types (`ExecutePowershellTool`, `ReadFileTool`, `WriteFileTool`, `AgentTool`) |
| [[Agency.Llm.Common]] | `Models` (from `Agency.Llm.Common`) enumerates configured LLM clients; `AgentFactory` calls `Models.CreateChatClient` to resolve the concrete `IChatClient` |
| [[Agency.Llm.OpenAI]] | Instantiated by `Models.CreateChatClient` when `ClientType = "OpenAI"` |
| [[Agency.Llm.Claude]] | Instantiated by `Models.CreateChatClient` when `ClientType = "Claude"` |

## Design Notes

- **`IChatOutput` abstraction** — `ConsoleOutput` (Spectre.Console) and `TextWriterChatOutput` (plain `TextWriter`) both implement `IChatOutput`. `Agency.Harness.Console.Test` injects `TextWriterChatOutput` to assert on rendered output without a real terminal. The `[assembly: InternalsVisibleTo("Agency.Harness.Console.Test")]` attribute in `Program.cs` grants this access.
- **Streaming buffer instead of live print** — `TextDeltaEvent` tokens are accumulated in a `StringBuilder` (`streamingBuffer`) and flushed only when the `AssistantTurnEvent` arrives. This prevents partial streaming text from interleaving with `ToolInvokedEvent` bordered panels that may fire mid-turn.
- **`CommandRegistry` uses a static constructor** — commands are registered once at class-load time and the resulting `IReadOnlyList<Command>` is shared across all `CommandManager` instances. Adding a command only requires calling `RegisterCommand` or `RegisterAsyncCommand` in the static constructor; no change to `CommandManager` is needed.
- **`sessionCts` is never recreated** — once Ctrl+C fires, `sessionCts.Cancel()` is called and `sessionCts.IsCancellationRequested` remains `true`. The REPL detects this after the interrupted turn and exits. The session is not designed to be restartable after cancellation.
- **Per-session log files, not daily-rolling** — traces and metrics use `DailyRollingFileWriter` (rolls at UTC midnight), but Serilog logs use a single per-session file stamped with `yyyy-MM-dd-HHmmss` and `RollingInterval.Infinite`. This means each process launch produces a distinct log file, which makes correlating a log file to a specific run trivial without needing to filter by time within a shared daily file.
- **Independent per-signal enable flags** — setting `Enabled: false` for traces or metrics skips building the corresponding OTel provider entirely (no `TracerProvider`/`MeterProvider` is registered). Setting `Enabled: false` for logs registers `NullLoggerProvider` rather than Serilog, so the `ILogger<T>` injection chain remains valid throughout the application.
- **Three-source hook fold** — `AgentFactory.CreateAgent` assembles hooks via `AgentHooksExtensions.Fold(BaselineHooks, ConfiguredHooks, UserHooks)`, producing a single merged `AgentHooks?`. The order is intentional: `BaselineHooks` (memory pipeline, registered by `AddAgencyMemory`) forms the foundation; `ConfiguredHooks` (JSON-driven declarative hooks, registered by `AddAgencyConfiguredHooks`) layer on top; `UserHooks` (code-supplied hooks set directly on `AgentOptions`) override both. This lets the memory subsystem always run first, JSON config extend it without code changes, and caller code have the final word.
