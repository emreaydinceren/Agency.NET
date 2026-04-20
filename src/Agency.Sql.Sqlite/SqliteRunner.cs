using Agency.Sql.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Agency.Sql.Sqlite;

/// <summary>
/// Runs raw SQL statements and queries against a SQLite database.
/// </summary>
public sealed class SqliteRunner : SqlRunnerBase
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

    private readonly string _connectionString;
    private readonly Action<SqliteConnection>? _onConnectionOpen;

    /// <summary>
    /// Creates a new SQLite runner for the provided connection string.
    /// </summary>
    /// <param name="connectionString">The SQLite connection string.</param>
    /// <param name="onConnectionOpen">Optional callback invoked after each connection is opened. Use this to register UDFs or load extensions.</param>
    /// <param name="logger">Optional logger.</param>
    public SqliteRunner(string connectionString, Action<SqliteConnection>? onConnectionOpen = null, ILogger<SqliteRunner>? logger = null)
        : base(_activitySource, _executionCount, _executionDuration, "sqlite", logger ?? NullLogger<SqliteRunner>.Instance)
    {
        this._connectionString = !string.IsNullOrWhiteSpace(connectionString)
            ? connectionString
            : throw new ArgumentException("Connection string cannot be null or whitespace.", nameof(connectionString));
        this._onConnectionOpen = onConnectionOpen;
    }

    /// <inheritdoc/>
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(this._connectionString);
        await connection.OpenAsync(cancellationToken);
        this._onConnectionOpen?.Invoke(connection);
        return connection;
    }

    /// <inheritdoc/>
    protected override DbCommand BuildCommand(DbConnection connection, string sql, Dictionary<string, object?>? parameters)
    {
        var command = new SqliteCommand(sql, (SqliteConnection)connection);

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
