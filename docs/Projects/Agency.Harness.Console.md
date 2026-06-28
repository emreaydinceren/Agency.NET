# Agency.Harness.Console

#console #repl #chat #agentic #interactive #telemetry #mcp #skills #ingestion #semantic-search #vector-store

## What It Is

`Agency.Harness.Console` is the terminal entry point that wires [[Agency.Harness]]'s `Agent` and `ChatSession` into an interactive Spectre.Console REPL, handling multi-turn input, slash-command dispatch, inline model switching, streaming Markdown rendering (including GFM pipe tables), permission prompts, Loop Kit progress rendering, Ctrl+C interruption, optional memory, MCP-server tool discovery, skill `/commands`, document ingestion + semantic search over a project-scoped vector store, and structured OpenTelemetry file export through the .NET Generic Host.

**Namespace:** `Agency.Harness.Console`

## Prerequisites

- An LLM endpoint must be configured under `Agent:LLmClients` (the shipped defaults target a local LM Studio instance at `http://llm.test:1234`). `Agent:DefaultClientName` and `Agent:DefaultModel` must resolve to one of those clients.
- A stable per-installation **user id** (`Agent:UserId`) partitions memory. When absent it is generated once and persisted back into `appsettings.json` on first run (see `UserIdConfiguration`). The id is substituted for the `{userId}` placeholder in tool calls by `UserIdPlaceholderHook`.
- **Memory** is opt-in via `Memory:Enabled` (default `false`). When enabled, the configured `Memory:Provider` backend (`postgres` — requires its Docker container — or `sqlite`) and the embeddings endpoint (`Embedding`) must be reachable; schema init fails fast at startup otherwise.
- **Ingestion & semantic search** are an independent data plane gated only on an embedding endpoint: when `Embedding:BaseUrl` is configured, embeddings, a vector store (`VectorStore:Provider`), the ingestion services, and the `SemanticSearchTool` are registered and a vector-store schema is initialised at startup — regardless of `Memory:Enabled`. The vector store needs `ConnectionStrings:VectorStoreSqlite` (default provider `sqlite`) or `ConnectionStrings:VectorStorePostgreSql`.
- **MCP servers** are opt-in via the `Mcp` section. Server paths may use the `${RepoRoot}` and `${Configuration}` portability tokens (see `McpConfigResolver`). MCP startup is skipped entirely under `DOTNET_ENVIRONMENT=Test`.
- **Skills** are discovered from `Skills:Directories` (defaults to `./.agency/skills` then `~/.agency/skills`, project-first). Skill shell execution can be turned off with `Skills:DisableShellExecution`.

## API Surface

This is an executable project (`<OutputType>Exe</OutputType>`). Types are `internal` (the configuration option classes are `public`), exposed to the test project via `[assembly: InternalsVisibleTo("Agency.Harness.Console.Test")]`. There are no public APIs intended for consumption by other libraries.

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

### Configuration option classes

The vector store / ingestion / retrieval data plane binds three `public sealed` option classes, each carrying its `SectionName` constant and defaults:

```csharp
// File: src/Harness/Agency.Harness.Console/Configuration/VectorStoreOptions.cs
public sealed class VectorStoreOptions
{
    public const string SectionName = "VectorStore";
    public string Provider { get; init; } = "sqlite";   // "sqlite" or "postgres"
}

// File: src/Harness/Agency.Harness.Console/Configuration/IngestionOptions.cs
public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";
    public int ChunkSize { get; init; } = 512;
    public int ChunkOverlap { get; init; } = 64;
    public string SearchPattern { get; init; } = "*.md";
}

// File: src/Harness/Agency.Harness.Console/Configuration/RetrievalOptions.cs
public sealed class RetrievalOptions
{
    public const string SectionName = "Retrieval";
    public int TopK { get; init; } = 5;
}
```

### Ingestion & retrieval services

`IProjectSessionState` (defined in [[Agency.Harness]]) is implemented here as a scoped, in-process session state that owns a stable `UserId`/`SessionId` and the set of loaded project scopes:

```csharp
// File: src/Harness/Agency.Harness.Console/Services/ProjectSessionState.cs
internal sealed class ProjectSessionState : IProjectSessionState   // IProjectSessionState is in Agency.Harness
{
    public ProjectSessionState(IOptions<AgentOptions> options);
    public string UserId { get; }                          // AgentOptions.UserId ?? Environment.UserName
    public string SessionId { get; }                       // new GUID ("N") per scope
    public IReadOnlyList<string> LoadedProjects { get; }
    public void LoadProject(string projectName);           // case-insensitive, idempotent add
    public void UnloadProject(string projectName);
}
```

```csharp
// File: src/Harness/Agency.Harness.Console/Services/IngestionCommandService.cs
internal sealed class IngestionCommandService(IVectorStore vectorStore, ITextSplitter textSplitter)
{
    // Runs a DefaultIngestionPipeline (FileLoader/DirectoryLoader → splitter → vector store),
    // returning the number of chunks stored, scoped by userId + optional sessionId/projectId.
    public Task<int> IngestFileAsync(string filePath, string userId, string? sessionId, string? projectId, CancellationToken ct = default);
    public Task<int> IngestDirectoryAsync(string directoryPath, string searchPattern, string userId, string? sessionId, string? projectId, CancellationToken ct = default);
    public static int CountFiles(string directoryPath, string searchPattern);   // recursive match count
}
```

```csharp
// File: src/Harness/Agency.Harness.Console/Services/DocumentContextHydrationService.cs
internal sealed class DocumentContextHydrationService(IVectorStore vectorStore, IProjectSessionState sessionState)
{
    public bool IsDirty { get; }
    public void MarkDirty();   // commands call this after ingest / project load/unload
    // When dirty, re-lists in-scope documents and builds a Fact string enumerating each
    // document with its [global|session|project:<id>] scope; caches it until next MarkDirty.
    public Task<string?> RefreshIfDirtyAsync(CancellationToken ct = default);
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

The static constructor registers the built-in commands, including the five ingestion/project commands wired to `AddFileCommand`, `AddFolderCommand`, and `ProjectsCommand`. `ScopeResolutionHelper.Resolve(IProjectSessionState)` returns the `(sessionId, projectId)` ingestion scope — auto-targeting the single loaded project when exactly one is loaded, otherwise prompting Global / Session / a loaded project / a new project name.

`CommandManager` matches typed input against the registered commands and dispatches; `DumpContextCommand.Run` and `ModelsCommand.RunSelectModelCommandAsync` implement the `/dump-context` and `/model` commands respectively.

## Registration

`Program.Main` builds a Generic Host and registers all services before creating a DI scope to run the session. Memory, the vector-store data plane, MCP, and skills are conditionally registered.

```csharp
// File: src/Harness/Agency.Harness.Console/Program.cs
using Agency.Embeddings.Common;
using Agency.Embeddings.OpenAI;
using Agency.Harness.Console.Commands;
using Agency.Harness.Console.Configuration;
using Agency.Harness.Console.Services;
using Agency.Harness.Console.Telemetry;
using Agency.Harness.Tools;
using Agency.Ingestion;
using Agency.Ingestion.SemanticKernel;
using Agency.VectorStore.Common;
using Agency.VectorStore.Sql.Postgres;
using Agency.VectorStore.Sql.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddTelemetry(builder.Configuration);          // traces, metrics, Serilog
builder.Services.AddSingleton<IChatOutput, ConsoleOutput>();

// Deterministic clock under Test; resolve/persist Agent:UserId; bind AgentOptions; compose the
// {userId}-substitution hook; AddAgencyConfiguredHooks / AddAgencyPermissions  (unchanged) …

// Embeddings — registered whenever Embedding:BaseUrl is set, independently of Memory:Enabled.
bool embeddingsConfigured = builder.Configuration["Embedding:BaseUrl"] is not null;
if (embeddingsConfigured)
{
    builder.Services.AddAgencyEmbeddingsOpenAI(opts =>
        builder.Configuration.GetSection(EmbeddingOptions.SectionName).Bind(opts));
}

// Memory (opt-in via Memory:Enabled) registers the chosen store provider, the consolidator/
// distiller/hygiene background services, and a baseline AgentHooks singleton  (unchanged) …

// Vector store / ingestion / retrieval. Option bindings are unconditional (always validated);
// the store, splitter, and ingestion services are gated on embeddingsConfigured.
builder.Services.Configure<VectorStoreOptions>(builder.Configuration.GetSection(VectorStoreOptions.SectionName));
builder.Services.Configure<IngestionOptions>(builder.Configuration.GetSection(IngestionOptions.SectionName));
builder.Services.Configure<RetrievalOptions>(builder.Configuration.GetSection(RetrievalOptions.SectionName));
builder.Services.AddScoped<IProjectSessionState, ProjectSessionState>();   // always available

if (embeddingsConfigured)
{
    builder.Services.AddSingleton<ITextSplitter>(sp => new SemanticKernelTextSplitter(chunkSize, chunkOverlap));
    builder.Services.AddSingleton<IVectorStore>(sp => /* PostgresKVStore or SqliteKVStore per VectorStore:Provider */);
    builder.Services.AddScoped<IngestionCommandService>();
    builder.Services.AddScoped<DocumentContextHydrationService>();
}

// MCP (opt-in, skipped under Test); Skills (catalog + watcher + /skill-name commands)  (unchanged) …

builder.Services.AddAgencyAgent();                          // Models + IAgentFactory + scoped default Agent (Agency.Harness)
builder.Services.AddAgencyLoop(builder.Configuration);      // binds LoopOptions from "Loop" section
builder.Services.AddScoped<GoalState>();                    // shared by LoopRunner and the goalkeeper tools

// ToolContext: built-in tools + AgentTool + SkillTool + EnableGoalkeeperTool + DisableGoalkeeperTool
// + MCP tools; plus SemanticSearchTool when an IVectorStore is resolvable;
// optionally wrapped in ProgressiveDiscoveryToolRegistry for MCP tools.
builder.Services.AddScoped(sp => /* ... */);
builder.Services.AddScoped<ConsoleChatSession>();

using IHost host = builder.Build();
// Schema init: memory schema when Memory:Enabled; vector-store schema when embeddingsConfigured.
await Log.CloseAndFlushAsync();   // after the session finishes
```

- `AddTelemetry(IConfiguration)` — the project's own extension; registers OTel file exporters and Serilog (see Observability).
- `FixedTimeProvider` / `UserIdConfiguration` / the `{userId}` hook are wired only conditionally (Test vs. production).
- Memory services (`AddAgencyMemoryPostgres`/`AddAgencyMemorySqlite`, `AddAgencyMemory`, `AddAgencyConsolidator`, `AddAgencyHygiene`, `AddAgencyDistillerLlm`) are registered only when `Memory:Enabled` is `true`.
- **Embeddings (`AddAgencyEmbeddingsOpenAI`) are registered whenever `Embedding:BaseUrl` is present**, decoupled from memory, so the vector store can embed without memory enabled.
- The **vector store** (`SqliteKVStore` or `PostgresKVStore` per `VectorStore:Provider`), the `SemanticKernelTextSplitter` (`ITextSplitter`), `IngestionCommandService`, and `DocumentContextHydrationService` are registered only when `embeddingsConfigured`. `IProjectSessionState` is registered unconditionally so session identity exists even without embeddings.
- After `host.Build()`, the vector-store schema is initialised (`SqliteKVStore.InitializeSchemaAsync` / `PostgresKVStore.InitializeSchemaAsync` with `Embedding:Dimensions`) when `embeddingsConfigured`, independently of the memory schema init.

### Configuration

`appsettings.json` ships defaults for a local LM Studio instance. Key sections: `Agent` (clients, `DefaultClientName`/`DefaultModel`, `ProgressiveDiscovery` — **defaults to `true`** — `LogToolPayloads`, persisted `UserId`), `Memory` (`Enabled`, `Provider`), `Mcp` (`Servers` with `${RepoRoot}`/`${Configuration}` tokens), `ConnectionStrings` (memory + vector-store connection strings), `Embedding`, `VectorStore` (`Provider`), `Ingestion` (`ChunkSize`/`ChunkOverlap`/`SearchPattern`), `Retrieval` (`TopK`), `Skills` (`Directories`, `DisableShellExecution`), `Permissions`, `Hooks`, `Loop`, and `OpenTelemetry`.

```json
{
  "Agent": {
    "DefaultClientName": "LocalVia-OpenAI-API",
    "DefaultModel": "google/gemma-4-e2b",
    "ProgressiveDiscovery": true,
    "LogToolPayloads": false,
    "LLmClients": [
      { "Name": "LocalVia-OpenAI-API", "ClientType": "OpenAI", "BaseUrl": "http://llm.test:1234/v1", "ApiKey": "lm-studio", "Timeout": "00:10:00", "SuppressThinking": true },
      { "Name": "LocalVia-Claude-API", "ClientType": "Claude", "BaseUrl": "http://llm.test:1234", "ApiKey": "lm-studio" }
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
    "Sqlite": "Data Source=agency-memory.db",
    "VectorStoreSqlite": "Data Source=agency-vectorstore.db",
    "VectorStorePostgreSql": "${ConnectionStrings:PostgreSql}"
  },
  "Embedding": { "BaseUrl": "http://llm.test:1234/v1", "ApiKey": "lm-studio", "ModelId": "local-embedding-model", "Dimensions": 1024 },
  "VectorStore": { "Provider": "sqlite" },
  "Ingestion": { "ChunkSize": 512, "ChunkOverlap": 64, "SearchPattern": "*.md" },
  "Retrieval": { "TopK": 5 },
  "Skills": { "Directories": [], "DisableShellExecution": false },
  "Permissions": { "Enabled": true, "Allow": [ "ReadFile" ], "Deny": [], "OnUnresolved": "Ask" },
  "Loop": {},
  "OpenTelemetry": { "ServiceName": "Agency.Harness.Console", "FileExport": { "OutputDirectory": "./logs" } }
}
```

`appsettings.json` deliberately omits the `ConnectionStrings:PostgreSql` key — it is a secret sourced from the shared `AgencySecrets` user-secrets vault. `Program.cs` loads that vault explicitly (via `AddUserSecrets("AgencySecrets")`, front-inserted so env vars / CLI still override) because `Host.CreateApplicationBuilder` only auto-loads user secrets in the Development environment. `VectorStorePostgreSql` then resolves from it through the `${ConnectionStrings:PostgreSql}` placeholder. See [[Agency.Configuration]] and the Configuration Manual.

`appsettings.Test.json` overrides values for the functional-test environment, where MCP startup and user-id persistence are skipped and the clock is frozen.

## How It Works

`Program.Main` resolves `ConsoleChatSession` from the DI scope and calls `RunAsync`. The session prints a header, creates a `ChatSession` (with a `UserSpecificContext` carrying the resolved user id, and — when a vector store is wired — a `SessionContext` carrying the `ProjectSessionState.SessionId`), and enters a REPL until the user exits.

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
1. **Document-inventory hydration** — when a `DocumentContextHydrationService` is registered, `RefreshIfDirtyAsync` is awaited; if it returns a Fact (the dirty flag was set by an ingest or project load/unload), the session's knowledge is refreshed via `ChatSession.SetKnowledge(new KnowledgeContext { Facts = [fact] })` so the next turn sees the in-scope document list.
2. `ConsoleInputReader.ReadLineAsync` renders a bordered prompt and reads input with history navigation, multi-line entry (Ctrl+Enter), and `/`-autocomplete via `ConsolePicker`.
3. Bare `exit`/`quit` text terminates the session immediately.
4. Input starting with `/` is dispatched to `CommandManager.ExecuteCommandAsync`, returning a `CommandContinuation`.
5. All other input is forwarded to `LoopRunner.RunAsync` (which internally calls `ChatSession.SendAsync`); the resulting `IAsyncEnumerable<AgentEvent>` is drained by `ProcessStreamAsync`.

### Ingestion & semantic-search flow

When `Embedding:BaseUrl` is configured, the data-plane commands ingest documents and feed retrieval:

- `/add-file <path>` (`AddFileCommand`) normalises the path, checks `IVectorStore.ListDocumentsAsync` for a prior ingest of the same source (prompting to re-ingest), resolves scope via `ScopeResolutionHelper`, then runs `IngestionCommandService.IngestFileAsync` inside a Spectre status spinner and marks the hydration service dirty.
- `/add-folder <path>` (`AddFolderCommand`) prompts for a glob (default `*.md`), counts matches with `IngestionCommandService.CountFiles`, guards bulk ingests (>50 files prompt for confirmation), resolves scope, runs `IngestionCommandService.IngestDirectoryAsync`, and marks dirty.
- `/projects-load <name>` / `/projects-unload <name>` (`ProjectsCommand`) mutate `IProjectSessionState.LoadedProjects` and mark the hydration service dirty so the next turn's Fact reflects the new scope; `/projects-list` renders all projects from `IVectorStore.ListProjectsAsync` with a loaded/available status column.
- Ingested documents become searchable through the `SemanticSearchTool` (see Agent Tools), which scopes its query to the session's `UserId`, `SessionId`, and loaded projects.

`ScopeResolutionHelper.Resolve` auto-targets the single loaded project (returns `projectId` = that project) when exactly one is loaded; otherwise it prompts for Global, Session (uses the live `SessionId`), an existing loaded project, or a new project name.

### Event handling and permission parking

`ProcessStreamAsync` renders each `AgentEvent`: `AssistantTurnEvent` prints message text as Markdown and tool-call panels; `ToolInvokedEvent` prints a rounded bordered panel with a truncated result; `IterationCompletedEvent` records the LLM duration for the `tok/s` readout; `AgentResultEvent` prints the per-turn `↳ +N in, +N out [Status]` delta. When a turn ends with `AwaitingPermission` the stream parks: `CollectPermissionResponses` renders a permission panel + picker (Allow once/always, Deny once/always — "Allow always" is hidden for hook-sourced asks), then `ResumeWithPermissionsAsync` continues the turn. Cancelling the picker (Escape) abandons the parked turn.

### Loop Kit event rendering

`ProcessStreamAsync` also recognizes the four Loop Kit `AgentEvent` subtypes ([[Agency.Harness]] `GoalSetEvent`, `TurnStartedEvent`, `VerdictEvent`, `LoopResultEvent`) and routes them to `ConsoleChatSession.RenderLoopEvent` — a static, `IChatOutput`-only renderer (so it is unit-testable via `TextWriterChatOutput`):

- `GoalSetEvent` → a cyan bordered banner showing the goal condition and the caps (`MaxTurns`, optional `Budget`/`Tokens`).
- `TurnStartedEvent` → a yellow `↺ Turn N  <directive>` line.
- `VerdictEvent` → green `✓ Done` or yellow `↻ Continue`, with the Goalkeeper's reason.
- `LoopResultEvent` → a gray `↳ Loop <Outcome> · N in, N out [$cost]` summary plus any final text.

> **Wiring.** `Program.cs` calls `AddAgencyLoop(config)` (binds `LoopOptions` from the `Loop` section), registers `GoalState` as a scoped DI service, and registers `EnableGoalkeeperTool` / `DisableGoalkeeperTool` in the `ToolContext` factory. `ConsoleChatSession` creates a `LoopRunner` per session (via `CreateLoopRunner`) and forwards all user input through `LoopRunner.RunAsync` instead of bare `ChatSession.SendAsync`. A **rendering guard** (`_goalObservedInCurrentTurn`) suppresses `TurnStartedEvent` and `LoopResultEvent` when no `GoalSetEvent` has been seen in the current turn, preventing loop infrastructure noise on plain (non-goal) turns.

### Ctrl+C handling

A per-turn `CancellationTokenSource` is created each loop iteration. `Console.CancelKeyPress` cancels the current turn (sets `e.Cancel = true` to suppress process exit); a second Ctrl+C with no active turn exits the session. An interrupted turn prints `[interrupted]` and is not counted.

### Slash commands

Built-in commands are registered in `CommandRegistry`'s static constructor; skill commands are registered at startup from the catalog snapshot.

| Command | Argument hint | Behaviour |
|---|---|---|
| `/clear` | | Clear the console and reset the `ChatSession` |
| `/exit`, `/quit` | | Exit the current chat session (`CommandContinuation.ExitSession`) |
| `/help` | | Show help information (no-op `CommandContinuation.Continue`) |
| `/model` | | Show model picker (via `ModelsCommand`) to hot-swap the active `Agent` |
| `/dump-context` | | Print the full context sent to the model (not added to history) |
| `/add-file` | `<path>` | Ingest a file into the vector store |
| `/add-folder` | `<path>` | Ingest all files in a folder into the vector store |
| `/projects-load` | `<name>` | Load a project into the session context |
| `/projects-unload` | `<name>` | Unload a project from the session context |
| `/projects-list` | | List all projects in the vector store |
| `/<skill-name>` | | Render a user-invocable skill body and submit it as a user turn |

> The data-plane commands resolve their dependencies (`IVectorStore`, `IngestionCommandService`, `DocumentContextHydrationService`) from `ConsoleChatSession.ServiceProvider` with `GetRequiredService`, so they only function when `Embedding:BaseUrl` is configured; otherwise those services are absent.

### `/dump-context`

`DumpContextCommand.Run` calls `ChatSession.PreviewContext()` and prints the system prompt, conversation messages (text, tool-calls, tool-results), and tools. Tools are grouped by origin — *Built-in* first, then each MCP server in configured order — by reconstructing provenance from `McpClientPool.ToolNamesByServer` (the flat registry and `ToolDefinition` carry none). Single-line descriptions render inline as `name: summary`; the progressive-discovery `{"type":"object"}` placeholder schema is suppressed. Display-only — it changes nothing sent to the model.

### MCP config resolution

When the `Mcp` section lists servers, `McpConfigResolver.Expand` substitutes `${RepoRoot}` (nearest ancestor containing `.git`) and `${Configuration}` (derived from the `bin/<cfg>/` output path) in each server's command, arguments, and environment variables before `McpClientPool.CreateAsync` connects. This keeps committed server paths portable across machines, drives, OSes, and build configurations. MCP is skipped entirely under `DOTNET_ENVIRONMENT=Test`.

### Model switching

`/model` lists all configured clients/models via `Models.GetAllAsync()`, presents them in a searchable picker grouped by client, and calls `ConsoleChatSession.SetAgent(newAgent)` to hot-swap the active `Agent` (and the live `ChatSession`'s agent) without restarting.

## Agent Tools

The `ToolContext` registered in DI includes built-in tools sourced from [[Agency.Harness]], plus discovered MCP tools and — when a vector store is configured — the semantic-search tool:

| Tool | Purpose |
|---|---|
| `ExecutePowershellTool` | Executes PowerShell commands and returns stdout/stderr |
| `ReadFileTool` | Reads file content from the local file system |
| `WriteFileTool` | Writes content to the local file system |
| `AgentTool` | Spawns a sub-`Agent` sharing the outward `ToolRegistry`, enabling recursive agent calls |
| `SkillTool` | Lets the model invoke skills; runs skill shell steps (unless disabled) and forks sub-agents for skill turns |
| `EnableGoalkeeperTool` | Arms the session `GoalState` with a verifiable done-condition; `LoopRunner` then evaluates the Goalkeeper after every turn |
| `DisableGoalkeeperTool` | Disarms the `GoalState`, stopping goal-driven looping after the current turn |
| `SemanticSearchTool` | Searches the ingested vector store, scoped to the session's user/session/loaded projects (defined in [[Agency.Harness]]) |

`SemanticSearchTool` is **registered here but defined in [[Agency.Harness]]**. The DI factory only adds it when an `IVectorStore` is resolvable (i.e. `Embedding:BaseUrl` is configured); it is constructed with the resolved `IVectorStore`, the scoped `IProjectSessionState`, and `RetrievalOptions.TopK`.

When MCP servers are configured their tools are folded into the same registry. When `Agent:ProgressiveDiscovery` is `true` (the default), the registry is wrapped in a `ProgressiveDiscoveryToolRegistry` that withholds **MCP** tool schemas behind a `tool_help` tool while revealing native/internal tools (including `SemanticSearchTool`) in full. `AgentTool` captures the *outward* registry so sub-agents share the same tool set and disclosure mode.

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
| [[Agency.Harness]] | Consumes `Agent`, `ChatSession`, `AgentEvent` subtypes (including the Loop Kit `GoalSetEvent`/`TurnStartedEvent`/`VerdictEvent`/`LoopResultEvent`), `AgentOptions`, `AgentHooks`, `Models`/`IAgentFactory` (via `AddAgencyAgent`), `ToolContext`/`ToolRegistry`/`ProgressiveDiscoveryToolRegistry`, `UserSpecificContext`/`SessionContext`/`KnowledgeContext`, `IProjectSessionState`, the `SemanticSearchTool`, `McpClientPool`/`McpClientOptions`, permission types, and built-in tools (`ExecutePowershellTool`, `ReadFileTool`, `WriteFileTool`, `AgentTool`, `SkillTool`) |
| [[Agency.Harness.Skills]] | Loads `ISkillCatalog`/`ReloadableSkillCatalog`, `SkillContext`, `SkillWatcher`, `SkillRenderer`; registers each user-invocable skill as a `/command` |
| [[Agency.Ingestion]] | `IngestionCommandService` drives a `DefaultIngestionPipeline` with `FileLoader`/`DirectoryLoader` and `ITextSplitter`/`IngestionResult` |
| [[Agency.Ingestion.SemanticKernel]] | Provides the `SemanticKernelTextSplitter` registered as `ITextSplitter` (chunk size/overlap from `IngestionOptions`) |
| [[Agency.VectorStore.Common]] | `IVectorStore`, `DocumentInfo`, `ListDocumentsAsync`/`ListProjectsAsync` — the store abstraction used by ingestion, hydration, and the project commands |
| [[Agency.VectorStore.Sql.Sqlite]] | `SqliteKVStore` (default provider) — registered and schema-initialised when `VectorStore:Provider` = `sqlite` |
| [[Agency.VectorStore.Sql.Postgres]] | `PostgresKVStore` — registered and schema-initialised when `VectorStore:Provider` = `postgres` |
| [[Agency.Llm.Common]] | `Models` enumerates configured LLM clients; the library's `AgentFactory` calls `Models.CreateChatClient` to resolve the `IChatClient`; binds `LlmClientOptions` |
| [[Agency.Llm.OpenAI]] | Instantiated by `Models.CreateChatClient` when `ClientType = "OpenAI"`; also used directly to build consolidator/distiller chat clients when memory is enabled |
| [[Agency.Llm.Claude]] | Instantiated by `Models.CreateChatClient` when `ClientType = "Claude"` |
| [[Agency.Embeddings.OpenAI]] | Registered (`AddAgencyEmbeddingsOpenAI`) whenever `Embedding:BaseUrl` is configured — drives both memory and the vector store |
| [[Agency.Memory.Sql.Postgres]] / [[Agency.Memory.Sql.Sqlite]] | One is registered as the memory store based on `Memory:Provider` |
| [[Agency.Memory.Distiller]] / [[Agency.Memory.Consolidator]] / [[Agency.Memory.Hygiene]] | Background memory services registered when memory is enabled |

## Design Notes

- **The data plane is gated on `Embedding:BaseUrl`, not `Memory:Enabled`** — ingestion and semantic search need only an embedding endpoint and a vector store; they do not require the conversational-memory subsystem (consolidator/distiller/hygiene). Gating embeddings + the vector store on `Embedding:BaseUrl` lets a user ingest documents and search them with memory turned off, while still sharing the one embedding client when memory *is* on. The option bindings (`VectorStoreOptions`/`IngestionOptions`/`RetrievalOptions`) and `IProjectSessionState` are registered unconditionally so configuration is always validated and session identity always exists.
- **Scope resolution auto-targets a single loaded project** — when exactly one project is loaded, `ScopeResolutionHelper.Resolve` skips the scope prompt and ingests straight into that project, matching the common single-project workflow; it only asks (Global / Session / project / new name) when the choice is genuinely ambiguous (zero or multiple loaded projects). This keeps the frequent case friction-free without hiding the choice when it matters.
- **Document inventory is injected as a Fact each turn, refreshed lazily** — `DocumentContextHydrationService` rebuilds the in-scope document list only when a command marks it dirty (an ingest or a project load/unload), caching the Fact otherwise. The REPL pushes it through `ChatSession.SetKnowledge` so the model always knows which documents `semantic_search` can reach, without re-querying the store on every turn.
- **Vector-store schema init is separate from memory schema init** — `Program` runs `InitializeSchemaAsync` on the resolved `SqliteKVStore`/`PostgresKVStore` (with `Embedding:Dimensions`) whenever `embeddingsConfigured`, independently of the memory schema initializer, so the data plane comes up even with `Memory:Enabled=false`.
- **`${RepoRoot}`/`${Configuration}` tokens for MCP portability** — committed `appsettings.json` must reference an MCP server binary whose absolute path differs per machine, drive, OS, and build configuration. Storing literal paths would break on every other checkout. `McpConfigResolver` resolves the repo root from the nearest `.git` ancestor and the configuration from the running `bin/<cfg>/` path, so the *same* committed config works everywhere a working tree exists.
- **MCP startup skipped under Test** — MCP servers are external processes absent from the functional-test/CI environment, and their discovered tools would be injected into the agent's tool list, changing the LLM request body and breaking offline HTTP-cache replay. The Test environment therefore never constructs `McpClientOptions`.
- **`FixedTimeProvider` for deterministic replay** — the agent's "Current date/time (UTC)" system-prompt line would otherwise vary per run, changing request bodies and the resulting HTTP-cache key. Under `DOTNET_ENVIRONMENT=Test` a frozen clock makes console turns byte-identical between local record and CI replay; production registers no `TimeProvider`, so the live clock is used.
- **Host-owned user identity via a placeholder hook** — the host, not the model, owns the user id. Tools (e.g. the memory MCP server) advertise a required `UserId`, and the model is instructed to pass the literal `{userId}`; `UserIdPlaceholderHook` rewrites it to the real GUID at `OnPreToolUse`. This makes it impossible for the model to fabricate or leak a wrong id, while remaining a no-op for tools that don't use the placeholder.
- **`Agent:UserId` is generated once and persisted** — `UserIdConfiguration.EnsureUserId` writes a new id back into `appsettings.json` and into the in-memory config for the current run, so memory partitions are stable across restarts without manual setup. Persistence is skipped under Test to keep the test config untouched and replay deterministic.
- **`MarkdownRenderer` is a line-based translator, not a CommonMark parser** — `Print` walks one line at a time matching prefix constructs (code fences, headings, blockquotes, lists, rules) and converting inline spans to Spectre markup. GFM pipe tables are the one *multi-line* construct: `TryParseTable` keys off the delimiter row (`| --- | :--: |`) following a header — keying on the delimiter, not the mere presence of `|`, stops ordinary prose containing a pipe from being mis-parsed. `BuildTable` emits a Spectre `Table`, normalising ragged rows to the header column count (Spectre throws when cell count ≠ column count). Unrecognised lines fall through to verbatim text.
- **`/dump-context` reconstructs tool grouping from the MCP pool** — `ToolDefinition` carries no origin and the registry is a flat name-keyed map, so the command resolves `McpClientPool` and uses `ToolNamesByServer` to bucket tools into *Built-in* vs each *server · MCP* group. It is display-only and reconstructs, for human readability, provenance the wire format discards.
- **Progressive-discovery wrap uses a deferred closure capture** — `AgentTool` is registered *during* population of the inner `ToolRegistry`, yet must hand sub-agents the *outward* registry (the progressive wrapper, decided only *after* population). The block declares `IToolRegistry outward = null!`, the `AgentTool`/`SkillTool` closures capture the **variable** `outward`, and it is assigned the final registry just before `ToolContext` is returned — long before any closure runs.
- **Two `IChatOutput` implementations** — `ConsoleOutput` (Spectre.Console, with a background spinner thread) drives the real terminal; `TextWriterChatOutput` writes plain text and is used by `Agency.Harness.Console.Test` to assert on rendered output without a terminal.
