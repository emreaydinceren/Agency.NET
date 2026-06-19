# Agency.Mcp.Memory

#mcp #memory #keyvaluestore #server

## What It Is

`Agency.Mcp.Memory` is the standalone MCP server executable that exposes scoped memory operations to LLM agents over stdio transport, backed by a pluggable [[Agency.KeyValueStore.Common]] `IKVStore` implementation (SQLite or PostgreSQL).

**Namespace:** `Agency.Mcp.Memory`

## Prerequisites

- A running SQLite file path or PostgreSQL instance.
- `appsettings.json` (or equivalent) with a `"Memory"` section providing `Provider` and `ConnectionString`.

## API Surface

### `MemoryTool`

MCP tool class decorated with `[McpServerToolType]`. Receives an `IKVStore` via constructor injection and exposes four MCP tools. The class-level `[Description]` attribute is populated from the internal `ToolDescription.Text` constant, which provides the LLM with a full usage guide for the scoped memory model. Every tool parameter is optional (`= default`) at the C# level; the tools themselves validate that the required values (`scope`, `scope.UserId`, `domain`, `key`, `value`) are present and return a corrective error string otherwise.

```csharp
// File: src/Mcp/Agency.Mcp.Memory/MemoryTool.cs
using Agency.KeyValueStore.Common;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Agency.Mcp.Memory;

[McpServerToolType, Description(ToolDescription.Text)]
public class MemoryTool(IKVStore kvStore)
{
    [McpServerTool, Description("Saves a piece of information to long-term memory ...")]
    public Task<string> Memorize(MemoryRecord? record = null);

    [McpServerTool, Description("Permanently deletes a single memorized entry ...")]
    public Task<string> Forget(MemoryScope? scope = null, string? domain = null, string? key = null);

    [McpServerTool, Description("Lists the distinct Domains, Keys, and Tags ...")]
    public Task<string> ListGlobalKeys(MemoryScope? scope = null);

    [McpServerTool, Description("Retrieves previously memorized information ...")]
    public Task<string> Recall(MemoryScope? scope = null, string? domain = null, string? key = null, string[]? tags = null);
}
```

### `MemoryRecord`

Input record for the `Memorize` tool.

```csharp
// File: src/Mcp/Agency.Mcp.Memory/MemoryRecord.cs
namespace Agency.Mcp.Memory;

public record class MemoryRecord
{
    public MemoryScope? Scope   { get; set; }
    public string?      Key     { get; set; }
    public string?      Domain  { get; set; }
    public string?      Value   { get; set; }
    public string[]?    Tags    { get; set; }
}
```

### `MemoryScope`

Identifies the owner of a memory entry. `SessionId` is nullable; `null` denotes a user-wide (global) scope.

```csharp
// File: src/Mcp/Agency.Mcp.Memory/MemoryScope.cs
namespace Agency.Mcp.Memory;

public record class MemoryScope(string UserId, string? SessionId);
```

### `MemoryOptions`

Bound from the `"Memory"` configuration section.

```csharp
// File: src/Mcp/Agency.Mcp.Memory/MemoryOptions.cs
namespace Agency.Mcp.Memory;

public class MemoryOptions
{
    public string Provider         { get; set; } = string.Empty;   // "sqlite" or "postgres"
    public string ConnectionString { get; set; } = string.Empty;
}
```

### `MemoryServiceCollectionExtensions`

DI registration helper used by `Program`.

```csharp
// File: src/Mcp/Agency.Mcp.Memory/MemoryServiceCollectionExtensions.cs
using Agency.KeyValueStore.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Agency.Mcp.Memory;

public static class MemoryServiceCollectionExtensions
{
  // Registers PostgresKVStore, SqliteKVStore, IKVStore selector, and MemorySchemaInitializer.
    // Throws InvalidOperationException for unsupported provider values.
    public static IServiceCollection AddKVStore(this IServiceCollection services);
}
```

## Registration

`Program.cs` wires the full server:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agency.Mcp.Memory;

System.Console.OutputEncoding = System.Text.Encoding.UTF8;
System.Console.InputEncoding  = System.Text.Encoding.UTF8;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<MemoryOptions>(builder.Configuration.GetSection("Memory"));

builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

builder.Services.AddKVStore();
builder.Services.AddSingleton<MemoryTool>();

await builder.Build().RunAsync();
```

`appsettings.json` configuration:

```json
{
  "Memory": {
    "Provider": "sqlite",
    "ConnectionString": "Data Source=memory.db"
  }
}
```

For PostgreSQL:

```json
{
  "Memory": {
    "Provider": "postgres",
    "ConnectionString": "Host=localhost;Database=dev_db;Username=dev_user;Password=dev_password"
  }
}
```

## How It Works

1. On startup, `Program` sets both `Console.OutputEncoding` and `Console.InputEncoding` to UTF-8, then builds a .NET Generic Host, binds `MemoryOptions` from the `"Memory"` config section, and registers the MCP stdio transport with `WithToolsFromAssembly()`.
2. `AddKVStore()` registers both `PostgresKVStore` and `SqliteKVStore` as singletons, then resolves `IKVStore` by switching on `MemoryOptions.Provider`. An unsupported provider value throws `InvalidOperationException` at resolution time.
3. `MemorySchemaInitializer` (an `internal sealed` hosted service) calls `SqliteKVStore.InitializeSchemaAsync()` inside `StartAsync` when the provider is `sqlite`; PostgreSQL requires a pre-existing schema and is skipped.
4. The MCP framework discovers `MemoryTool` via `[McpServerToolType]` and exposes its four `[McpServerTool]` methods over stdio. Each tool parameter declares `= default`, so the MCP schema treats them as optional; the tools enforce real requirements at runtime via `ValidateScope` and per-field checks, returning corrective error strings (each with a worked-example payload) rather than throwing.
5. All memory entries are stored in `IKVStore` under a composite key `{domain}|{key}`, with a metadata dictionary carrying `domain`, `key`, and optional `tags`. Scope partitioning (`UserId` + `SessionId`) is enforced by the KV store.
6. The class-level `[Description(ToolDescription.Text)]` attribute injects a structured usage guide directly into the MCP tool descriptor, and each tool/parameter carries its own `[Description]` (including `MemoryRecord`/`MemoryScope` property descriptions). Together these tell the calling LLM the scope semantics, storage model, and the literal `"{userId}"` placeholder convention without requiring external documentation.

## Agent Tools

All tools are exposed on `MemoryTool` and discovered automatically via `WithToolsFromAssembly()`. The descriptions below are the exact `[McpServerTool]` strings shown to the LLM (abridged in the table; full text in source). Every tool first runs `ValidateScope`, returning a corrective error string (with a worked-example payload) when `scope` is missing or `scope.UserId` is blank — this guards against an LLM calling with empty `{}` arguments.

| Tool Name (exact string) | Description shown to LLM | Key Parameters |
|---|---|---|
| `Memorize` | "Saves a piece of information to long-term memory so it can be recalled in later turns or future sessions. Use this whenever the user shares a durable fact ... The single required argument is a nested OBJECT named record containing Scope (UserId = literal placeholder `"{userId}"`), Domain, Key, and Value; optionally Tags. Returns the composite storage key on success." | `record` (`MemoryRecord?`, optional in C# but required at runtime — must carry `Scope.UserId`, `Domain`, `Key`, `Value`; optional `Tags`) |
| `Recall` | "Retrieves previously memorized information about the user from long-term memory. ... scope is a nested OBJECT, not a string, and is the only required argument — do NOT call with empty arguments `{}`. ... Provide both Domain and Key for an exact lookup, Domain alone to list one category, or Tags to filter by label. Returns a JSON array of matching entries (empty array when nothing matches)." | `scope` (`MemoryScope?`, required at runtime), `domain` (optional), `key` (optional), `tags` (optional) |
| `Forget` | "Permanently deletes a single memorized entry identified by its Domain and Key within the given Scope. Use this when the user asks you to forget a specific fact. scope is a nested OBJECT ... Returns whether the entry was removed or was not found." | `scope` (`MemoryScope?`, required at runtime), `domain` (required at runtime), `key` (required at runtime) |
| `ListGlobalKeys` | "Lists the distinct Domains, Keys, and Tags already stored for the user across the global (user-wide) session. Call this first for broad discovery ... scope is a nested OBJECT ... and is the only required argument — do NOT call with empty arguments `{}`. Returns JSON grouped by domain." | `scope` (`MemoryScope?`, required at runtime) |

### `Memorize`

**Returns:** `"Memorized: {domain}|{key}"` on success, or a corrective error string (with a worked-example payload) if `record` is null or any required field (`Scope`, `Scope.UserId`, `Domain`, `Key`, `Value`) is missing.

**Behavior:** Runs `ValidateScope`, then validates the remaining required fields, then calls `IKVStore.UpsertAsync(userId, sessionId, "{domain}|{key}", value, metadata)` with metadata `{ domain, key, tags? }` (`tags` added only when non-empty).

### `Recall`

**Returns:** JSON-serialized array of `SearchHit<string>` results.

**Behavior:**
- Runs `ValidateScope` first; passing only a valid `scope` recalls everything known about the user.
- If both `domain` and `key` are provided: exact composite key lookup (`{domain}|{key}`).
- If only `domain` is provided: metadata filter on `domain`.
- If `tags` are provided: metadata filter requiring all specified tags.
- Delegates to `IKVStore.SearchAsync<string>` with a `Query` capped at 10 results and `includeValues: true`.

### `Forget`

**Returns:** `"Removed: {domain}|{key}"` if the entry existed, `"Not found: {domain}|{key}"` otherwise.

**Behavior:** Calls `IKVStore.DeleteAsync` with composite key `{domain}|{key}`.

### `ListGlobalKeys`

**Returns:** JSON object keyed by domain; each domain entry contains `Keys` (distinct string set) and `Tags` (distinct string set).

**Behavior:** Calls `IKVStore.GetMetadataAsync`, iterates all `SearchHit` results, and aggregates `key` and `tags` metadata per domain. Handles `string[]`, `IEnumerable<object>`, and `JsonElement` array representations of tags from deserialized metadata.

## Observability

This project does not define a custom `ActivitySource` or `Meter`. All diagnostics are emitted through the standard .NET `ILogger` pipeline. Console logs are routed to stderr (`LogToStandardErrorThreshold = LogLevel.Trace`) to avoid contaminating the MCP stdio transport on stdout.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.KeyValueStore.Common]] | `MemoryTool` depends on `IKVStore` (`UpsertAsync`, `SearchAsync`, `DeleteAsync`, `GetMetadataAsync`) and the `Query`, `SearchHit`, and `SearchHit<T>` types |
| [[Agency.KeyValueStore.Sql.Postgres]] | Provides `PostgresKVStore` when `Provider = "postgres"` |
| [[Agency.KeyValueStore.Sql.Sqlite]] | Provides `SqliteKVStore` when `Provider = "sqlite"`; schema is auto-initialized on startup |
| [[Agency.Sql.Postgres]] | Provides `PostgreSqlRunner` used to construct `PostgresKVStore` |
| [[Agency.Sql.Sqlite]] | Provides `SqliteRunner` used to construct `SqliteKVStore` |

## Design Notes

- Every tool parameter is declared optional with `= default`. `AIFunctionFactory` (used by `WithToolsFromAssembly()`) marks a parameter required unless it has a default value — nullability alone is ignored — so without the defaults the LLM would be forced to supply every argument. The tools instead validate at runtime and return guidance, which lets the LLM call `Recall`/`ListGlobalKeys` with only a `scope`.
- Scope is partitioned by `UserId` (required logical owner) and an optional `SessionId`. `UserId` is passed as the literal placeholder `"{userId}"` — the host substitutes the real id so the LLM never invents or leaks identities. A null `SessionId` denotes user-wide (global) memory; a session id partitions task/conversation-local memory.
- The composite storage key `{domain}|{key}` encodes both dimensions into a single string, allowing the underlying KV store to remain unaware of domain semantics while still enabling exact-match retrieval without a metadata scan.
- `SessionId = null` represents a user-wide (global) scope; `ListGlobalKeys` explicitly targets this scope to provide a cross-session index, letting agents discover what is persisted before issuing targeted `Recall` calls.
- `MemorySchemaInitializer` is `internal sealed` and runs schema creation inside `StartAsync` so the SQLite table is guaranteed to exist before the first MCP tool call; PostgreSQL requires an externally managed schema and does not participate in this step.
- The `ToolDescription.Text` constant embeds a structured LLM usage guide directly in the `[Description]` attribute so the MCP framework propagates it to clients without any out-of-band documentation; this keeps the tool self-describing at the protocol level.
- Both `Console.OutputEncoding` and `Console.InputEncoding` are forced to UTF-8 at startup to ensure correct JSON-RPC framing over stdio on all host platforms, particularly Windows where the default console encoding may otherwise corrupt multi-byte characters.
