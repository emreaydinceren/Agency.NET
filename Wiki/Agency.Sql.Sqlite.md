# Agency.Sql.Sqlite

#sql #sqlite #embeddings #observability

## What It Is

`Agency.Sql.Sqlite` is the SQLite database adapter that provides an async SQL runner and a `vectorize()` placeholder embedder for SQLite-backed pipelines.

**Namespace:** `Agency.Sql.Sqlite`

## API Surface

```csharp
// File: src/Sql/Agency.Sql.Sqlite/SqliteRunner.cs
using Agency.Common;
using Agency.Sql.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Agency.Sql.Sqlite;

public sealed class SqliteRunner : SqlRunnerBase
{
    public const string ActivitySourceName = "Agency.Sql.Sqlite";
    public const string MeterName = "Agency.Sql.Sqlite";

    // Constructor
    public SqliteRunner(
        string connectionString,
        Action<SqliteConnection>? onConnectionOpen = null,
        ILogger<SqliteRunner>? logger = null);

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
}
```

```csharp
// File: src/Sql/Agency.Sql.Sqlite/SQLQueryEmbedder.cs
using Agency.Embeddings.Common;

namespace Agency.Sql.Sqlite;

public partial class SQLQueryEmbedder
{
    public SQLQueryEmbedder(IEmbeddingGenerator embeddingGenerator);

    // Replaces vectorize('<text>') placeholders with quoted '[f1,f2,...]' vector literals.
    public Task<string> EmbedVectorsInQueryAsync(
        string sqlQuery,
        CancellationToken cancellationToken = default);
}
```

## How It Works

**SqliteRunner:**

1. Opens a `SqliteConnection` using the provided connection string.
2. Invokes the optional `onConnectionOpen` callback — used by consumers to register SQLite UDFs (e.g. `vec_distance_cosine`) before any query runs.
3. Builds a `SqliteCommand` with named parameters and executes it.
4. `ExecuteAsync` returns rows-affected; `QueryAsync` returns a `Dataset`; `QueryAsync<TResult>` maps each row through a caller-supplied async predicate.
5. All operations are wrapped in an OTel `Activity` and recorded against the `sqlite.executions` counter and `sqlite.duration` histogram.

**SQLQueryEmbedder:**

1. Scans the SQL string for `vectorize('<text>')` occurrences using a compiled `Regex`.
2. Calls `IEmbeddingGenerator.GenerateEmbeddingAsync` for each unique text.
3. Replaces each match right-to-left with a quoted vector literal `'[f1,f2,...]'`, keeping string indexes stable.
4. Returns the rewritten SQL ready for direct execution against SQLite (e.g. inside a `vec_distance_cosine` UDF call).

## Observability

- **ActivitySource name:** `Agency.Sql.Sqlite`
- **Meter name:** `Agency.Sql.Sqlite`
- **Activities:** `sqlite.execute`, `sqlite.query` — tagged with `db.system`, `db.operation`, `db.statement`, and (on success) `db.rows_affected` / `db.row_count`
- **Counter:** `sqlite.executions` (unit: `{operation}`) — tags: `operation` (`execute`|`query`), `status` (`success`|`error`)
- **Histogram:** `sqlite.duration` (unit: `ms`) — tag: `operation`

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Sql.Common]] | `SqliteRunner` extends `SqlRunnerBase`, which owns all telemetry logic and the `ExecuteAsync`/`QueryAsync` implementations |
| [[Agency.Common]] | `QueryAsync` returns `Dataset`; the `IColumnMetadata` adapter is provided by [[Agency.Sql.Common]] |
| [[Agency.Embeddings.Common]] | `SQLQueryEmbedder` depends on `IEmbeddingGenerator` to resolve vector literals |
| [[Agency.VectorStore.Sql.Sqlite]] | Uses `SqliteRunner` for all DDL/DML and passes `onConnectionOpen` to register vector UDFs |
| [[Agency.KeyValueStore.Sql.Sqlite]] | Uses `SqliteRunner` for key-value persistence over SQLite |

## Design Notes

- `SqliteRunner` is `sealed` and holds no mutable state beyond the connection string and the optional UDF callback, making it safe to share across threads as long as callers manage their own connections.
- The `onConnectionOpen` hook is the intended extension point for registering native SQLite extensions (e.g. sqlite-vec); it avoids coupling this library to any specific vector library while keeping UDF setup co-located with the connection lifecycle.
- `SQLQueryEmbedder` replaces matches right-to-left so that earlier match indexes remain valid after each substitution — a simple correctness invariant that avoids a second index-mapping pass.
- The vector literal format `'[f1,f2,...]'` (quoted string) is compatible with both pgvector's text-cast input and sqlite-vec's text-based distance functions, making the embedder reusable across both SQL backends.
