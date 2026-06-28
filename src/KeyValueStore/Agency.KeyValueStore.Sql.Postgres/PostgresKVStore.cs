
using Agency.KeyValueStore.Common;
using Agency.Sql.Postgres;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using System.Data.Common;
using System.Text.Json;

namespace Agency.KeyValueStore.Sql.Postgres;
/// <summary>
/// PostgreSQL-backed implementation of <see cref="IKVStore"/> that stores key-value entries without vector embeddings.
/// Results are ordered by recency (<c>updated_on DESC</c>) and substring matching is used for value search.
/// </summary>
public class PostgresKVStore : IKVStore
{
    /// <summary>
    /// The activity source name used for KV store telemetry.
    /// </summary>
    public const string ActivitySourceName = "Agency.KeyValueStore.Sql.Postgres";

    /// <summary>
    /// The meter name used for KV store telemetry.
    /// </summary>
    public const string MeterName = "Agency.KeyValueStore.Sql.Postgres";

    private static readonly KvStoreTelemetry _telemetry = new(ActivitySourceName, MeterName);

    private readonly ILogger<PostgresKVStore> _logger;

    private readonly PostgreSqlRunner _postgreSqlRunner;

    /// <summary>
    /// Initializes a new instance of <see cref="PostgresKVStore"/> with the given SQL runner and logger.
    /// </summary>
    /// <param name="postgreSqlRunner">The PostgreSQL runner used to execute queries. Cannot be null.</param>
    /// <param name="logger">The logger instance. If null, a no-op logger is used.</param>
    public PostgresKVStore(
        PostgreSqlRunner postgreSqlRunner,
        ILogger<PostgresKVStore> logger)
    {
        this._postgreSqlRunner = postgreSqlRunner ?? throw new ArgumentNullException(nameof(postgreSqlRunner));
        this._logger = logger ?? NullLogger<PostgresKVStore>.Instance;
    }

    /// <summary>
    /// Creates the <c> kv_store</c> table and supporting GIN index if they do not already exist.
    /// </summary>
    /// <param name="cancellationToken">A token to observe for cancellation requests.</param>
    public async Task InitializeSchemaAsync(CancellationToken cancellationToken = default)
    {
        using var activity = _telemetry.StartActivity("kvstore.initialize");
        activity?.SetTag("kvstore.operation", "initialize");
        this._logger.LogDebug("Initializing KV store schema");

        await _telemetry.ExecuteAsync<int>(
            "initialize",
            activity,
            () =>
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

                return this._postgreSqlRunner.ExecuteAsync(sql, null, cancellationToken);
            },
            onSuccess: (_, elapsedMs) => this._logger.LogDebug("KV store schema initialization completed in {ElapsedMs}ms", elapsedMs),
            onError: (ex, elapsedMs) => this._logger.LogError(ex, "Error initializing KV store schema after {ElapsedMs}ms", elapsedMs));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SearchHit<TValue>>> SearchAsync<TValue>(Query query, CancellationToken cancellationToken = default)
    {
        if (query == null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        using var activity = _telemetry.StartActivity("kvstore.search");
        activity?.SetTag("kvstore.operation", "search");
        activity?.SetTag("kvstore.limit", query.Limit ?? 10);
        activity?.SetTag("kvstore.has_metadata_filter", query.MetadataFilter != null);
        this._logger.LogDebug("Searching KV store with limit {Limit} and metadata filter present: {HasFilter}", query.Limit ?? 10, query.MetadataFilter != null);

        return await _telemetry.ExecuteAsync<IReadOnlyList<SearchHit<TValue>>>(
            "search",
            activity,
            async () =>
            {
                const string sql = @"
                SELECT session_id, key, value, metadata, updated_on
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

                activity?.SetTag("kvstore.result_count", results.Count);
                return results;
            },
            onSuccess: (results, elapsedMs) => this._logger.LogDebug("KV store search completed in {ElapsedMs}ms. Results returned: {ResultCount}", elapsedMs, results.Count),
            onError: (ex, elapsedMs) => this._logger.LogError(ex, "Error searching KV store after {ElapsedMs}ms", elapsedMs));
    }

    /// <inheritdoc/>
    public async Task UpsertAsync<TValue>(string userId, string? sessionId, string key, TValue value, IDictionary<string, object>? metadata = null, CancellationToken cancellationToken = default)
    {
        using var activity = _telemetry.StartActivity("kvstore.upsert");
        activity?.SetTag("kvstore.operation", "upsert");
        activity?.SetTag("kvstore.key", key);
        activity?.SetTag("kvstore.has_metadata", metadata != null);
        this._logger.LogDebug("Upserting KV store entry with key {Key} and metadata present: {HasMetadata}", key, metadata != null);

        await _telemetry.ExecuteAsync<int>(
            "upsert",
            activity,
            () =>
            {
                string contentJson = JsonSerializer.Serialize(value);

                const string sql = @"
                INSERT INTO kv_store (user_id, session_id, key, value, metadata)
                VALUES (@uid, @sid, @k, @v, @m)
                ON CONFLICT (user_id, session_id, key) DO UPDATE
                SET value      = EXCLUDED.value,
                    metadata   = EXCLUDED.metadata,
                    updated_on = NOW();";

                return this._postgreSqlRunner.ExecuteAsync(
                    sql,
                    new Dictionary<string, object?>
                    {
                        ["uid"] = userId,
                        ["sid"] = KvStoreTelemetry.ResolveSessionId(sessionId),
                        ["k"] = key,
                        ["v"] = new NpgsqlParameter("v", NpgsqlDbType.Jsonb) { Value = contentJson },
                        ["m"] = metadata != null ? new NpgsqlParameter("m", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(metadata) } : DBNull.Value
                    },
                    cancellationToken);
            },
            onSuccess: (_, elapsedMs) => this._logger.LogDebug("KV store upsert completed in {ElapsedMs}ms for key {Key}", elapsedMs, key),
            onError: (ex, elapsedMs) => this._logger.LogError(ex, "Error upserting KV store entry after {ElapsedMs}ms for key {Key}", elapsedMs, key));
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(string userId, string? sessionId, string key, CancellationToken cancellationToken = default)
    {
        using var activity = _telemetry.StartActivity("kvstore.delete");
        activity?.SetTag("kvstore.operation", "delete");
        activity?.SetTag("kvstore.key", key);
        this._logger.LogDebug("Deleting KV store entry with key {Key}", key);

        return await _telemetry.ExecuteAsync(
            "delete",
            activity,
            async () =>
            {
                int rowsAffected = await this._postgreSqlRunner.ExecuteAsync(
                    "DELETE FROM kv_store WHERE user_id = @uid AND session_id = @sid AND key = @k;",
                    new Dictionary<string, object?> { ["uid"] = userId, ["sid"] = KvStoreTelemetry.ResolveSessionId(sessionId), ["k"] = key },
                    cancellationToken);
                activity?.SetTag("kvstore.deleted", rowsAffected > 0);
                return rowsAffected > 0;
            },
            onSuccess: (deleted, elapsedMs) => this._logger.LogDebug("KV store delete completed in {ElapsedMs}ms for key {Key}. Deleted: {Deleted}", elapsedMs, key, deleted),
            onError: (ex, elapsedMs) => this._logger.LogError(ex, "Error deleting KV store entry after {ElapsedMs}ms for key {Key}", elapsedMs, key));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SearchHit>> GetMetadataAsync(string userId, string? sessionId, CancellationToken cancellationToken = default)
    {
        using var activity = _telemetry.StartActivity("kvstore.getMetadata");
        activity?.SetTag("kvstore.operation", "getMetadata");

        return await _telemetry.ExecuteAsync<IReadOnlyList<SearchHit>>(
            "search",
            activity,
            async () =>
            {
                const string sql =
                    @"SELECT session_id, key, metadata, updated_on
                FROM kv_store
                    WHERE user_id = @uid
                      AND(@hasSessionId = FALSE OR session_id = @sid)
                    ORDER BY updated_on DESC";

                var parameters = new Dictionary<string, object?>
                {
                    ["uid"] = userId
                };

                if (sessionId != null)
                {
                    parameters["sid"] = sessionId;
                    parameters["hasSessionId"] = true;
                }
                else
                {
                    parameters["sid"] = DBNull.Value;
                    parameters["hasSessionId"] = false;
                }

                var results = await this._postgreSqlRunner.QueryAsync<SearchHit>(
                    sql,
                    async (reader) => await HydrateSearchHitAsync(reader),
                    parameters,
                    cancellationToken);

                activity?.SetTag("kvstore.result_count", results.Count);
                return results;
            },
            onSuccess: (results, elapsedMs) => this._logger.LogDebug("KV store search completed in {ElapsedMs}ms. Results returned: {ResultCount}", elapsedMs, results.Count),
            onError: (ex, elapsedMs) => this._logger.LogError(ex, "Error searching KV store after {ElapsedMs}ms", elapsedMs));
    }

    private static async Task<SearchHit<TValue>> HydrateSearchHitAsync<TValue>(DbDataReader reader)
    {
        //  session_id, key, value, metadata, updated_on

        string rawSession = reader.GetString(0);
        string? sessionId = rawSession == KvStoreTelemetry.GlobalSession ? null : rawSession;

        string? valueJson = reader.GetValue(2) switch
        {
            null => null,
            DBNull => null,
            JsonDocument document => document.RootElement.GetRawText(),
            JsonElement element => element.GetRawText(),
            _ => reader.GetValue(2)?.ToString()
        };

        string? metadataJson = reader.GetValue(3) switch
        {
            null => null,
            DBNull => null,
            JsonDocument document => document.RootElement.GetRawText(),
            JsonElement element => element.GetRawText(),
            _ => reader.GetValue(3)?.ToString()
        };

        return await Task.FromResult(new SearchHit<TValue>(
            SessionId: sessionId,
            Key: reader.GetString(1),
            Value: string.IsNullOrWhiteSpace(valueJson) ? default! : JsonSerializer.Deserialize<TValue>(valueJson)!,
            Metadata: JsonMetadataHelpers.DeserializeMetadata(metadataJson),
            UpdatedOn: GetUpdatedOn(reader, 4)
        ));
    }

    private static async Task<SearchHit> HydrateSearchHitAsync(DbDataReader reader)
    {
        //  session_id, key, metadata, updated_on

        string rawSession = reader.GetString(0);
        string? sessionId = rawSession == KvStoreTelemetry.GlobalSession ? null : rawSession;

        string? metadataJson = reader.GetValue(2) switch
        {
            null => null,
            DBNull => null,
            JsonDocument document => document.RootElement.GetRawText(),
            JsonElement element => element.GetRawText(),
            _ => reader.GetValue(2)?.ToString()
        };

        return await Task.FromResult(new SearchHit(
            SessionId: sessionId,
            Key: reader.GetString(1),
            Metadata: JsonMetadataHelpers.DeserializeMetadata(metadataJson),
            UpdatedOn: GetUpdatedOn(reader, 3)
        ));
    }

    private static DateTimeOffset GetUpdatedOn(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return DateTimeOffset.UtcNow;
        }
        DateTime dt = reader.GetDateTime(ordinal);
        return new DateTimeOffset(dt, TimeSpan.Zero);
    }
}
