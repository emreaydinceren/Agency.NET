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
    Agent CreateAgent(string? clientName, string? modelName);
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
    return agentFactory.CreateAgent(null, null);
});

// MCP servers (opt-in): when an "Mcp" section lists servers, a McpClientPool singleton is
// registered and its discovered tools are folded into the registry below.

// ToolContext with pre-registered tools (recursive AgentTool, MCP tools, optional progressive disclosure)
builder.Services.AddScoped(sp =>
{
    var agentFactory = sp.GetRequiredService<IAgentFactory>();
    var options = sp.GetRequiredService<IOptions<AgentOptions>>().Value;

    var inner = new ToolRegistry();
    IToolRegistry outward = null!;                 // captured by the AgentTool closure (assigned after population)
    inner.Register(new ExecutePowershellTool());
    inner.Register(new ReadFileTool());
    inner.Register(new WriteFileTool());
    inner.Register(new AgentTool((clientName, modelName) =>
        (options, agentFactory.CreateAgent(clientName, modelName), outward)));

    var mcpToolNames = new HashSet<string>();
    var mcpPool = sp.GetService<McpClientPool>();   // null when MCP is not configured
    if (mcpPool is not null)
    {
        foreach (ITool tool in mcpPool.Tools)
        {
            inner.Register(tool);
            mcpToolNames.Add(tool.Definition.Name);
        }
    }

    // Opt-in progressive disclosure: MCP tools ship name + one-line summary only (schema withheld
    // behind tool_help); native/internal tools are revealed in full.
    outward = options.ProgressiveDiscovery
        ? new ProgressiveDiscoveryToolRegistry(inner, mcpToolNames)
        : inner;
    return new ToolContext { Registry = outward };
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
    "ProgressiveDiscovery": false,
    "LogToolPayloads": false,
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
| `/dump-context` | Prints the exact next-turn context — system prompt, messages, and tools — for inspection. Tools are grouped by origin (Built-in, then each MCP server) and rendered compactly; reflects progressive disclosure when enabled. |

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

When MCP servers are configured, their tools are folded into the same registry (see [[Agency.Harness]] · *Connecting MCP Servers*). When `Agent:ProgressiveDiscovery` is `true`, the registry is wrapped in a `ProgressiveDiscoveryToolRegistry`, which advertises every tool with a one-line summary + placeholder schema and adds a `tool_help` tool that reveals full detail on demand.

`AgentTool` receives a factory lambda that captures both `AgentOptions` and the **outward-facing** registry (the progressive wrapper when enabled, else the plain `ToolRegistry`), so sub-agents share the same tool set — and the same disclosure mode — as the parent.

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
- **Progressive-discovery wrap uses a deferred closure capture** — `AgentTool` is registered *during* population of the inner `ToolRegistry`, but its factory must hand sub-agents the *outward* registry (the progressive wrapper, decided only *after* population). The block declares `IToolRegistry outward = null!` up front, the `AgentTool` closure captures the **variable** `outward`, and `outward` is assigned the final (wrapped-or-plain) registry just before `ToolContext` is returned. The closure only runs at sub-agent-creation time — long after assignment — so the captured value is always populated. Without this, sub-agents would either see the unwrapped registry (no progressive disclosure) or force the wrap decision too early.
- **`MarkdownRenderer` is a line-based translator, not a CommonMark parser** — `Print` walks the text one line at a time, matching each against a fixed set of prefix constructs (code fences, headings, blockquotes, lists, rules) and converting inline spans to Spectre markup. GFM pipe tables are the one *multi-line* construct it handles: `TryParseTable` keys off the delimiter row (`| --- | :--: |`) immediately following a header row — keying on the delimiter, not the presence of a `|`, is what stops ordinary prose containing a pipe from being mis-parsed. `BuildTable` then emits a Spectre `Table`, padding short rows and truncating long ones to the header column count (Spectre throws when a row's cell count ≠ column count). Anything the renderer doesn't recognise falls through to a verbatim paragraph line, so unknown constructs degrade to plain text rather than erroring.
- **`dump-context` reconstructs tool grouping from the MCP pool** — `ToolDefinition` carries no origin, and the registry is a flat name-keyed map, so the command resolves `McpClientPool` from DI and uses its `ToolNamesByServer` map to bucket tools into *Built-in* vs each *server · MCP* group. It also suppresses the repetitive `{"type":"object"}` placeholder schema and renders single-line descriptions inline as `name: summary`. This is display-only — it changes nothing about what is sent to the model; it reconstructs, for human readability, provenance the wire format discards.
