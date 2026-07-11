using Agency.Common;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Agency.Sql.Common;

/// <summary>
/// Base class for database SQL runners. Provides the shared telemetry, validation, and execution
/// skeleton; subclasses supply only the connection factory and command builder.
/// </summary>
public abstract partial class SqlRunnerBase
{
    private readonly ActivitySource _activitySource;
    private readonly Counter<long> _executionCount;
    private readonly Histogram<double> _executionDuration;
    private readonly string _dbSystem;
    private readonly ILogger _logger;

    /// <summary>Logger for this runner instance.</summary>
    protected ILogger Logger => this._logger;

    /// <summary>
    /// Initialises the shared telemetry state.
    /// </summary>
    /// <param name="activitySource">The provider-specific <see cref="ActivitySource"/>.</param>
    /// <param name="executionCount">Counter tracking total operation executions.</param>
    /// <param name="executionDuration">Histogram tracking operation duration.</param>
    /// <param name="dbSystem">OTel <c>db.system</c> value and activity-name prefix (e.g. <c>"sqlite"</c>).</param>
    /// <param name="logger">Logger instance.</param>
    protected SqlRunnerBase(
        ActivitySource activitySource,
        Counter<long> executionCount,
        Histogram<double> executionDuration,
        string dbSystem,
        ILogger logger)
    {
        this._activitySource = activitySource;
        this._executionCount = executionCount;
        this._executionDuration = executionDuration;
        this._dbSystem = dbSystem;
        this._logger = logger;
    }

    /// <summary>Opens a new database connection.</summary>
    protected abstract Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken);

    /// <summary>Builds a provider-specific command from the given connection, SQL, and parameters.</summary>
    protected abstract DbCommand BuildCommand(DbConnection connection, string sql, Dictionary<string, object?>? parameters);

    /// <summary>
    /// Executes a non-query SQL statement (DDL or DML) and returns the number of rows affected.
    /// </summary>
    public async Task<int> ExecuteAsync(
        string sql,
        Dictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("SQL statement cannot be null or whitespace.", nameof(sql));
        }

        using var activity = this._activitySource.StartActivity($"{this._dbSystem}.execute", ActivityKind.Client);
        activity?.SetTag("db.system", this._dbSystem);
        activity?.SetTag("db.operation", "execute");
        activity?.SetTag("db.statement", sql);

        var stopwatch = Stopwatch.StartNew();
        this.LogExecutingNonQuery(sql);

        try
        {
            await using var connection = await this.OpenConnectionAsync(cancellationToken);
            await using var command = this.BuildCommand(connection, sql, parameters);
            int rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);

            stopwatch.Stop();
            this._executionCount.Add(1, new TagList { { "operation", "execute" }, { "status", "success" } });
            this._executionDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "execute" } });

            activity?.SetTag("db.rows_affected", rowsAffected);
            activity?.SetStatus(ActivityStatusCode.Ok);

            this.LogNonQueryCompleted(stopwatch.Elapsed.TotalMilliseconds, rowsAffected);
            return rowsAffected;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            this._executionCount.Add(1, new TagList { { "operation", "execute" }, { "status", "error" } });
            this._executionDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "execute" } });

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                { "exception.type", ex.GetType().FullName },
                { "exception.message", ex.Message },
                { "exception.stacktrace", ex.ToString() },
            }));

            this.LogErrorExecutingNonQuery(ex, stopwatch.Elapsed.TotalMilliseconds, sql);
            throw;
        }
    }

    /// <summary>
    /// Executes a SQL query and returns each row as a read-only dictionary keyed by column name.
    /// </summary>
    public async Task<Dataset> QueryAsync(
        string sql,
        Dictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("SQL query cannot be null or whitespace.", nameof(sql));
        }

        using var activity = this._activitySource.StartActivity($"{this._dbSystem}.query", ActivityKind.Client);
        activity?.SetTag("db.system", this._dbSystem);
        activity?.SetTag("db.operation", "query");
        activity?.SetTag("db.statement", sql);

        var stopwatch = Stopwatch.StartNew();
        this.LogExecutingQuery(sql);

        try
        {
            await using var connection = await this.OpenConnectionAsync(cancellationToken);
            await using var command = this.BuildCommand(connection, sql, parameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            ReadOnlyCollection<DbColumn> columnSchema = await reader.GetColumnSchemaAsync(cancellationToken);
            List<object?[]> rows = new();

            while (await reader.ReadAsync(cancellationToken))
            {
                object?[] fields = new object?[reader.FieldCount];
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    fields[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                rows.Add(fields);
            }

            stopwatch.Stop();
            this._executionCount.Add(1, new TagList { { "operation", "query" }, { "status", "success" } });
            this._executionDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "query" } });

            activity?.SetTag("db.row_count", rows.Count);
            activity?.SetStatus(ActivityStatusCode.Ok);

            this.LogQueryCompleted(stopwatch.Elapsed.TotalMilliseconds, rows.Count);

            var columns = columnSchema.Select(c => (IColumnMetadata)new DbColumnAdapter(c)).ToList();
            return new Dataset(columns, rows);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            this._executionCount.Add(1, new TagList { { "operation", "query" }, { "status", "error" } });
            this._executionDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "query" } });

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                { "exception.type", ex.GetType().FullName },
                { "exception.message", ex.Message },
                { "exception.stacktrace", ex.ToString() },
            }));

            this.LogErrorExecutingQuery(ex, stopwatch.Elapsed.TotalMilliseconds, sql);
            throw;
        }
    }

    /// <summary>
    /// Executes a SQL query and maps each row to a custom object model using the provided async predicate reader.
    /// </summary>
    public async Task<List<TResult>> QueryAsync<TResult>(
        string sql,
        Func<DbDataReader, Task<TResult>> predicate,
        Dictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("SQL query cannot be null or whitespace.", nameof(sql));
        }

        ArgumentNullException.ThrowIfNull(predicate);

        using var activity = this._activitySource.StartActivity($"{this._dbSystem}.query", ActivityKind.Client);
        activity?.SetTag("db.system", this._dbSystem);
        activity?.SetTag("db.operation", "query");
        activity?.SetTag("db.statement", sql);

        var stopwatch = Stopwatch.StartNew();
        this.LogExecutingQueryWithPredicate(sql);

        try
        {
            await using var connection = await this.OpenConnectionAsync(cancellationToken);
            await using var command = this.BuildCommand(connection, sql, parameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            List<TResult> results = new();

            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(await predicate(reader));
            }

            stopwatch.Stop();
            this._executionCount.Add(1, new TagList { { "operation", "query" }, { "status", "success" } });
            this._executionDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "query" } });

            activity?.SetTag("db.row_count", results.Count);
            activity?.SetStatus(ActivityStatusCode.Ok);

            this.LogQueryWithPredicateCompleted(stopwatch.Elapsed.TotalMilliseconds, results.Count);
            return results;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            this._executionCount.Add(1, new TagList { { "operation", "query" }, { "status", "error" } });
            this._executionDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "query" } });

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                { "exception.type", ex.GetType().FullName },
                { "exception.message", ex.Message },
                { "exception.stacktrace", ex.ToString() },
            }));

            this.LogErrorExecutingQueryWithPredicate(ex, stopwatch.Elapsed.TotalMilliseconds, sql);
            throw;
        }
    }

    /// <summary>Logs that a non-query SQL statement is being executed.</summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "Executing non-query SQL: {Sql}")]
    private partial void LogExecutingNonQuery(string sql);

    /// <summary>Logs that a non-query SQL statement completed.</summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "Non-query SQL completed in {ElapsedMs}ms. Rows affected: {RowsAffected}")]
    private partial void LogNonQueryCompleted(double elapsedMs, int rowsAffected);

    /// <summary>Logs that a non-query SQL statement failed.</summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "Error executing non-query SQL after {ElapsedMs}ms: {Sql}")]
    private partial void LogErrorExecutingNonQuery(Exception ex, double elapsedMs, string sql);

    /// <summary>Logs that a SQL query is being executed.</summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "Executing query SQL: {Sql}")]
    private partial void LogExecutingQuery(string sql);

    /// <summary>Logs that a SQL query completed.</summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "Query SQL completed in {ElapsedMs}ms. Rows returned: {RowCount}")]
    private partial void LogQueryCompleted(double elapsedMs, int rowCount);

    /// <summary>Logs that a SQL query failed.</summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "Error executing query SQL after {ElapsedMs}ms: {Sql}")]
    private partial void LogErrorExecutingQuery(Exception ex, double elapsedMs, string sql);

    /// <summary>Logs that a SQL query with a row-mapping predicate is being executed.</summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "Executing query SQL with predicate: {Sql}")]
    private partial void LogExecutingQueryWithPredicate(string sql);

    /// <summary>Logs that a SQL query with a row-mapping predicate completed.</summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "Query SQL with predicate completed in {ElapsedMs}ms. Rows returned: {RowCount}")]
    private partial void LogQueryWithPredicateCompleted(double elapsedMs, int rowCount);

    /// <summary>Logs that a SQL query with a row-mapping predicate failed.</summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "Error executing query SQL with predicate after {ElapsedMs}ms: {Sql}")]
    private partial void LogErrorExecutingQueryWithPredicate(Exception ex, double elapsedMs, string sql);

    internal sealed class DbColumnAdapter : IColumnMetadata
    {
        private readonly DbColumn _column;

        internal DbColumnAdapter(DbColumn column)
        {
            this._column = column;
        }

        public string? ColumnName => this._column.ColumnName;

        public int? ColumnOrdinal => this._column.ColumnOrdinal;
    }
}
