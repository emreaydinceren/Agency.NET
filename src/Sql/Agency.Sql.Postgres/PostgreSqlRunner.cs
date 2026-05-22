using Agency.Sql.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Agency.Sql.Postgres;

/// <summary>
/// Runs raw SQL statements and queries against a PostgreSQL instance.
/// Owns a singleton <see cref="NpgsqlDataSource"/> for connection pooling; dispose this class when done.
/// </summary>
public sealed class PostgreSqlRunner : SqlRunnerBase, IAsyncDisposable
{
    /// <summary>
    /// The activity source name used for SQL telemetry.
    /// </summary>
    public const string ActivitySourceName = "Agency.Sql.Postgres";

    /// <summary>
    /// The meter name used for SQL telemetry.
    /// </summary>
    public const string MeterName = "Agency.Sql.Postgres";

    private static readonly ActivitySource _activitySource = new(ActivitySourceName);
    private static readonly Meter _meter = new(MeterName);

    private static readonly Counter<long> _executionCount =
        _meter.CreateCounter<long>("postgresql.executions", unit: "{operation}", description: "Total number of SQL operations executed.");

    private static readonly Histogram<double> _executionDuration =
        _meter.CreateHistogram<double>("postgresql.duration", unit: "ms", description: "Duration of SQL operations in milliseconds.");

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>
    /// Creates a new PostgreSQL runner for the provided connection string.
    /// </summary>
    public PostgreSqlRunner(string connectionString, ILogger<PostgreSqlRunner>? logger = null)
        : base(_activitySource, _executionCount, _executionDuration, "postgresql", logger ?? NullLogger<PostgreSqlRunner>.Instance)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string cannot be null or whitespace.", nameof(connectionString));
        }

        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        this._dataSource = builder.Build();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => this._dataSource.DisposeAsync();

    /// <inheritdoc/>
    protected override async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        => await this._dataSource.OpenConnectionAsync(cancellationToken);

    /// <inheritdoc/>
    protected override DbCommand BuildCommand(DbConnection connection, string sql, Dictionary<string, object?>? parameters)
    {
        var command = new NpgsqlCommand(sql, (NpgsqlConnection)connection);

        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
            {
                if (value is NpgsqlParameter parameter)
                {
                    command.Parameters.Add(parameter);
                }
                else
                {
                    command.Parameters.AddWithValue(name, value ?? DBNull.Value);
                }
            }
        }

        return command;
    }
}
