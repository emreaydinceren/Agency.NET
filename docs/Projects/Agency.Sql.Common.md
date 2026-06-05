# Agency.Sql.Common
#sql #abstractions #observability #base

## What It Is

Agency.Sql.Common is the shared base library that provides the full OpenTelemetry telemetry skeleton and execution algorithm common to all SQL runner implementations in the solution. Subclasses supply only the provider-specific connection factory and command builder; all instrumentation and error recording logic lives here.

Namespace: `Agency.Sql.Common`

## API Surface

```csharp
using Agency.Common;
using Microsoft.Extensions.Logging;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Agency.Sql.Common;
```

| Type | Member | Signature |
|---|---|---|
| `SqlRunnerBase` (abstract class) | Constructor | `protected SqlRunnerBase(ActivitySource activitySource, Counter<long> executionCount, Histogram<double> executionDuration, string dbSystem, ILogger logger)` |
| `SqlRunnerBase` | `Logger` | `protected readonly ILogger Logger` |
| `SqlRunnerBase` | `OpenConnectionAsync` | `protected abstract Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)` |
| `SqlRunnerBase` | `BuildCommand` | `protected abstract DbCommand BuildCommand(DbConnection connection, string sql, Dictionary<string, object?>? parameters)` |
| `SqlRunnerBase` | `ExecuteAsync` | `public Task<int> ExecuteAsync(string sql, Dictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)` |
| `SqlRunnerBase` | `QueryAsync` | `public Task<Dataset> QueryAsync(string sql, Dictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)` |
| `SqlRunnerBase` | `QueryAsync<TResult>` | `public Task<List<TResult>> QueryAsync<TResult>(string sql, Func<DbDataReader, Task<TResult>> predicate, Dictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default)` |

## Dependencies

- `Agency.Common`
- `Microsoft.Extensions.Logging.Abstractions`

## Related

- [[Agency.Common]]
- [[Agency.Sql.Postgres]]
- [[Agency.Sql.Sqlite]]
