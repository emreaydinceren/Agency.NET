# Agency.Memory.Sql.Postgres

PostgreSQL + pgvector implementation of `IMemoryStore` for the Agency long-term memory system.

## Install

```
dotnet add package AgencyDotNet.Memory.Sql.Postgres
```

## Types

- **`PostgresMemoryStore`** — `IMemoryStore` backed by PostgreSQL with pgvector for semantic similarity search.
- **`SchemaInitializer`** — creates the memory table and vector index on first run.

## Usage

```csharp
services.AddPostgresMemoryStore(connectionString);
// Requires PostgreSQL 15+ with the pgvector extension enabled.
```

Part of the [Agency AI Toolkit](https://github.com/emreaydinceren/Agency.NET) — an open-source .NET agentic AI toolkit.
