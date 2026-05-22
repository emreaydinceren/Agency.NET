# Agency.Sql.Common

#sql #abstractions #observability #base

## What It Is

Agency.Sql.Common is the shared base library that provides the full OpenTelemetry telemetry skeleton and execution algorithm common to all SQL runner implementations in the solution.

**Namespace:** `Agency.Sql.Common`

## API Surface

```csharp
// File: src/Sql/Agency.Sql.Common/SqlRunnerBase.cs
using Agency.Common;
using Microsoft.Extensions.Logging;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Agency.Sql.Common;

public abstract class SqlRunnerBase
{
    protected readonly ILogger Logger;

    protected SqlRunnerBase(
        ActivitySource activitySource,
        Counter<long> executionCount,
        Histogram<double> executionDuration,
        string dbSystem,
        ILogger logger);

    // Subclasses implement these two factory methods:
    protected abstract Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken);
    protected abstract DbCommand BuildCommand(DbConnection connection, string sql, Dictionary<string, object?>? parameters);

    // Public execution surface:
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

// Internal adapter — not part of the public contract:
// DbColumnAdapter : IColumnMetadata  (wraps DbColumn from GetColumnSchemaAsync)
```

## How It Works

All three public methods follow the same algorithm:

1. Validate that `sql` is not null or whitespace.
2. Start a named `Activity` tagged with `db.system`, `db.operation`, and `db.statement`.
3. Start a `Stopwatch` and call the abstract `OpenConnectionAsync`.
4. Build a provider-specific command via the abstract `BuildCommand` and execute it.
5. On success: record the execution counter (`status=success`) and duration histogram, set `ActivityStatusCode.Ok`, and return the result.
6. On failure: record the counter (`status=error`) and duration histogram, attach an `exception` `ActivityEvent`, set `ActivityStatusCode.Error`, and rethrow.

`QueryAsync` (non-generic) reads all rows into memory, adapts the `DbColumn` schema via the internal `DbColumnAdapter`, and returns a [[Agency.Common]] `Dataset`.

`QueryAsync<TResult>` streams each row through a caller-supplied async `predicate` (`Func<DbDataReader, Task<TResult>>`) and collects results into a `List<TResult>`.

## Observability

`SqlRunnerBase` does not own its own `ActivitySource` or `Meter`; it receives them from the concrete subclass constructor and records against them. Each subclass provides its own named source and meter so that PostgreSQL and SQLite telemetry appear under distinct names in any OTel backend.

| Signal | Type | Tags |
|---|---|---|
| Activity | `{dbSystem}.execute` / `{dbSystem}.query` | `db.system`, `db.operation`, `db.statement`, `db.rows_affected` / `db.row_count` |
| Counter | passed in as `executionCount` | `operation` (`execute`/`query`), `status` (`success`/`error`) |
| Histogram | passed in as `executionDuration` (ms) | `operation` |

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Sql.Postgres]] | `PostgreSqlRunner` extends `SqlRunnerBase`, supplying the Npgsql connection factory and command builder |
| [[Agency.Sql.Sqlite]] | `SqliteRunner` extends `SqlRunnerBase`, supplying the SQLite connection factory and command builder |
| [[Agency.Common]] | Returns `Dataset`; uses `IColumnMetadata` via the internal `DbColumnAdapter` |

## Design Notes

- `SqlRunnerBase` is a classic **Template Method** pattern: the invariant algorithm (telemetry → open → execute → record → return) lives in the base class while the variant parts (provider-specific connection and command creation) are delegated to subclasses via `abstract` methods, eliminating duplicated OTel code that previously lived in both provider projects.
- The `ActivitySource`, `Counter<long>`, and `Histogram<double>` are injected via the constructor rather than being created inside the base class, preserving the convention that each concrete runner owns its own named OTel instruments while still centralising all recording logic.
