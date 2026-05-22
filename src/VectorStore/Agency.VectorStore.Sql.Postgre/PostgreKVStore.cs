namespace Agency.VectorStore.Sql.Postgre;

using Agency.Embeddings.Common;
using Agency.Sql.Postgres;
using Agency.VectorStore.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;

public class PostgreKVStore : IVectorStore
{
    /// <summary>
    /// The activity source name used for vector store telemetry.
    /// </summary>
    public const string ActivitySourceName = "Agency.VectorStore.Sql.Postgre";

    /// <summary>
    /// The meter name used for vector store telemetry.
    /// </summary>
    public const string MeterName = "Agency.VectorStore.Sql.Postgre";

    private static readonly ActivitySource _activitySource = new(ActivitySourceName);
    private static readonly Meter _meter = new(MeterName);

    private static readonly Counter<long> _operationCount =
        _meter.CreateCounter<long>("vectorstore.operations", unit: "{operation}", description: "Total number of vector store operations executed.");

    private static readonly Histogram<double> _operationDuration =
        _meter.CreateHistogram<double>("vectorstore.duration", unit: "ms", description: "Duration of vector store operations in milliseconds.");

    private readonly ILogger<PostgreKVStore> _logger;

    private readonly IEmbeddingGenerator _embeddingGenerator;

    private readonly PostgreSqlRunner _postgreSqlRunner;

    private const string GlobalSession = "*";

    private static string ResolveSessionId(string? sessionId) => sessionId ?? GlobalSession;

    public PostgreKVStore(
        IEmbeddingGenerator embeddingGenerator,
        PostgreSqlRunner postgreSqlRunner,
        ILogger<PostgreKVStore> logger)
    {
        this._embeddingGenerator = embeddingGenerator ?? throw new ArgumentNullException(nameof(embeddingGenerator));
        this._postgreSqlRunner = postgreSqlRunner ?? throw new ArgumentNullException(nameof(postgreSqlRunner));
        this._logger = logger ?? NullLogger<PostgreKVStore>.Instance;
    }

    /// <summary>
    /// Configures the PostgreSQL database with the pgvector extension and optimized indexes.
    /// </summary>
    public async Task InitializeSchemaAsync(int dimensions = 1536, CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("vectorstore.initialize", ActivityKind.Client);
        activity?.SetTag("vectorstore.operation", "initialize");
        activity?.SetTag("vectorstore.dimensions", dimensions);

        var stopwatch = Stopwatch.StartNew();
        this._logger.LogDebug("Initializing vector store schema with {Dimensions} dimensions", dimensions);

        try
        {
            string sql = $@"
            CREATE EXTENSION IF NOT EXISTS vector;

            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'semantic_kv_store')
                   AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'semantic_kv_store' AND column_name = 'user_id') THEN
                    DROP TABLE semantic_kv_store CASCADE;
                END IF;
            END $$;

            CREATE TABLE IF NOT EXISTS semantic_kv_store (
                user_id    TEXT NOT NULL,
                session_id TEXT NOT NULL,
                key        TEXT NOT NULL,
                value      JSONB NOT NULL,
                embedding  vector({dimensions}) NOT NULL,
                metadata   JSONB,
                updated_on TIMESTAMPTZ DEFAULT NOW(),
                PRIMARY KEY (user_id, session_id, key)
            );

            CREATE INDEX IF NOT EXISTS idx_vector_search
            ON semantic_kv_store USING hnsw (embedding vector_cosine_ops);

            CREATE INDEX IF NOT EXISTS idx_metadata_filter
            ON semantic_kv_store USING GIN (metadata);
        ";

            await this._postgreSqlRunner.ExecuteAsync(sql, null, cancellationToken);

            stopwatch.Stop();
            _operationCount.Add(1, new TagList { { "operation", "initialize" }, { "status", "success" } });
            _operationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "initialize" } });

            activity?.SetStatus(ActivityStatusCode.Ok);
            this._logger.LogDebug("Vector store schema initialization completed in {ElapsedMs}ms", stopwatch.Elapsed.TotalMilliseconds);
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

            this._logger.LogError(ex, "Error initializing vector store schema after {ElapsedMs}ms", stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    public async Task<IReadOnlyList<SearchHit<TValue>>> SearchAsync<TValue>(Query query, CancellationToken cancellationToken = default)
    {
        if (query == null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        using var activity = _activitySource.StartActivity("vectorstore.search", ActivityKind.Client);
        activity?.SetTag("vectorstore.operation", "search");
        activity?.SetTag("vectorstore.limit", query.Limit ?? 10);
        activity?.SetTag("vectorstore.has_metadata_filter", query.MetadataFilter != null);

        var stopwatch = Stopwatch.StartNew();
        this._logger.LogDebug("Searching vector store with limit {Limit} and metadata filter present: {HasFilter}", query.Limit ?? 10, query.MetadataFilter != null);

        try
        {
            // SQL explanation:
            // 1. @> is the JSONB containment operator (checks if metadata contains the filter).
            // 2. <=> is the Cosine Distance operator for the vector column.
            // 3. When @qVector is NULL, skip vector distance calculation (returning 0.0 as placeholder)
            // 4. Cast @qVector to vector type to handle NULL case properly
            const string sql = @"
            SELECT user_id, session_id, key, value, metadata,
                   CASE WHEN @qVector::vector IS NULL THEN 0.0 ELSE (embedding <=> @qVector::vector) END AS distance,
                   updated_on
            FROM semantic_kv_store
            WHERE user_id = @uid
              AND (@hasSessionId = FALSE OR session_id = @sid)
              AND (@hasKey       = FALSE OR key = @k)
              AND (@hasFilter    = FALSE OR metadata @> @mFilter)
            ORDER BY distance ASC
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

            // Vector search on query.Value
            if (string.IsNullOrWhiteSpace(query.Value) == false)
            {
                var embedding = await this._embeddingGenerator.GenerateEmbeddingAsync(query.Value, cancellationToken);
                parameters["qVector"] = new Pgvector.Vector(embedding.ToArray());
            }
            else
            {
                parameters["qVector"] = DBNull.Value;
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

            activity?.SetTag("vectorstore.result_count", results.Count);
            activity?.SetStatus(ActivityStatusCode.Ok);

            this._logger.LogDebug("Vector store search completed in {ElapsedMs}ms. Results returned: {ResultCount}", stopwatch.Elapsed.TotalMilliseconds, results.Count);
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

            this._logger.LogError(ex, "Error searching vector store after {ElapsedMs}ms", stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    public async Task UpsertAsync<TValue>(string userId, string? sessionId, string key, TValue value, IDictionary<string, object>? metadata = null, CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("vectorstore.upsert", ActivityKind.Client);
        activity?.SetTag("vectorstore.operation", "upsert");
        activity?.SetTag("vectorstore.user_id", userId);
        activity?.SetTag("vectorstore.session_id", sessionId ?? "global");
        activity?.SetTag("vectorstore.key", key);
        activity?.SetTag("vectorstore.has_metadata", metadata != null);

        var stopwatch = Stopwatch.StartNew();
        this._logger.LogDebug("Upserting vector store entry for user {UserId} session {SessionId} key {Key} with metadata present: {HasMetadata}", userId, sessionId ?? "global", key, metadata != null);

        try
        {
            string contentToEmbed = JsonSerializer.Serialize(value);
            var vectorArray = await this._embeddingGenerator.GenerateEmbeddingAsync(contentToEmbed, cancellationToken);
            string vectorLiteral = $"[{string.Join(',', vectorArray.ToArray().Select(v => v.ToString(CultureInfo.InvariantCulture)))}]";

            // Use EXCLUDED to update existing records on compound key conflict
            const string query = @"
            INSERT INTO semantic_kv_store (user_id, session_id, key, value, embedding, metadata)
            VALUES (@uid, @sid, @k, @v, @e::vector, @m)
            ON CONFLICT (user_id, session_id, key) DO UPDATE
            SET value     = EXCLUDED.value,
                embedding = EXCLUDED.embedding,
                metadata  = EXCLUDED.metadata;";

            await this._postgreSqlRunner.ExecuteAsync(
                query,
                new Dictionary<string, object?>
                {
                    ["uid"] = userId,
                    ["sid"] = ResolveSessionId(sessionId),
                    ["k"] = key,
                    ["v"] = new NpgsqlParameter("v", NpgsqlDbType.Jsonb) { Value = contentToEmbed },
                    ["e"] = vectorLiteral,
                    ["m"] = metadata != null ? new NpgsqlParameter("m", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(metadata) } : DBNull.Value
                },
                cancellationToken);

            stopwatch.Stop();
            _operationCount.Add(1, new TagList { { "operation", "upsert" }, { "status", "success" } });
            _operationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "upsert" } });

            activity?.SetStatus(ActivityStatusCode.Ok);
            this._logger.LogDebug("Vector store upsert completed in {ElapsedMs}ms for user {UserId} session {SessionId} key {Key}", stopwatch.Elapsed.TotalMilliseconds, userId, sessionId ?? "global", key);
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

            this._logger.LogError(ex, "Error upserting vector store entry after {ElapsedMs}ms for user {UserId} session {SessionId} key {Key}", stopwatch.Elapsed.TotalMilliseconds, userId, sessionId ?? "global", key);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(string userId, string? sessionId, string key, CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("vectorstore.delete", ActivityKind.Client);
        activity?.SetTag("vectorstore.operation", "delete");
        activity?.SetTag("vectorstore.user_id", userId);
        activity?.SetTag("vectorstore.session_id", sessionId ?? "global");
        activity?.SetTag("vectorstore.key", key);

        var stopwatch = Stopwatch.StartNew();
        this._logger.LogDebug("Deleting vector store entry for user {UserId} session {SessionId} key {Key}", userId, sessionId ?? "global", key);

        try
        {
            int rowsAffected = await this._postgreSqlRunner.ExecuteAsync(
                "DELETE FROM semantic_kv_store WHERE user_id = @uid AND session_id = @sid AND key = @k;",
                new Dictionary<string, object?> { ["uid"] = userId, ["sid"] = ResolveSessionId(sessionId), ["k"] = key },
                cancellationToken);

            stopwatch.Stop();
            _operationCount.Add(1, new TagList { { "operation", "delete" }, { "status", "success" } });
            _operationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "delete" } });

            activity?.SetTag("vectorstore.deleted", rowsAffected > 0);
            activity?.SetStatus(ActivityStatusCode.Ok);
            this._logger.LogDebug("Vector store delete completed in {ElapsedMs}ms for user {UserId} session {SessionId} key {Key}. Deleted: {Deleted}", stopwatch.Elapsed.TotalMilliseconds, userId, sessionId ?? "global", key, rowsAffected > 0);
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

            this._logger.LogError(ex, "Error deleting vector store entry after {ElapsedMs}ms for user {UserId} session {SessionId} key {Key}", stopwatch.Elapsed.TotalMilliseconds, userId, sessionId ?? "global", key);
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

        return new SearchHit<TValue>(
            UserId: userId,
            SessionId: sessionId,
            Key: reader.GetString(2),
            Value: string.IsNullOrWhiteSpace(valueJson) ? default! : JsonSerializer.Deserialize<TValue>(valueJson)!,
            Metadata: JsonMetadataHelpers.DeserializeMetadata(metadataJson),
            Distance: reader.GetDouble(5),
            UpdatedOn: reader.IsDBNull(6) ? DateTimeOffset.UtcNow : new DateTimeOffset(reader.GetDateTime(6), TimeSpan.Zero)
        );
    }
}
