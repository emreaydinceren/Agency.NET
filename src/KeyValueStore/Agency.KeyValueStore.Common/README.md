# Agency.KeyValueStore.Common

Key-value store abstractions for the Agency AI Toolkit — structured text storage without vector search.

## Install

```
dotnet add package AgencyDotNet.KeyValueStore.Common
```

## Types

- **`IKVStore`** — `UpsertAsync`, `SearchAsync<TValue>`, `DeleteAsync`, `GetMetadataAsync`.
- **`Query`** — filters by substring key match, exact key, or metadata.
- **`SearchHit<TValue>`** — result item with `Key`, `Value`, `Metadata`, and `UpdatedOn`.

## Usage

Use a concrete implementation:

- [`AgencyDotNet.KeyValueStore.Sql.Postgres`](https://www.nuget.org/packages/AgencyDotNet.KeyValueStore.Sql.Postgres) — PostgreSQL (ILIKE search)
- [`AgencyDotNet.KeyValueStore.Sql.Sqlite`](https://www.nuget.org/packages/AgencyDotNet.KeyValueStore.Sql.Sqlite) — SQLite (instr search)

Part of the [Agency AI Toolkit](https://github.com/emreaydinceren/Agency.NET) — an open-source .NET agentic AI toolkit.
