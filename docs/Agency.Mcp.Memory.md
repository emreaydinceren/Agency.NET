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

MCP tool class decorated with `[McpServerToolType]`. Receives an `IKVStore` via constructor injection and exposes four MCP tools. The class-level `[Description]` attribute is populated from the internal `ToolDescription.Text` constant, which provides the LLM with a full usage guide for the scoped memory model.

```csharp
// File: src/Mcp/Agency.Mcp.Memory/MemoryTool.cs
using Agency.KeyValueStore.Common;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Agency.Mcp.Memory;

[McpServerToolType, Description(ToolDescription.Text)]
public class MemoryTool(IKVStore kvStore)
{
    [McpServerTool, Description("Memorizes a provided piece of information.")]
    public Task<string> Memorize(MemoryRecord record);

    [McpServerTool, Description("Deletes a memorized piece of information.")]
    public Task<string> Forget(MemoryScope scope, string domain, string key);

    [McpServerTool, Description("Lists distinct keys and tags stored in the global (user-wide) session for a given user.")]
    public Task<string> ListGlobalKeys(MemoryScope memoryScope);

    [McpServerTool, Description("Recalls the memorized piece of information based on filter parameters.")]
    public Task<string> Recall(MemoryScope scope, string? domain, string? key, string[]? tags);
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
    // Registers PostgreKVStore, SqliteKVStore, IKVStore selector, and MemorySchemaInitializer.
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
2. `AddKVStore()` registers both `PostgreKVStore` and `SqliteKVStore` as singletons, then resolves `IKVStore` by switching on `MemoryOptions.Provider`. An unsupported provider value throws `InvalidOperationException` at resolution time.
3. `MemorySchemaInitializer` (an `internal sealed` hosted service) calls `SqliteKVStore.InitializeSchemaAsync()` inside `StartAsync` when the provider is `sqlite`; PostgreSQL requires a pre-existing schema and is skipped.
4. The MCP framework discovers `MemoryTool` via `[McpServerToolType]` and exposes its four `[McpServerTool]` methods over stdio.
5. All memory entries are stored in `IKVStore` under a composite key `{domain}|{key}`, with a metadata dictionary carrying `domain`, `key`, and optional `tags`. Scope partitioning (`UserId` + `SessionId`) is enforced by the KV store.
6. The class-level `[Description(ToolDescription.Text)]` attribute injects a structured usage guide directly into the MCP tool descriptor, informing the calling LLM about scope semantics, storage model, and tool behaviors without requiring external documentation.

## Agent Tools

All tools are exposed on `MemoryTool` and discovered automatically via `WithToolsFromAssembly()`.

| Tool Name | Description | Key Parameters |
|---|---|---|
| `Memorize` | Stores a piece of information under a composite `{domain}\|{key}`. | `record` (`MemoryRecord` with `Scope`, `Domain`, `Key`, `Value`; optional `Tags`) |
| `Recall` | Searches memory by scope, domain, key, and/or tags. | `scope`, `domain?`, `key?`, `tags?` |
| `Forget` | Deletes the entry identified by `{domain}\|{key}`. | `scope`, `domain`, `key` |
| `ListGlobalKeys` | Lists all distinct keys and tags grouped by domain for a given scope. | `memoryScope` |

### `Memorize`

**Returns:** `"Memorized: {domain}|{key}"` on success, or an error string if any required field (`Scope`, `Domain`, `Key`, `Value`) is missing.

**Behavior:** Validates all required fields, then calls `IKVStore.UpsertAsync` with composite key `{domain}|{key}` and metadata `{ domain, key, tags? }`.

### `Recall`

**Returns:** JSON-serialized array of `SearchHit<string>` results.

**Behavior:**
- If both `domain` and `key` are provided: exact composite key lookup (`{domain}|{key}`).
- If only `domain` is provided: metadata filter on `domain`.
- If `tags` are provided: metadata filter requiring all specified tags.
- Delegates to `IKVStore.SearchAsync<string>` with a `Query` capped at 10 results.

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
| [[Agency.KeyValueStore.Sql.Postgre]] | Provides `PostgreKVStore` when `Provider = "postgres"` |
| [[Agency.KeyValueStore.Sql.Sqlite]] | Provides `SqliteKVStore` when `Provider = "sqlite"`; schema is auto-initialized on startup |
| [[Agency.Sql.Postgre]] | Provides `PostgreSqlRunner` used to construct `PostgreKVStore` |
| [[Agency.Sql.Sqlite]] | Provides `SqliteRunner` used to construct `SqliteKVStore` |

## Design Notes

- The composite storage key `{domain}|{key}` encodes both dimensions into a single string, allowing the underlying KV store to remain unaware of domain semantics while still enabling exact-match retrieval without a metadata scan.
- `SessionId = null` represents a user-wide (global) scope; `ListGlobalKeys` explicitly targets this scope to provide a cross-session index, letting agents discover what is persisted before issuing targeted `Recall` calls.
- `MemorySchemaInitializer` is `internal sealed` and runs schema creation inside `StartAsync` so the SQLite table is guaranteed to exist before the first MCP tool call; PostgreSQL requires an externally managed schema and does not participate in this step.
- The `ToolDescription.Text` constant embeds a structured LLM usage guide directly in the `[Description]` attribute so the MCP framework propagates it to clients without any out-of-band documentation; this keeps the tool self-describing at the protocol level.
- Both `Console.OutputEncoding` and `Console.InputEncoding` are forced to UTF-8 at startup to ensure correct JSON-RPC framing over stdio on all host platforms, particularly Windows where the default console encoding may otherwise corrupt multi-byte characters.
