# Agency.Sql.Postgres

#sql #postgresql #pgvector #observability

## What It Is

Agency.Sql.Postgres is the PostgreSQL adapter that executes raw SQL against a PostgreSQL/pgvector database and resolves `vectorize('...')` macros in SQL text into real embedding vectors before execution.

**Namespace:** `Agency.Sql.Postgres`

## Prerequisites

- A reachable PostgreSQL instance with the `pgvector` extension installed.
- A valid Npgsql connection string, e.g. `Host=localhost;Port=5432;Database=dev_db;Username=dev_user;Password=dev_password`.
- For local development, start the bundled PostgreSQL container: `cd src && docker-compose up -d` (see `src/docker-compose.yml`).
- `SQLQueryEmbedder` additionally requires an [[Agency.Embeddings.Common]] `IEmbeddingGenerator` implementation at runtime.

## API Surface

### `PostgreSqlRunner`

Sealed, async-disposable SQL runner that owns a singleton `NpgsqlDataSource` with `pgvector` support. Inherits `ExecuteAsync` and `QueryAsync` from [[Agency.Sql.Common]] `SqlRunnerBase`.

```csharp
// File: src/Sql/Agency.Sql.Postgres/PostgreSqlRunner.cs
using Agency.Sql.Common;
using Agency.Common;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Data.Common;

namespace Agency.Sql.Postgres;

public sealed class PostgreSqlRunner : SqlRunnerBase, IAsyncDisposable
{
    public const string ActivitySourceName = "Agency.Sql.Postgres";
    public const string MeterName = "Agency.Sql.Postgres";

    // Constructor
    public PostgreSqlRunner(string connectionString, ILogger<PostgreSqlRunner>? logger = null);

    // Inherited from SqlRunnerBase
    public Task<int> ExecuteAsync(
        string sql,
        Dictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default);

    public Task<Dataset> QueryAsync(
        string sql,
        Dictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default);

    public Task<List<TResult>> QueryAsync<TResult>(
        string sql,
        Func<DbDataReader, Task<TResult>> predicate,
        Dictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default);

    public ValueTask DisposeAsync();
}
```

### `SQLQueryEmbedder`

Preprocesses SQL text by replacing all `vectorize('<text>')` macro calls with pgvector literal strings before the query is sent to PostgreSQL.

```csharp
// File: src/Sql/Agency.Sql.Postgres/SQLQueryEmbedder.cs
using Agency.Embeddings.Common;

namespace Agency.Sql.Postgres;

public partial class SQLQueryEmbedder
{
    public SQLQueryEmbedder(IEmbeddingGenerator embeddingGenerator);

    public Task<string> EmbedVectorsInQueryAsync(
        string sqlQuery,
        CancellationToken cancellationToken = default);
}
```

## How It Works

**`PostgreSqlRunner`** builds a single `NpgsqlDataSource` at construction time using `NpgsqlDataSourceBuilder.UseVector()`, which registers pgvector type mappings. Every `ExecuteAsync` or `QueryAsync` call opens a connection from that pool, creates an `NpgsqlCommand`, and closes the connection on completion. Parameters may be passed as a `Dictionary<string, object?>` or as pre-built `NpgsqlParameter` instances (for typed pgvector columns). The generic overload of `QueryAsync<TResult>` accepts an async row-mapping delegate instead of returning a `Dataset`.

**`SQLQueryEmbedder`** applies a source-generated `[GeneratedRegex]` to find every `vectorize('<text>')` call in the SQL string. It processes matches right-to-left so that earlier character offsets remain valid after each substitution. For each match it calls `IEmbeddingGenerator.GenerateEmbeddingAsync`, then formats the resulting `ReadOnlySpan<float>` as a pgvector SQL literal (`'[f1,f2,...]'`). Escaped single-quotes inside the argument text (`''`) are unescaped before embedding.

## Observability

`PostgreSqlRunner` instruments every operation via the [[Agency.Sql.Common]] base class pattern:

| Signal | Name | Tags |
|---|---|---|
| Activity | `postgresql.execute` / `postgresql.query` | `db.system`, `db.operation`, `db.statement` |
| Counter | `postgresql.executions` | `operation`, `status` |
| Histogram | `postgresql.duration` (ms) | `operation` |

- **ActivitySource name:** `Agency.Sql.Postgres`
- **Meter name:** `Agency.Sql.Postgres`

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Sql.Common]] | `PostgreSqlRunner` extends `SqlRunnerBase`, which provides the full telemetry skeleton and the `ExecuteAsync` / `QueryAsync` implementations |
| [[Agency.Common]] | `QueryAsync` returns `Dataset`; column schema is adapted via `DbColumnAdapter` defined in [[Agency.Sql.Common]] |
| [[Agency.Embeddings.Common]] | `SQLQueryEmbedder` depends on `IEmbeddingGenerator` to resolve `vectorize()` macros |
| [[Agency.VectorStore.Sql.Postgre]] | Uses `PostgreSqlRunner` for all database access |
| [[Agency.KeyValueStore.Sql.Postgre]] | Uses `PostgreSqlRunner` for all database access |
| [[Agency.RagFormatter]] | Formats the `Dataset` objects returned by `QueryAsync` into Markdown tables for LLM context |

## Design Notes

- `PostgreSqlRunner` is sealed and owns its `NpgsqlDataSource` lifetime; callers must dispose it with `await using` or `DisposeAsync()` to return pooled connections cleanly.
- The `vectorize()` macro substitution is intentionally a pure text transformation with no SQL parsing, keeping `SQLQueryEmbedder` independent of query structure and composable with any SQL dialect that pgvector supports.
- Passing a `NpgsqlParameter` directly as a dictionary value (instead of a raw object) allows callers to specify strongly-typed pgvector parameters (e.g. `Vector`) without going through the generic `AddWithValue` path.
- Both `ActivitySourceName` and `MeterName` are exposed as `public const string` fields so that host applications can subscribe to the exact source/meter names when configuring OpenTelemetry pipelines.
