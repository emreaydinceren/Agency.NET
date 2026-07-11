
using Agency.KeyValueStore.Common;
using Agency.Sql.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;

namespace Agency.KeyValueStore.Sql.Sqlite;
/// <summary>
/// An <see cref="IKVStore"/> backed by SQLite that stores key/value entries in a plain TEXT column and supports
/// substring-based value filtering via the SQLite <c> instr</c> function. Metadata filtering is applied in-process
/// after the SQL query because SQLite has no native JSONB containment operator. Results are ordered by recency (newest
/// first).
/// </summary>
public sealed partial class SqliteKVStore : IKVStore
{
    /// <summary>The activity source name used for KV store telemetry.</summary>
    public const string ActivitySourceName = "Agency.KeyValueStore.Sql.Sqlite";

    /// <summary>The meter name used for KV store telemetry.</summary>
    public const string MeterName = "Agency.KeyValueStore.Sql.Sqlite";

    private static readonly KvStoreTelemetry _telemetry = new(ActivitySourceName, MeterName);

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
    /// Creates the <c> kv_store</c> table and a key index if they do not already exist.
    /// </summary>
    /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
    public async Task InitializeSchemaAsync(CancellationToken cancellationToken = default)
    {
        using var activity = _telemetry.StartActivity("kvstore.initialize");
        activity?.SetTag("kvstore.operation", "initialize");
        KvStoreTelemetry.LogInitializingSchema(this._logger);

        await _telemetry.ExecuteAsync<int>(
            "initialize",
            activity,
            () => this._sqliteRunner.ExecuteAsync(
                """
                CREATE TABLE IF NOT EXISTS kv_store (
                    user_id    TEXT NOT NULL,
                    session_id TEXT NOT NULL,
                    key        TEXT NOT NULL,
                    value      TEXT NOT NULL,
                    metadata   TEXT,
                    updated_on TEXT DEFAULT (datetime('now')),
                    PRIMARY KEY (user_id, session_id, key)
                )
                """,
                null,
                cancellationToken),
            onSuccess: (_, elapsedMs) => KvStoreTelemetry.LogSchemaInitialized(this._logger, elapsedMs),
            onError: (ex, elapsedMs) => KvStoreTelemetry.LogErrorInitializingSchema(this._logger, ex, elapsedMs));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SearchHit<TValue>>> SearchAsync<TValue>(Query query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        using var activity = _telemetry.StartActivity("kvstore.search");
        activity?.SetTag("kvstore.operation", "search");
        activity?.SetTag("kvstore.limit", query.Limit ?? 10);
        activity?.SetTag("kvstore.has_metadata_filter", query.MetadataFilter != null);
        KvStoreTelemetry.LogSearching(this._logger, query.Limit ?? 10, query.MetadataFilter != null);

        return await _telemetry.ExecuteAsync<IReadOnlyList<SearchHit<TValue>>>(
            "search",
            activity,
            async () =>
            {
                // When a metadata filter is present we skip the SQL LIMIT so that C#-side filtering
                // can apply it after the containment check. LIMIT -1 means "no limit" in SQLite.
                int sqlLimit = query.MetadataFilter != null ? -1 : (query.Limit ?? 10);

                const string sql = """
                    SELECT session_id, key, value, metadata, updated_on
                    FROM kv_store
                    WHERE user_id = @uid
                      AND (@hasSessionId = 0 OR session_id = @sid)
                      AND (@hasKey       = 0 OR key = @k)
                      AND (@hasValue     = 0 OR instr(value, @v) > 0)
                    ORDER BY updated_on DESC
                    LIMIT @l
                    """;

                var parameters = new Dictionary<string, object?> { ["l"] = sqlLimit };

                parameters["uid"] = query.UserId;

                if (query.SessionId != null)
                {
                    parameters["sid"] = query.SessionId;
                    parameters["hasSessionId"] = 1;
                }
                else
                {
                    parameters["sid"] = string.Empty;
                    parameters["hasSessionId"] = 0;
                }

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

                activity?.SetTag("kvstore.result_count", results.Count);
                return results;
            },
            onSuccess: (results, elapsedMs) => KvStoreTelemetry.LogSearchCompleted(this._logger, elapsedMs, results.Count),
            onError: (ex, elapsedMs) => KvStoreTelemetry.LogErrorSearching(this._logger, ex, elapsedMs));
    }

    /// <inheritdoc/>
    public async Task UpsertAsync<TValue>(string userId, string? sessionId, string key, TValue value, IDictionary<string, object>? metadata = null, CancellationToken cancellationToken = default)
    {
        using var activity = _telemetry.StartActivity("kvstore.upsert");
        activity?.SetTag("kvstore.operation", "upsert");
        activity?.SetTag("kvstore.key", key);
        activity?.SetTag("kvstore.has_metadata", metadata != null);
        activity?.SetTag("kvstore.user_id", userId);
        KvStoreTelemetry.LogUpserting(this._logger, key, metadata != null);

        await _telemetry.ExecuteAsync<int>(
            "upsert",
            activity,
            () =>
            {
                string contentJson = JsonSerializer.Serialize(value);
                string? metadataJson = metadata != null ? JsonSerializer.Serialize(metadata) : null;

                const string upsertSql = """
                    INSERT INTO kv_store (user_id, session_id, key, value, metadata)
                    VALUES (@uid, @sid, @k, @v, @m)
                    ON CONFLICT (user_id, session_id, key) DO UPDATE
                    SET value      = excluded.value,
                        metadata   = excluded.metadata,
                        updated_on = datetime('now')
                    """;

                return this._sqliteRunner.ExecuteAsync(
                    upsertSql,
                    new Dictionary<string, object?>
                    {
                        ["uid"] = userId,
                        ["sid"] = KvStoreTelemetry.ResolveSessionId(sessionId),
                        ["k"] = key,
                        ["v"] = contentJson,
                        ["m"] = metadataJson
                    },
                    cancellationToken);
            },
            onSuccess: (_, elapsedMs) => KvStoreTelemetry.LogUpserted(this._logger, elapsedMs, key),
            onError: (ex, elapsedMs) => KvStoreTelemetry.LogErrorUpserting(this._logger, ex, elapsedMs, key));
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(string userId, string? sessionId, string key, CancellationToken cancellationToken = default)
    {
        using var activity = _telemetry.StartActivity("kvstore.delete");
        activity?.SetTag("kvstore.operation", "delete");
        activity?.SetTag("kvstore.key", key);
        KvStoreTelemetry.LogDeleting(this._logger, key);

        return await _telemetry.ExecuteAsync(
            "delete",
            activity,
            async () =>
            {
                int rowsAffected = await this._sqliteRunner.ExecuteAsync(
                    "DELETE FROM kv_store WHERE user_id = @uid AND session_id = @sid AND key = @k;",
                    new Dictionary<string, object?> { ["uid"] = userId, ["sid"] = KvStoreTelemetry.ResolveSessionId(sessionId), ["k"] = key },
                    cancellationToken);
                activity?.SetTag("kvstore.deleted", rowsAffected > 0);
                return rowsAffected > 0;
            },
            onSuccess: (deleted, elapsedMs) => KvStoreTelemetry.LogDeleted(this._logger, elapsedMs, key, deleted),
            onError: (ex, elapsedMs) => KvStoreTelemetry.LogErrorDeleting(this._logger, ex, elapsedMs, key));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SearchHit>> GetMetadataAsync(string userId, string? sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userId);

        using var activity = _telemetry.StartActivity("kvstore.getMetadata");
        activity?.SetTag("kvstore.operation", "getMetadata");

        return await _telemetry.ExecuteAsync<IReadOnlyList<SearchHit>>(
            "search",
            activity,
            async () =>
            {
                const string sql = """
                    SELECT session_id, key, metadata, updated_on
                    FROM kv_store
                    WHERE user_id = @uid
                      AND (@hasSessionId = 0 OR session_id = @sid)
                    ORDER BY updated_on DESC
                    """;

                var parameters = new Dictionary<string, object?> { ["uid"] = userId };

                if (sessionId != null)
                {
                    parameters["sid"] = sessionId;
                    parameters["hasSessionId"] = 1;
                }
                else
                {
                    parameters["sid"] = string.Empty;
                    parameters["hasSessionId"] = 0;
                }

                List<SearchHit> results = await this._sqliteRunner.QueryAsync<SearchHit>(
                    sql,
                    reader => Task.FromResult(HydrateSearchHit(reader)),
                    parameters,
                    cancellationToken);

                activity?.SetTag("kvstore.result_count", results.Count);
                return results;
            },
            onSuccess: (results, elapsedMs) => this.LogGetMetadataCompleted(elapsedMs, results.Count),
            onError: (ex, elapsedMs) => this.LogErrorGettingMetadata(ex, elapsedMs));
    }

    private static SearchHit<TValue> HydrateSearchHit<TValue>(DbDataReader reader)
    {
        // session_id, key, value, metadata, updated_on
        string? sessionId = reader.GetString(0) == KvStoreTelemetry.GlobalSession ? null : reader.GetString(0);
        string key = reader.GetString(1);
        string valueJson = reader.GetString(2);
        string? metadataJson = reader.IsDBNull(3) ? null : reader.GetString(3);

        return new SearchHit<TValue>(
            SessionId: sessionId,
            Key: key,
            Value: JsonSerializer.Deserialize<TValue>(valueJson)!,
            Metadata: JsonMetadataHelpers.DeserializeMetadata(metadataJson),
            UpdatedOn: GetUpdatedOn(reader, 4));
    }

    private static SearchHit HydrateSearchHit(DbDataReader reader)
    {
        // session_id, key, metadata, updated_on
        string? sessionId = reader.GetString(0) == KvStoreTelemetry.GlobalSession ? null : reader.GetString(0);
        string key = reader.GetString(1);
        string? metadataJson = reader.IsDBNull(2) ? null : reader.GetString(2);

        return new SearchHit(
            SessionId: sessionId,
            Key: key,
            Metadata: JsonMetadataHelpers.DeserializeMetadata(metadataJson),
            UpdatedOn: GetUpdatedOn(reader, 3));
    }

    private static DateTimeOffset GetUpdatedOn(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return DateTimeOffset.UtcNow;
        }
        string raw = reader.GetString(ordinal);
        if (DateTime.TryParseExact(raw, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime dt))
        {
            return new DateTimeOffset(dt, TimeSpan.Zero);
        }
        return DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="metadata"/> satisfies every constraint in
    /// <paramref name="filter"/>. For array-valued filter entries (e.g. <c> {"tags": ["medical"]}</c>), each element in
    /// the filter array must appear somewhere in the corresponding metadata array (subset containment). For scalar
    /// entries, the values are compared as strings.
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
    /// Checks whether a single metadata value satisfies a filter value. Supports array containment (all filter elements
    /// must appear in the metadata collection) and scalar string equality.
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

    /// <summary>Logs that a SQLite KV store metadata lookup completed.</summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "SQLite KV store metadata lookup completed in {ElapsedMs}ms. Results: {ResultCount}")]
    private partial void LogGetMetadataCompleted(double elapsedMs, int resultCount);

    /// <summary>Logs that getting metadata from the SQLite KV store failed.</summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "Error getting metadata from SQLite KV store after {ElapsedMs}ms")]
    private partial void LogErrorGettingMetadata(Exception ex, double elapsedMs);
}
