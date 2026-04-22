namespace Agency.KeyValueStore.Sql.Sqlite;

using Agency.KeyValueStore.Common;
using Agency.Sql.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;

/// <summary>
/// An <see cref="IKVStore"/> backed by SQLite that stores key/value entries in a plain TEXT column
/// and supports substring-based value filtering via the SQLite <c>instr</c> function.
/// Metadata filtering is applied in-process after the SQL query because SQLite has no native JSONB
/// containment operator. Results are ordered by recency (newest first).
/// </summary>
public sealed class SqliteKVStore : IKVStore
{
    /// <summary>The activity source name used for KV store telemetry.</summary>
    public const string ActivitySourceName = "Agency.KeyValueStore.Sql.Sqlite";

    /// <summary>The meter name used for KV store telemetry.</summary>
    public const string MeterName = "Agency.KeyValueStore.Sql.Sqlite";

    private static readonly ActivitySource _activitySource = new(ActivitySourceName);
    private static readonly Meter _meter = new(MeterName);

    private static readonly Counter<long> _operationCount =
        _meter.CreateCounter<long>("kvstore.operations", unit: "{operation}", description: "Total number of KV store operations executed.");

    private static readonly Histogram<double> _operationDuration =
        _meter.CreateHistogram<double>("kvstore.duration", unit: "ms", description: "Duration of KV store operations in milliseconds.");

    private readonly ILogger<SqliteKVStore> _logger;
    private readonly SqliteRunner _sqliteRunner;

    /// <summary>
    /// Creates a new <see cref="SqliteKVStore"/>.
    /// </summary>
    /// <param name="sqliteRunner">The SQLite runner used to execute SQL statements.</param>
    /// <param name="logger">Optional logger.</param>
    public SqliteKVStore(
        SqliteRunner sqliteRunner,
        ILogger<SqliteKVStore>? logger = null)
    {
        this._sqliteRunner = sqliteRunner ?? throw new ArgumentNullException(nameof(sqliteRunner));
        this._logger = logger ?? NullLogger<SqliteKVStore>.Instance;
    }

    /// <summary>
    /// Creates the <c>kv_store</c> table and a key index if they do not already exist.
    /// </summary>
    /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
    public async Task InitializeSchemaAsync(CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("kvstore.initialize", ActivityKind.Client);
        activity?.SetTag("kvstore.operation", "initialize");

        var stopwatch = Stopwatch.StartNew();
        this._logger.LogDebug("Initializing SQLite KV store schema");

        try
        {
            await this._sqliteRunner.ExecuteAsync(
                """
                CREATE TABLE IF NOT EXISTS kv_store (
                    key        TEXT PRIMARY KEY,
                    value      TEXT NOT NULL,
                    metadata   TEXT,
                    updated_on TEXT DEFAULT (datetime('now'))
                )
                """,
                null,
                cancellationToken);

            await this._sqliteRunner.ExecuteAsync(
                "CREATE INDEX IF NOT EXISTS idx_kv_key ON kv_store (key)",
                null,
                cancellationToken);

            stopwatch.Stop();
            _operationCount.Add(1, new TagList { { "operation", "initialize" }, { "status", "success" } });
            _operationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "initialize" } });

            activity?.SetStatus(ActivityStatusCode.Ok);
            this._logger.LogDebug("SQLite KV store schema initialization completed in {ElapsedMs}ms", stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _operationCount.Add(1, new TagList { { "operation", "initialize" }, { "status", "error" } });
            _operationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "initialize" } });

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                { "exception.type", ex.GetType().FullName },
                { "exception.message", ex.Message },
                { "exception.stacktrace", ex.ToString() },
            }));

            this._logger.LogError(ex, "Error initializing SQLite KV store schema after {ElapsedMs}ms", stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SearchHit<TValue>>> SearchAsync<TValue>(Query query, CancellationToken cancellationToken = default)
    {
        if (query == null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        using var activity = _activitySource.StartActivity("kvstore.search", ActivityKind.Client);
        activity?.SetTag("kvstore.operation", "search");
        activity?.SetTag("kvstore.limit", query.Limit ?? 10);
        activity?.SetTag("kvstore.has_metadata_filter", query.MetadataFilter != null);

        var stopwatch = Stopwatch.StartNew();
        this._logger.LogDebug("Searching SQLite KV store with limit {Limit}, metadata filter: {HasFilter}", query.Limit ?? 10, query.MetadataFilter != null);

        try
        {
            // When a metadata filter is present we skip the SQL LIMIT so that C#-side filtering
            // can apply it after the containment check. LIMIT -1 means "no limit" in SQLite.
            int sqlLimit = query.MetadataFilter != null ? -1 : (query.Limit ?? 10);

            const string sql = """
                SELECT key, value, metadata, 0.0 AS distance, updated_on
                FROM kv_store
                WHERE (@hasKey   = 0 OR key = @k)
                  AND (@hasValue = 0 OR instr(value, @v) > 0)
                ORDER BY updated_on DESC
                LIMIT @l
                """;

            var parameters = new Dictionary<string, object?> { ["l"] = sqlLimit };

            if (string.IsNullOrWhiteSpace(query.Value) == false)
            {
                parameters["v"] = query.Value;
                parameters["hasValue"] = 1;
            }
            else
            {
                parameters["v"] = string.Empty;
                parameters["hasValue"] = 0;
            }

            if (string.IsNullOrWhiteSpace(query.Key) == false)
            {
                parameters["k"] = query.Key;
                parameters["hasKey"] = 1;
            }
            else
            {
                parameters["k"] = null;
                parameters["hasKey"] = 0;
            }

            List<SearchHit<TValue>> results = await this._sqliteRunner.QueryAsync<SearchHit<TValue>>(
                sql,
                reader => Task.FromResult(HydrateSearchHit<TValue>(reader)),
                parameters,
                cancellationToken);

            if (query.MetadataFilter != null)
            {
                results = results
                    .Where(r => MatchesMetadataFilter(r.Metadata, query.MetadataFilter))
                    .Take(query.Limit ?? 10)
                    .ToList();
            }

            stopwatch.Stop();
            _operationCount.Add(1, new TagList { { "operation", "search" }, { "status", "success" } });
            _operationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "search" } });

            activity?.SetTag("kvstore.result_count", results.Count);
            activity?.SetStatus(ActivityStatusCode.Ok);

            this._logger.LogDebug("SQLite KV store search completed in {ElapsedMs}ms. Results: {ResultCount}", stopwatch.Elapsed.TotalMilliseconds, results.Count);
            return results;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _operationCount.Add(1, new TagList { { "operation", "search" }, { "status", "error" } });
            _operationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "search" } });

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                { "exception.type", ex.GetType().FullName },
                { "exception.message", ex.Message },
                { "exception.stacktrace", ex.ToString() },
            }));

            this._logger.LogError(ex, "Error searching SQLite KV store after {ElapsedMs}ms", stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task UpsertAsync<TValue>(string key, TValue value, IDictionary<string, object>? metadata = null, CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("kvstore.upsert", ActivityKind.Client);
        activity?.SetTag("kvstore.operation", "upsert");
        activity?.SetTag("kvstore.key", key);
        activity?.SetTag("kvstore.has_metadata", metadata != null);

        var stopwatch = Stopwatch.StartNew();
        this._logger.LogDebug("Upserting SQLite KV store entry with key {Key}", key);

        try
        {
            string contentJson = JsonSerializer.Serialize(value);
            string? metadataJson = metadata != null ? JsonSerializer.Serialize(metadata) : null;

            const string upsertSql = """
                INSERT INTO kv_store (key, value, metadata)
                VALUES (@k, @v, @m)
                ON CONFLICT (key) DO UPDATE
                SET value      = excluded.value,
                    metadata   = excluded.metadata,
                    updated_on = datetime('now')
                """;

            await this._sqliteRunner.ExecuteAsync(
                upsertSql,
                new Dictionary<string, object?>
                {
                    ["k"] = key,
                    ["v"] = contentJson,
                    ["m"] = metadataJson
                },
                cancellationToken);

            stopwatch.Stop();
            _operationCount.Add(1, new TagList { { "operation", "upsert" }, { "status", "success" } });
            _operationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "upsert" } });

            activity?.SetStatus(ActivityStatusCode.Ok);
            this._logger.LogDebug("SQLite KV store upsert completed in {ElapsedMs}ms for key {Key}", stopwatch.Elapsed.TotalMilliseconds, key);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _operationCount.Add(1, new TagList { { "operation", "upsert" }, { "status", "error" } });
            _operationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "upsert" } });

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                { "exception.type", ex.GetType().FullName },
                { "exception.message", ex.Message },
                { "exception.stacktrace", ex.ToString() },
            }));

            this._logger.LogError(ex, "Error upserting SQLite KV store entry after {ElapsedMs}ms for key {Key}", stopwatch.Elapsed.TotalMilliseconds, key);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("kvstore.delete", ActivityKind.Client);
        activity?.SetTag("kvstore.operation", "delete");
        activity?.SetTag("kvstore.key", key);

        var stopwatch = Stopwatch.StartNew();
        this._logger.LogDebug("Deleting SQLite KV store entry with key {Key}", key);

        try
        {
            int rowsAffected = await this._sqliteRunner.ExecuteAsync(
                "DELETE FROM kv_store WHERE key = @k;",
                new Dictionary<string, object?> { ["k"] = key },
                cancellationToken);

            stopwatch.Stop();
            _operationCount.Add(1, new TagList { { "operation", "delete" }, { "status", "success" } });
            _operationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "delete" } });

            activity?.SetTag("kvstore.deleted", rowsAffected > 0);
            activity?.SetStatus(ActivityStatusCode.Ok);
            this._logger.LogDebug("SQLite KV store delete completed in {ElapsedMs}ms for key {Key}. Deleted: {Deleted}", stopwatch.Elapsed.TotalMilliseconds, key, rowsAffected > 0);
            return rowsAffected > 0;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _operationCount.Add(1, new TagList { { "operation", "delete" }, { "status", "error" } });
            _operationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "delete" } });

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                { "exception.type", ex.GetType().FullName },
                { "exception.message", ex.Message },
                { "exception.stacktrace", ex.ToString() },
            }));

            this._logger.LogError(ex, "Error deleting SQLite KV store entry after {ElapsedMs}ms for key {Key}", stopwatch.Elapsed.TotalMilliseconds, key);
            throw;
        }
    }

    private static SearchHit<TValue> HydrateSearchHit<TValue>(DbDataReader reader)
    {
        string key = reader.GetString(0);
        string valueJson = reader.GetString(1);
        string? metadataJson = reader.IsDBNull(2) ? null : reader.GetString(2);
        double distance = Convert.ToDouble(reader.GetValue(3));

        DateTimeOffset updatedOn = DateTimeOffset.UtcNow;
        if (!reader.IsDBNull(4))
        {
            string raw = reader.GetString(4);
            if (DateTime.TryParseExact(raw, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime dt))
            {
                updatedOn = new DateTimeOffset(dt, TimeSpan.Zero);
            }
        }

        return new SearchHit<TValue>(
            Key: key,
            Value: JsonSerializer.Deserialize<TValue>(valueJson)!,
            Metadata: JsonMetadataHelpers.DeserializeMetadata(metadataJson),
            Distance: distance,
            UpdatedOn: updatedOn);
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="metadata"/> satisfies every constraint in
    /// <paramref name="filter"/>.
    /// For array-valued filter entries (e.g. <c>{"tags": ["medical"]}</c>), each element in the filter
    /// array must appear somewhere in the corresponding metadata array (subset containment).
    /// For scalar entries, the values are compared as strings.
    /// </summary>
    /// <param name="metadata">The metadata dictionary from a search hit.</param>
    /// <param name="filter">The filter criteria to match against.</param>
    private static bool MatchesMetadataFilter(Dictionary<string, object>? metadata, IDictionary<string, object> filter)
    {
        if (metadata == null)
        {
            return false;
        }

        foreach (var (key, filterValue) in filter)
        {
            if (!metadata.TryGetValue(key, out object? metaValue))
            {
                return false;
            }

            if (!MetadataValueMatches(metaValue, filterValue))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks whether a single metadata value satisfies a filter value.
    /// Supports array containment (all filter elements must appear in the metadata collection)
    /// and scalar string equality.
    /// </summary>
    /// <param name="metaValue">The value from the stored metadata.</param>
    /// <param name="filterValue">The value specified in the filter.</param>
    private static bool MetadataValueMatches(object? metaValue, object filterValue)
    {
        // Array containment: every element in filterValue must appear in metaValue
        if (filterValue is System.Collections.IEnumerable filterSeq && filterValue is not string)
        {
            if (metaValue is not System.Collections.IEnumerable metaSeq || metaValue is string)
            {
                return false;
            }

            var metaSet = metaSeq.Cast<object?>()
                .Select(v => v?.ToString())
                .ToHashSet(StringComparer.Ordinal);

            return filterSeq.Cast<object?>().All(fv => metaSet.Contains(fv?.ToString()));
        }

        // Scalar equality
        return string.Equals(metaValue?.ToString(), filterValue?.ToString(), StringComparison.Ordinal);
    }
}
