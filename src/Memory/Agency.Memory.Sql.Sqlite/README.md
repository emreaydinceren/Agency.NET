# Agency.Memory.Sql.Sqlite

SQLite implementation of `IMemoryStore` for the Agency long-term memory system.
Embedded, zero-server, with an in-process cosine UDF for similarity search.

## Install

```
dotnet add package Agency.Memory.Sql.Sqlite
```

## Types

- **`SqliteMemoryStore`** — `IMemoryStore` backed by SQLite with an in-process cosine UDF for semantic similarity search.
- **`MemorySchemaInitializer`** — creates the memory tables and indexes on first run (idempotent).
- **`SqliteWatermarkRepository`** — persists per-session distillation watermarks.
- **`SqliteDeadLetterRepository`** — stores failed jobs for operational inspection.

## Usage

```csharp
services.AddAgencyMemorySqlite("Data Source=memory.db");
// No external server required — SQLite runs in-process.
```

Part of the [Agency AI Toolkit](https://github.com/emreaydinceren/Agency.NET) — an open-source .NET agentic AI toolkit.