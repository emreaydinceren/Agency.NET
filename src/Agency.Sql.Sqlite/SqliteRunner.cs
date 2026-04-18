using Agency.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.ObjectModel;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Agency.Sql.Sqlite;

/// <summary>
/// Runs raw SQL statements and queries against a SQLite database.
/// </summary>
public sealed class SqliteRunner
{
    /// <summary>
    /// The activity source name used for SQL telemetry.
    /// </summary>
    public const string ActivitySourceName = "Agency.Sql.Sqlite";

    /// <summary>
    /// The meter name used for SQL telemetry.
    /// </summary>
    public const string MeterName = "Agency.Sql.Sqlite";

    private static readonly ActivitySource _activitySource = new(ActivitySourceName);
    private static readonly Meter _meter = new(MeterName);

    private static readonly Counter<long> _executionCount =
        _meter.CreateCounter<long>("sqlite.executions", unit: "{operation}", description: "Total number of SQL operations executed.");

    private static readonly Histogram<double> _executionDuration =
        _meter.CreateHistogram<double>("sqlite.duration", unit: "ms", description: "Duration of SQL operations in milliseconds.");

    private readonly ILogger<SqliteRunner> _logger;
    private readonly string _connectionString;
    private readonly Action<SqliteConnection>? _onConnectionOpen;

    /// <summary>
    /// Creates a new SQLite runner for the provided connection string.
    /// </summary>
    /// <param name="connectionString">The SQLite connection string.</param>
    /// <param name="onConnectionOpen">Optional callback invoked after each connection is opened. Use this to register UDFs or load extensions.</param>
    /// <param name="logger">Optional logger.</param>
    public SqliteRunner(string connectionString, Action<SqliteConnection>? onConnectionOpen = null, ILogger<SqliteRunner>? logger = null)
    {
        this._logger = logger ?? NullLogger<SqliteRunner>.Instance;
        this._onConnectionOpen = onConnectionOpen;
        this._connectionString = !string.IsNullOrWhiteSpace(connectionString)
            ? connectionString
            : throw new ArgumentException("Connection string cannot be null or whitespace.", nameof(connectionString));
    }

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

        using var activity = _activitySource.StartActivity("sqlite.execute", ActivityKind.Client);
        activity?.SetTag("db.system", "sqlite");
        activity?.SetTag("db.operation", "execute");
        activity?.SetTag("db.statement", sql);

        var stopwatch = Stopwatch.StartNew();
        this._logger.LogDebug("Executing non-query SQL: {Sql}", sql);

        try
        {
            await using var connection = await this.OpenConnectionAsync(cancellationToken);
            await using var command = BuildCommand(connection, sql, parameters);
            int rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);

            stopwatch.Stop();
            _executionCount.Add(1, new TagList { { "operation", "execute" }, { "status", "success" } });
            _executionDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "execute" } });

            activity?.SetTag("db.rows_affected", rowsAffected);
            activity?.SetStatus(ActivityStatusCode.Ok);

            this._logger.LogDebug("Non-query SQL completed in {ElapsedMs}ms. Rows affected: {RowsAffected}", stopwatch.Elapsed.TotalMilliseconds, rowsAffected);
            return rowsAffected;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _executionCount.Add(1, new TagList { { "operation", "execute" }, { "status", "error" } });
            _executionDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "execute" } });

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                { "exception.type", ex.GetType().FullName },
                { "exception.message", ex.Message },
                { "exception.stacktrace", ex.ToString() },
            }));

            this._logger.LogError(ex, "Error executing non-query SQL after {ElapsedMs}ms: {Sql}", stopwatch.Elapsed.TotalMilliseconds, sql);
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

        using var activity = _activitySource.StartActivity("sqlite.query", ActivityKind.Client);
        activity?.SetTag("db.system", "sqlite");
        activity?.SetTag("db.operation", "query");
        activity?.SetTag("db.statement", sql);

        var stopwatch = Stopwatch.StartNew();
        this._logger.LogDebug("Executing query SQL: {Sql}", sql);

        try
        {
            await using var connection = await this.OpenConnectionAsync(cancellationToken);
            await using var command = BuildCommand(connection, sql, parameters);
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
            _executionCount.Add(1, new TagList { { "operation", "query" }, { "status", "success" } });
            _executionDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "query" } });

            activity?.SetTag("db.row_count", rows.Count);
            activity?.SetStatus(ActivityStatusCode.Ok);

            this._logger.LogDebug("Query SQL completed in {ElapsedMs}ms. Rows returned: {RowCount}", stopwatch.Elapsed.TotalMilliseconds, rows.Count);

            var columns = columnSchema.Select(c => (IColumnMetadata)new DbColumnAdapter(c)).ToList();
            return new Dataset(columns, rows);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _executionCount.Add(1, new TagList { { "operation", "query" }, { "status", "error" } });
            _executionDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "query" } });

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                { "exception.type", ex.GetType().FullName },
                { "exception.message", ex.Message },
                { "exception.stacktrace", ex.ToString() },
            }));

            this._logger.LogError(ex, "Error executing query SQL after {ElapsedMs}ms: {Sql}", stopwatch.Elapsed.TotalMilliseconds, sql);
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

        if (predicate is null)
        {
            throw new ArgumentNullException(nameof(predicate));
        }

        using var activity = _activitySource.StartActivity("sqlite.query", ActivityKind.Client);
        activity?.SetTag("db.system", "sqlite");
        activity?.SetTag("db.operation", "query");
        activity?.SetTag("db.statement", sql);

        var stopwatch = Stopwatch.StartNew();
        this._logger.LogDebug("Executing query SQL with predicate: {Sql}", sql);

        try
        {
            await using var connection = await this.OpenConnectionAsync(cancellationToken);
            await using var command = BuildCommand(connection, sql, parameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            List<TResult> results = new();

            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(await predicate(reader));
            }

            stopwatch.Stop();
            _executionCount.Add(1, new TagList { { "operation", "query" }, { "status", "success" } });
            _executionDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "query" } });

            activity?.SetTag("db.row_count", results.Count);
            activity?.SetStatus(ActivityStatusCode.Ok);

            this._logger.LogDebug("Query SQL with predicate completed in {ElapsedMs}ms. Rows returned: {RowCount}", stopwatch.Elapsed.TotalMilliseconds, results.Count);
            return results;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _executionCount.Add(1, new TagList { { "operation", "query" }, { "status", "error" } });
            _executionDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "query" } });

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                { "exception.type", ex.GetType().FullName },
                { "exception.message", ex.Message },
                { "exception.stacktrace", ex.ToString() },
            }));

            this._logger.LogError(ex, "Error executing query SQL with predicate after {ElapsedMs}ms: {Sql}", stopwatch.Elapsed.TotalMilliseconds, sql);
            throw;
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(this._connectionString);
        await connection.OpenAsync(cancellationToken);
        this._onConnectionOpen?.Invoke(connection);
        return connection;
    }

    private static SqliteCommand BuildCommand(
        SqliteConnection connection,
        string sql,
        Dictionary<string, object?>? parameters)
    {
        var command = new SqliteCommand(sql, connection);

        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }
        }

        return command;
    }

    private sealed class DbColumnAdapter : IColumnMetadata
    {
        private readonly DbColumn _column;

        public DbColumnAdapter(DbColumn column)
        {
            this._column = column;
        }

        public string? ColumnName => this._column.ColumnName;

        public int? ColumnOrdinal => this._column.ColumnOrdinal;
    }
}
