# Agency.Mcp.Memory

#mcp #memory #vectorstore #server

## What It Is

`Agency.Mcp.Memory` is a standalone **Model Context Protocol (MCP) server** that exposes three tools — `Memorize`, `Recall`, and `Forget` — backed by an [[Agency.VectorStore.Common]] `IKVStore`. It runs as a stdio-transport MCP process and can be wired to any MCP-compatible client (e.g. Claude Desktop). The backing store is configurable at startup: either SQLite (local/embedded) or PostgreSQL (production).

## Key Types

### `MemoryTool`

The MCP tool class decorated with `[McpServerToolType]`. All three methods are exposed as MCP tools via `[McpServerTool]`.

```csharp
// Store a piece of information
string result = await tool.Memorize(new MemoryRecord
{
    Scope = new MemoryScope { UserId = "user-1", SessionId = "session-42" },
    Domain = "preferences",
    Key = "theme",
    Value = "dark",
    Tags = ["ui"]
});
// → "Memorized: user-1|session-42|preferences|theme"

// Recall with metadata filter
string json = await tool.Recall(
    scope: new MemoryScope { UserId = "user-1", SessionId = "session-42" },
    domain: "preferences",
    key: null,
    tags: ["ui"]);

// Delete a specific entry
string msg = await tool.Forget(scope, "preferences", "theme");
// → "Removed: user-1|session-42|preferences|theme"
```

Storage keys follow the composite format `{userId}|{sessionId}|{domain}|{key}`, giving each user+session pair its own namespace inside a single shared table.

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

A hosted service (`IHostedService`) that calls `SqliteKVStore.InitializeSchemaAsync()` on startup when the provider is SQLite. This guarantees the `semantic_kv_store` table exists before the first tool call.

### `RandomEmbeddingGenerator`

A fallback `IEmbeddingGenerator` registered internally. Produces deterministic pseudo-random vectors seeded from the input string's hash code, so metadata-only recall works without a real embedding API. Similarity-ranked semantic search requires a real embedding provider to be substituted.

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
| [[Agency.VectorStore.Common]] | `MemoryTool` takes `IKVStore` and calls `UpsertAsync`, `SearchAsync`, `DeleteAsync` |
| [[Agency.VectorStore.Sql.Postgre]] | Concrete `IKVStore` when `Provider = "postgres"` |
| [[Agency.VectorStore.Sql.Sqlite]] | Concrete `IKVStore` when `Provider = "sqlite"` |
| [[Agency.Sql.Postgre]] | `PostgreSqlRunner` used indirectly via `PostgreKVStore` |
| [[Agency.Sql.Sqlite]] | `SqliteRunner` used indirectly via `SqliteKVStore` |
| [[Agency.Embeddings.Common]] | Implements `IEmbeddingGenerator`; `RandomEmbeddingGenerator` is the fallback |
