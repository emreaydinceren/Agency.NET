# Agency.Mcp.Memory

MCP (Model Context Protocol) memory server for the Agency AI Toolkit — exposes `memorize`, `recall`, and `forget` tools over `IKVStore`.

## Install

```
dotnet add package Agency.Mcp.Memory
```

## Types

- **`MemoryTool`** — MCP server tool with three operations: `memorize` (store), `recall` (search by key/scope), `forget` (delete).
- **`MemoryRecord`** — stored memory item with content and metadata.
- **`MemoryScope`** — partitions memories by user or session.

## Usage

```csharp
services.AddScoped<IKVStore, SqliteKVStore>(_ =>
    new SqliteKVStore("Data Source=memory.sqlite"));

services.AddMemory(); // registers MemoryTool with IKVStore

// The MCP server exposes:
// - memorize(scope, key, content)
// - recall(scope, query)
// - forget(scope, key)
```

Part of the [Agency AI Toolkit](https://github.com/emre/Agency) — an open-source .NET agentic AI toolkit.
