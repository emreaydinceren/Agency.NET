# Agency.VectorStore.Common

Vector store abstractions for the Agency AI Toolkit — store and search embeddings across providers.

## Install

```
dotnet add package AgencyDotNet.VectorStore.Common
```

## Types

- **`IVectorStore`** — `UpsertAsync`, `SearchAsync<TValue>`, `DeleteAsync`.
- **`Query`** — configures a similarity search: `UserId`, `SessionId`, `Value` (the query embedding), `Key`, `MetadataFilter`, `Limit`.
- **`SearchHit<TValue>`** — result item with `Key`, `Value`, `Metadata`, `Distance`, and `UpdatedOn`.

## Usage

Use a concrete implementation:

- [`AgencyDotNet.VectorStore.Sql.Postgres`](https://www.nuget.org/packages/AgencyDotNet.VectorStore.Sql.Postgres) — PostgreSQL + pgvector
- [`AgencyDotNet.VectorStore.Sql.Sqlite`](https://www.nuget.org/packages/AgencyDotNet.VectorStore.Sql.Sqlite) — SQLite (cosine via UDF)

Part of the [Agency AI Toolkit](https://github.com/emreaydinceren/Agency.NET) — an open-source .NET RAG pipeline.
