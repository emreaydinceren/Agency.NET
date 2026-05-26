# Agency.Sql.Common

Abstract SQL runner base for the Agency AI Toolkit — shared query execution logic for SQL-backed providers.

## Install

```
dotnet add package Agency.Sql.Common
```

## Types

- **`SqlRunnerBase`** — abstract base providing `ExecuteAsync` (non-query) and `QueryAsync<TResult>` (returns `Dataset`). Concrete subclasses supply the ADO.NET connection.

## Usage

Use a concrete implementation:

- [`Agency.Sql.Postgres`](https://www.nuget.org/packages/Agency.Sql.Postgres) — PostgreSQL
- [`Agency.Sql.Sqlite`](https://www.nuget.org/packages/Agency.Sql.Sqlite) — SQLite

Part of the [Agency AI Toolkit](https://github.com/emre/Agency) — an open-source .NET RAG pipeline.
