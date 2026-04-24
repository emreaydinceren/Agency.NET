# Agency.Sql.Common

#sql #abstractions #observability #base

## What It Is

`Agency.Sql.Common` provides `SqlRunnerBase` — an abstract base class that owns the full OTel telemetry skeleton shared by every SQL runner in the solution. Concrete runners (`PostgreSqlRunner`, `SqliteRunner`) extend it and supply only the two provider-specific details: how to open a connection and how to build a parameterized command.

## Key Types

### `SqlRunnerBase`

```csharp
public abstract class SqlRunnerBase
{
    // Subclasses implement these two factory methods:
    protected abstract Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken);
    protected abstract DbCommand BuildCommand(DbConnection connection, string sql, Dictionary<string, object?>? parameters);

    // Public surface exposed to callers:
    public Task<int>             ExecuteAsync(string sql, Dictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default);
    public Task<Dataset>         QueryAsync(string sql, Dictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default);
    public Task<List<TResult>>   QueryAsync<TResult>(string sql, Func<DbDataReader, Task<TResult>> predicate, Dictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default);
}
```

The base class wires up the `ActivitySource`, execution counter, and duration histogram that are passed in from the concrete subclass constructor. All three public methods follow the same pattern:

1. Start a named `Activity` with `db.system`, `db.operation`, and `db.statement` tags.
2. Open a connection via the abstract method.
3. Build and execute the command.
4. Record the counter and histogram on both success and failure paths.
5. Set `ActivityStatusCode.Ok` or propagate the exception.

### `DbColumnAdapter` (internal)

An internal adapter that wraps `DbColumn` (from `GetColumnSchemaAsync`) to satisfy [[Agency.Common]]'s `IColumnMetadata` interface. Used by `QueryAsync` when assembling a `Dataset`.

## How It Relates to Other Projects

| Project | Relationship |
|---|---|
| [[Agency.Sql.Postgre]] | `PostgreSqlRunner` extends `SqlRunnerBase`, supplying the Npgsql connection factory and command builder |
| [[Agency.Sql.Sqlite]] | `SqliteRunner` extends `SqlRunnerBase`, supplying the SQLite connection factory and command builder |
| [[Agency.Common]] | Returns `Dataset`; uses `IColumnMetadata` via `DbColumnAdapter` |

## Design Notes

`SqlRunnerBase` is a classic **Template Method** pattern: the invariant algorithm (telemetry → open → execute → record → return) lives in the base class while the variant parts (provider-specific connection and command creation) are pushed to subclasses via `abstract` methods. This eliminated a large block of duplicated OTel code that previously lived in both PostgreSQL and SQLite runners.
