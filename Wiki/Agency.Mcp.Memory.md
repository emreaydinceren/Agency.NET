# Agency.Mcp.Memory

#mcp #memory #keyvaluestore #server

## What It Is

`Agency.Mcp.Memory` is a standalone **Model Context Protocol (MCP) server** that exposes four tools: `Memorize`, `Recall`, `Forget`, and `ListGlobalKeys`. It runs over stdio transport and resolves its backing store from configuration (`sqlite` or `postgres`) via [[Agency.KeyValueStore.Common]] `IKVStore` implementations.

## Key Types

### `MemoryTool`

The MCP tool class decorated with `[McpServerToolType]`. Its methods are exposed as MCP tools via `[McpServerTool]`.

```csharp
var scope = new MemoryScope("user-1", "session-42");

// Store a value under domain|key with metadata (domain/key/tags)
string memorize = await tool.Memorize(new MemoryRecord
{
    Scope = scope,
    Key = "theme",
    Domain = "preferences",
    Value = "light",
    Tags = ["ui"]
});
// -> "Memorized: preferences|theme"

// Recall from the same scope, filtered by domain and tags
string recall = await tool.Recall(
    scope,
    domain: "preferences",
    key: null,
    tags: ["ui"]);

// Delete a specific entry
string forget = await tool.Forget(scope, "preferences", "theme");
// -> "Removed: preferences|theme"

// List distinct keys/tags in the user's global session
string globalIndex = await tool.ListGlobalKeys(new MemoryScope("user-1", null));
```

`Memorize` validates `Scope`, `Domain`, `Key`, and `Value` and writes entries using a storage key in `{domain}|{key}` format. Scope is applied through `IKVStore` partitioning (`userId` + `sessionId`).

`Recall` supports three lookup modes:
- Both `domain` and `key` provided: exact composite key match.
- `domain` only: filters by domain metadata.
- `tags` only: filters by tag metadata (entries must contain all specified tags).

`ListGlobalKeys` reads all metadata entries for a scope and returns a JSON object grouped by domain, containing distinct `Keys` and `Tags` arrays.

### `MemoryRecord`

Input record for `Memorize`:

```csharp
public record class MemoryRecord
{
    public MemoryScope? Scope { set; get; }
    public string? Key    { set; get; }
    public string? Domain { set; get; }
    public string? Value  { set; get; }
    public string[]? Tags { set; get; }
}
```

### `MemoryScope`

Identifies the owner of a memory entry via primary constructor:

```csharp
public record class MemoryScope(string UserId, string? SessionId)
{
}
```

`SessionId` is nullable: `null` denotes a user-wide (global) scope.

### `ToolDescription`

An internal static class that holds the multi-line tool description text shown to the LLM in the MCP tool metadata. It documents the four core concepts (Scope, UserId, SessionId, Domain, Key, Tags), the storage model (`{domain}|{key}` composite key), and guidance for LLM tool calls.

```csharp
internal static class ToolDescription
{
    internal const string Text = """
    Use MemoryTool as a scoped memory system with four core concepts:
    • Scope: ownership boundary for data.
    • UserId: required logical owner.
    • SessionId: optional conversation/task partition under that user.
    • Domain: high-level category of memory (e.g., Work, Home, Health).
    • Key: item identifier within a domain (e.g., Address, ExpensePolicy).
    • Tags: optional multi-label metadata for retrieval/filtering (e.g., ["taxes","family"]).
    ...
    """;
}
```

### `MemoryOptions`

Configuration bound from `appsettings.json` section `"Memory"`:

| Property | Values | Description |
|---|---|---|
| `Provider` | `"sqlite"` or `"postgres"` | Selects the backing store |
| `ConnectionString` | provider-specific | Connection string for the selected store |

### `MemorySchemaInitializer`

An internal hosted service (`IHostedService`) that calls `SqliteKVStore.InitializeSchemaAsync()` on startup when the provider is SQLite. Runs synchronously inside `StartAsync` so the schema exists before the first tool call.

### `MemoryServiceCollectionExtensions`

Provides `AddKVStore(IServiceCollection)` to register:

- `PostgreKVStore` using `PostgreSqlRunner` and configured connection string
- `SqliteKVStore` using `SqliteRunner` and configured connection string
- `IKVStore` selector based on `MemoryOptions.Provider`
- `MemorySchemaInitializer` hosted service

### `Program`

Builds the host, binds `MemoryOptions` from the `Memory` configuration section, configures console logging to stderr (`LogToStandardErrorThreshold = LogLevel.Trace`), registers MCP stdio transport with tools from the assembly (`AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly()`), and wires `MemoryTool` plus `IKVStore` services.

This project does not define a custom `ActivitySource` or `Meter`; observability is currently provided through standard host logging.

## Configuration

`appsettings.json` (or environment variables / user-secrets):

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

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.KeyValueStore.Common]] | `MemoryTool` depends on `IKVStore` (`UpsertAsync`, `SearchAsync`, `DeleteAsync`, `GetMetadataAsync`) |
| [[Agency.KeyValueStore.Sql.Postgre]] | Provides `PostgreKVStore` when `Provider = "postgres"` |
| [[Agency.KeyValueStore.Sql.Sqlite]] | Provides `SqliteKVStore` when `Provider = "sqlite"` |
| [[Agency.Sql.Postgre]] | Provides `PostgreSqlRunner` used to construct `PostgreKVStore` |
| [[Agency.Sql.Sqlite]] | Provides `SqliteRunner` used to construct `SqliteKVStore` |
