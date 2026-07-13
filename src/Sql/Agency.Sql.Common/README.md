# Agency.Sql.Common

Abstract SQL runner base for the Agency AI Toolkit — shared query execution logic for SQL-backed providers.

## Install

```
dotnet add package AgencyDotNet.Sql.Common
```

## Types

- **`SqlRunnerBase`** — abstract base providing `ExecuteAsync` (non-query) and `QueryAsync<TResult>` (returns `Dataset`). Concrete subclasses supply the ADO.NET connection.

## Usage

Use a concrete implementation:

- [`AgencyDotNet.Sql.Postgres`](https://www.nuget.org/packages/AgencyDotNet.Sql.Postgres) — PostgreSQL
- [`AgencyDotNet.Sql.Sqlite`](https://www.nuget.org/packages/AgencyDotNet.Sql.Sqlite) — SQLite

Part of the [Agency AI Toolkit](https://github.com/emreaydinceren/Agency.NET) — an open-source .NET RAG pipeline.
