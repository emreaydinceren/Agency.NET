namespace Agency.KeyValueStore.Sql.Postgre;

using Agency.KeyValueStore.Common;
using Agency.Sql.Postgre;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;

/// <summary>
/// PostgreSQL-backed implementation of <see cref="IKVStore"/> that stores key-value entries without vector embeddings.
/// Results are ordered by recency (<c>updated_on DESC</c>) and substring matching is used for value search.
/// </summary>
public class PostgreKVStore : IKVStore
{
    /// <summary>
    /// The activity source name used for KV store telemetry.
    /// </summary>
    public const string ActivitySourceName = "Agency.KeyValueStore.Sql.Postgre";

    /// <summary>
    /// The meter name used for KV store telemetry.
    /// </summary>
    public const string MeterName = "Agency.KeyValueStore.Sql.Postgre";

    private static readonly ActivitySource _activitySource = new(ActivitySourceName);
    private static readonly Meter _meter = new(MeterName);

    private static readonly Counter<long> _operationCount =
        _meter.CreateCounter<long>("kvstore.operations", unit: "{operation}", description: "Total number of KV store operations executed.");

    private static readonly Histogram<double> _operationDuration =
        _meter.CreateHistogram<double>("kvstore.duration", unit: "ms", description: "Duration of KV store operations in milliseconds.");

    private readonly ILogger<PostgreKVStore> _logger;

    private readonly PostgreSqlRunner _postgreSqlRunner;

    private const string GlobalSession = "*";
    private static string ResolveSessionId(string? sessionId) => sessionId ?? GlobalSession;

    /// <summary>
    /// Initializes a new instance of <see cref="PostgreKVStore"/> with the given SQL runner and logger.
    /// </summary>
    /// <param name="postgreSqlRunner">The PostgreSQL runner used to execute queries. Cannot be null.</param>
    /// <param name="logger">The logger instance. If null, a no-op logger is used.</param>
    public PostgreKVStore(
        PostgreSqlRunner postgreSqlRunner,
        ILogger<PostgreKVStore> logger)
    {
        this._postgreSqlRunner = postgreSqlRunner ?? throw new ArgumentNullException(nameof(postgreSqlRunner));
        this._logger = logger ?? NullLogger<PostgreKVStore>.Instance;
    }

    /// <summary>
    /// Creates the <c>kv_store</c> table and supporting GIN index if they do not already exist.
    /// </summary>
    /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
    public async Task InitializeSchemaAsync(CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("kvstore.initialize", ActivityKind.Client);
        activity?.SetTag("kvstore.operation", "initialize");

        var stopwatch = Stopwatch.StartNew();
        this._logger.LogDebug("Initializing KV store schema");

        try
        {
            const string sql = @"
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'kv_store')
                   AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'kv_store' AND column_name = 'user_id') THEN
                    DROP TABLE kv_store CASCADE;
                END IF;
            END $$;

            CREATE TABLE IF NOT EXISTS kv_store (
                user_id    TEXT NOT NULL,
                session_id TEXT NOT NULL,
                key        TEXT NOT NULL,
                value      JSONB NOT NULL,
                metadata   JSONB,
                updated_on TIMESTAMPTZ DEFAULT NOW(),
                PRIMARY KEY (user_id, session_id, key)
            );

            CREATE INDEX IF NOT EXISTS idx_kv_store_metadata
            ON kv_store USING GIN (metadata);
            ";

            await this._postgreSqlRunner.ExecuteAsync(sql, null, cancellationToken);

            stopwatch.Stop();
            _operationCount.Add(1, new TagList { { "operation", "initialize" }, { "status", "success" } });
            _operationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "initialize" } });

            activity?.SetStatus(ActivityStatusCode.Ok);
            this._logger.LogDebug("KV store schema initialization completed in {ElapsedMs}ms", stopwatch.Elapsed.TotalMilliseconds);
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

            this._logger.LogError(ex, "Error initializing KV store schema after {ElapsedMs}ms", stopwatch.Elapsed.TotalMilliseconds);
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
        this._logger.LogDebug("Searching KV store with limit {Limit} and metadata filter present: {HasFilter}", query.Limit ?? 10, query.MetadataFilter != null);

        try
        {
            const string sql = @"
            SELECT user_id, session_id, key, value, metadata, 0.0::double precision AS distance, updated_on
            FROM kv_store
            WHERE user_id = @uid
              AND (@hasSessionId = FALSE OR session_id = @sid)
              AND (@hasKey       = FALSE OR key = @k)
              AND (@hasFilter    = FALSE OR metadata @> @mFilter)
              AND (@hasValue     = FALSE OR value::text ILIKE @vLike)
            ORDER BY updated_on DESC
            LIMIT @l;";

            var parameters = new Dictionary<string, object?>
            {
                ["l"] = query.Limit ?? 10
            };

            parameters["uid"] = query.UserId;

            if (query.SessionId != null)
            {
                parameters["sid"] = query.SessionId;
                parameters["hasSessionId"] = true;
            }
            else
            {
                parameters["sid"] = DBNull.Value;
                parameters["hasSessionId"] = false;
            }

            // Substring value search (optional)
            if (string.IsNullOrWhiteSpace(query.Value) == false)
            {
                parameters["hasValue"] = true;
                parameters["vLike"] = "%" + query.Value + "%";
            }
            else
            {
                parameters["hasValue"] = false;
                parameters["vLike"] = string.Empty;
            }

            // Exact key match (optional)
            if (string.IsNullOrWhiteSpace(query.Key) == false)
            {
                parameters["k"] = query.Key;
                parameters["hasKey"] = true;
            }
            else
            {
                parameters["k"] = DBNull.Value;
                parameters["hasKey"] = false;
            }

            if (query.MetadataFilter != null)
            {
                parameters["mFilter"] = new NpgsqlParameter("mFilter", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(query.MetadataFilter) };
                parameters["hasFilter"] = true;
            }
            else
            {
                parameters["mFilter"] = DBNull.Value;
                parameters["hasFilter"] = false;
            }

            var results = await this._postgreSqlRunner.QueryAsync<SearchHit<TValue>>(
                sql,
                async (reader) => await HydrateSearchHitAsync<TValue>(reader),
                parameters,
                cancellationToken);

            stopwatch.Stop();
            _operationCount.Add(1, new TagList { { "operation", "search" }, { "status", "success" } });
            _operationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "search" } });

            activity?.SetTag("kvstore.result_count", results.Count);
            activity?.SetStatus(ActivityStatusCode.Ok);

            this._logger.LogDebug("KV store search completed in {ElapsedMs}ms. Results returned: {ResultCount}", stopwatch.Elapsed.TotalMilliseconds, results.Count);
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

            this._logger.LogError(ex, "Error searching KV store after {ElapsedMs}ms", stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task UpsertAsync<TValue>(string userId, string? sessionId, string key, TValue value, IDictionary<string, object>? metadata = null, CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("kvstore.upsert", ActivityKind.Client);
        activity?.SetTag("kvstore.operation", "upsert");
        activity?.SetTag("kvstore.key", key);
        activity?.SetTag("kvstore.has_metadata", metadata != null);

        var stopwatch = Stopwatch.StartNew();
        this._logger.LogDebug("Upserting KV store entry with key {Key} and metadata present: {HasMetadata}", key, metadata != null);

        try
        {
            string contentJson = JsonSerializer.Serialize(value);

            const string sql = @"
            INSERT INTO kv_store (user_id, session_id, key, value, metadata)
            VALUES (@uid, @sid, @k, @v, @m)
            ON CONFLICT (user_id, session_id, key) DO UPDATE
            SET value      = EXCLUDED.value,
                metadata   = EXCLUDED.metadata,
                updated_on = NOW();";

            await this._postgreSqlRunner.ExecuteAsync(
                sql,
                new Dictionary<string, object?>
                {
                    ["uid"] = userId,
                    ["sid"] = ResolveSessionId(sessionId),
                    ["k"] = key,
                    ["v"] = new NpgsqlParameter("v", NpgsqlDbType.Jsonb) { Value = contentJson },
                    ["m"] = metadata != null ? new NpgsqlParameter("m", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(metadata) } : DBNull.Value
                },
                cancellationToken);

            stopwatch.Stop();
            _operationCount.Add(1, new TagList { { "operation", "upsert" }, { "status", "success" } });
            _operationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "upsert" } });

            activity?.SetStatus(ActivityStatusCode.Ok);
            this._logger.LogDebug("KV store upsert completed in {ElapsedMs}ms for key {Key}", stopwatch.Elapsed.TotalMilliseconds, key);
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

            this._logger.LogError(ex, "Error upserting KV store entry after {ElapsedMs}ms for key {Key}", stopwatch.Elapsed.TotalMilliseconds, key);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(string userId, string? sessionId, string key, CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("kvstore.delete", ActivityKind.Client);
        activity?.SetTag("kvstore.operation", "delete");
        activity?.SetTag("kvstore.key", key);

        var stopwatch = Stopwatch.StartNew();
        this._logger.LogDebug("Deleting KV store entry with key {Key}", key);

        try
        {
            int rowsAffected = await this._postgreSqlRunner.ExecuteAsync(
                "DELETE FROM kv_store WHERE user_id = @uid AND session_id = @sid AND key = @k;",
                new Dictionary<string, object?> { ["uid"] = userId, ["sid"] = ResolveSessionId(sessionId), ["k"] = key },
                cancellationToken);

            stopwatch.Stop();
            _operationCount.Add(1, new TagList { { "operation", "delete" }, { "status", "success" } });
            _operationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "delete" } });

            activity?.SetTag("kvstore.deleted", rowsAffected > 0);
            activity?.SetStatus(ActivityStatusCode.Ok);
            this._logger.LogDebug("KV store delete completed in {ElapsedMs}ms for key {Key}. Deleted: {Deleted}", stopwatch.Elapsed.TotalMilliseconds, key, rowsAffected > 0);
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

            this._logger.LogError(ex, "Error deleting KV store entry after {ElapsedMs}ms for key {Key}", stopwatch.Elapsed.TotalMilliseconds, key);
            throw;
        }
    }

    private static async Task<SearchHit<TValue>> HydrateSearchHitAsync<TValue>(DbDataReader reader)
    {
        string userId = reader.GetString(0);
        string rawSession = reader.GetString(1);
        string? sessionId = rawSession == GlobalSession ? null : rawSession;

        string? valueJson = reader.GetValue(3) switch
        {
            null => null,
            DBNull => null,
            JsonDocument document => document.RootElement.GetRawText(),
            JsonElement element => element.GetRawText(),
            _ => reader.GetValue(3)?.ToString()
        };

        string? metadataJson = reader.GetValue(4) switch
        {
            null => null,
            DBNull => null,
            JsonDocument document => document.RootElement.GetRawText(),
            JsonElement element => element.GetRawText(),
            _ => reader.GetValue(4)?.ToString()
        };

        return await Task.FromResult(new SearchHit<TValue>(
            UserId: userId,
            SessionId: sessionId,
            Key: reader.GetString(2),
            Value: string.IsNullOrWhiteSpace(valueJson) ? default! : JsonSerializer.Deserialize<TValue>(valueJson)!,
            Metadata: JsonMetadataHelpers.DeserializeMetadata(metadataJson),
            Distance: reader.GetDouble(5),
            UpdatedOn: reader.IsDBNull(6) ? DateTimeOffset.UtcNow : new DateTimeOffset(reader.GetDateTime(6), TimeSpan.Zero)
        ));
    }
}
