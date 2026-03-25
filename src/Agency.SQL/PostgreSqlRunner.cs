using Agency.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using System.Collections.ObjectModel;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Agency.SQL;

/// <summary>
/// Runs raw SQL statements and queries against a PostgreSQL instance.
/// </summary>
public sealed class PostgreSqlRunner
{
    /// <summary>
    /// The activity source name used for SQL telemetry.
    /// </summary>
    public static readonly string ActivitySourceName = "Agency.SQL";

    /// <summary>
    /// The meter name used for SQL telemetry.
    /// </summary>
    public static readonly string MeterName = "Agency.SQL";

    private static readonly ActivitySource _activitySource = new(ActivitySourceName);
    private static readonly Meter _meter = new(MeterName);

    private static readonly Counter<long> _executionCount =
        _meter.CreateCounter<long>("postgresql.executions", unit: "{operation}", description: "Total number of SQL operations executed.");

    private static readonly Histogram<double> _executionDuration =
        _meter.CreateHistogram<double>("postgresql.duration", unit: "ms", description: "Duration of SQL operations in milliseconds.");

    private readonly ILogger<PostgreSqlRunner> _logger;

    private readonly string _connectionString;

    /// <summary>
    /// Creates a new PostgreSQL runner for the provided connection string.
    /// </summary>
    public PostgreSqlRunner(string connectionString, ILogger<PostgreSqlRunner>? logger = null)
    {
        _logger = logger ?? NullLogger<PostgreSqlRunner>.Instance;
        _connectionString = !string.IsNullOrWhiteSpace(connectionString)
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

        using var activity = _activitySource.StartActivity("postgresql.execute", ActivityKind.Client);
        activity?.SetTag("db.system", "postgresql");
        activity?.SetTag("db.operation", "execute");
        activity?.SetTag("db.statement", sql);

        var stopwatch = Stopwatch.StartNew();
        _logger.LogDebug("Executing non-query SQL: {Sql}", sql);

        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = BuildCommand(connection, sql, parameters);
            int rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);

            stopwatch.Stop();
            _executionCount.Add(1, new TagList { { "operation", "execute" }, { "status", "success" } });
            _executionDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "execute" } });

            activity?.SetTag("db.rows_affected", rowsAffected);
            activity?.SetStatus(ActivityStatusCode.Ok);

            _logger.LogDebug("Non-query SQL completed in {ElapsedMs}ms. Rows affected: {RowsAffected}", stopwatch.Elapsed.TotalMilliseconds, rowsAffected);
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

            _logger.LogError(ex, "Error executing non-query SQL after {ElapsedMs}ms: {Sql}", stopwatch.Elapsed.TotalMilliseconds, sql);
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

        using var activity = _activitySource.StartActivity("postgresql.query", ActivityKind.Client);
        activity?.SetTag("db.system", "postgresql");
        activity?.SetTag("db.operation", "query");
        activity?.SetTag("db.statement", sql);

        var stopwatch = Stopwatch.StartNew();
        _logger.LogDebug("Executing query SQL: {Sql}", sql);

        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = BuildCommand(connection, sql, parameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            List<object?[]> rows = new();

            ReadOnlyCollection<DbColumn> columnSchema = await reader.GetColumnSchemaAsync(cancellationToken);

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

            _logger.LogDebug("Query SQL completed in {ElapsedMs}ms. Rows returned: {RowCount}", stopwatch.Elapsed.TotalMilliseconds, rows.Count);
            return new Dataset(columnSchema, rows);
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

            _logger.LogError(ex, "Error executing query SQL after {ElapsedMs}ms: {Sql}", stopwatch.Elapsed.TotalMilliseconds, sql);
            throw;
        }
    }

    private static NpgsqlCommand BuildCommand(
        NpgsqlConnection connection,
        string sql,
        Dictionary<string, object?>? parameters)
    {
        var command = new NpgsqlCommand(sql, connection);

        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }
        }

        return command;
    }
}
