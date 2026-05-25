# Agency.KeyValueStore.Common

Key-value store abstractions for the Agency AI Toolkit — structured text storage without vector search.

## Install

```
dotnet add package Agency.KeyValueStore.Common
```

## Types

- **`IKVStore`** — `UpsertAsync`, `SearchAsync<TValue>`, `DeleteAsync`, `GetMetadataAsync`.
- **`Query`** — filters by substring key match, exact key, or metadata.
- **`SearchHit<TValue>`** — result item with `Key`, `Value`, `Metadata`, and `UpdatedOn`.

## Usage

Use a concrete implementation:

- [`Agency.KeyValueStore.Sql.Postgres`](https://www.nuget.org/packages/Agency.KeyValueStore.Sql.Postgres) — PostgreSQL (ILIKE search)
- [`Agency.KeyValueStore.Sql.Sqlite`](https://www.nuget.org/packages/Agency.KeyValueStore.Sql.Sqlite) — SQLite (instr search)

Part of the [Agency AI Toolkit](https://github.com/emre/Agency) — an open-source .NET agentic AI toolkit.
