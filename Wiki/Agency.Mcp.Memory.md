# Agency.Mcp.Memory

#mcp #memory #keyvaluestore #server

## What It Is

`Agency.Mcp.Memory` is a standalone **Model Context Protocol (MCP) server** that exposes four tools: `Memorize`, `Recall`, `Forget`, and `ListGlobalKeys`. It runs over stdio transport and resolves its backing store from configuration (`sqlite` or `postgres`) via [[Agency.KeyValueStore.Common]] `IKVStore` implementations.

## Key Types

### `MemoryTool`

The MCP tool class decorated with `[McpServerToolType]`. Its methods are exposed as MCP tools via `[McpServerTool]`.

```csharp
var scope = new MemoryScope { UserId = "user-1", SessionId = "session-42" };

// Store a value under domain|key with metadata (domain/key/tags)
string memorize = await tool.Memorize(new MemoryRecord
{
    Scope = scope,
    Domain = "preferences",
    Key = "theme",
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

// List distinct keys/tags in the user's global session (sessionId="*")
string globalIndex = await tool.ListGlobalKeys("user-1");
```

`Memorize` validates `Scope`, `Domain`, `Key`, and `Value` and writes entries using a storage key in `{domain}|{key}` format. Scope is applied through `IKVStore` partitioning (`userId` + `sessionId`).

### `MemoryRecord`

Input record for `Memorize`:

```csharp
public record class MemoryRecord
{
    public MemoryScope? Scope { get; set; }
    public string? Domain    { get; set; }
    public string? Key       { get; set; }
    public string? Value     { get; set; }
    public string[]? Tags    { get; set; }
}
```

### `MemoryScope`

Identifies the owner of a memory entry:

```csharp
public record class MemoryScope
{
    public string UserId    { get; set; } = string.Empty;  // empty = global scope
    public string SessionId { get; set; } = string.Empty;  // empty = user-wide scope
}
```

### `MemoryOptions`

Configuration bound from `appsettings.json` section `"Memory"`:

| Property | Values | Description |
|---|---|---|
| `Provider` | `"sqlite"` or `"postgres"` | Selects the backing store |
| `ConnectionString` | provider-specific | Connection string for the selected store |

### `MemorySchemaInitializer`

A hosted service (`IHostedService`) that calls `SqliteKVStore.InitializeSchemaAsync()` on startup when the provider is SQLite.

### `MemoryServiceCollectionExtensions`

Provides `AddKVStore(IServiceCollection)` to register:

- `PostgreKVStore` using `PostgreSqlRunner` and configured connection string
- `SqliteKVStore` using `SqliteRunner` and configured connection string
- `IKVStore` selector based on `MemoryOptions.Provider`
- `MemorySchemaInitializer` hosted service

### `Program`

Builds the host, binds `MemoryOptions` from the `Memory` configuration section, configures console logging to stderr (`LogToStandardErrorThreshold = Trace`), registers MCP stdio transport with tools from the assembly, and wires `MemoryTool` plus `IKVStore` services.

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
| [[Agency.KeyValueStore.Common]] | `MemoryTool` depends on `IKVStore` (`UpsertAsync`, `SearchAsync`, `DeleteAsync`) |
| [[Agency.KeyValueStore.Sql.Postgre]] | Provides `PostgreKVStore` when `Provider = "postgres"` |
| [[Agency.KeyValueStore.Sql.Sqlite]] | Provides `SqliteKVStore` when `Provider = "sqlite"` |
| [[Agency.Sql.Postgre]] | Provides `PostgreSqlRunner` used to construct `PostgreKVStore` |
| [[Agency.Sql.Sqlite]] | Provides `SqliteRunner` used to construct `SqliteKVStore` |
