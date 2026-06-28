
using Agency.Embeddings.Common;
using Agency.Sql.Postgres;
using Agency.VectorStore.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;

namespace Agency.VectorStore.Sql.Postgres;
public class PostgresKVStore : IVectorStore
{
    /// <summary>
    /// The activity source name used for vector store telemetry.
    /// </summary>
    public const string ActivitySourceName = "Agency.VectorStore.Sql.Postgres";

    /// <summary>
    /// The meter name used for vector store telemetry.
    /// </summary>
    public const string MeterName = "Agency.VectorStore.Sql.Postgres";

    private static readonly VectorStoreTelemetry _telemetry = new(ActivitySourceName, MeterName);

    private readonly ILogger<PostgresKVStore> _logger;

    private readonly IEmbeddingGenerator _embeddingGenerator;

    private readonly PostgreSqlRunner _postgreSqlRunner;

    public PostgresKVStore(
        IEmbeddingGenerator embeddingGenerator,
        PostgreSqlRunner postgreSqlRunner,
        ILogger<PostgresKVStore> logger)
    {
        this._embeddingGenerator = embeddingGenerator ?? throw new ArgumentNullException(nameof(embeddingGenerator));
        this._postgreSqlRunner = postgreSqlRunner ?? throw new ArgumentNullException(nameof(postgreSqlRunner));
        this._logger = logger ?? NullLogger<PostgresKVStore>.Instance;
    }

    /// <summary>
    /// Configures the PostgreSQL database with the pgvector extension and optimized indexes.
    /// </summary>
    public async Task InitializeSchemaAsync(int dimensions = 1536, CancellationToken cancellationToken = default)
    {
        using var activity = _telemetry.StartActivity("vectorstore.initialize");
        activity?.SetTag("vectorstore.operation", "initialize");
        activity?.SetTag("vectorstore.dimensions", dimensions);
        this._logger.LogDebug("Initializing vector store schema with {Dimensions} dimensions", dimensions);

        await _telemetry.ExecuteAsync<int>(
            "initialize",
            activity,
            () =>
            {
                string sql = $@"
                CREATE EXTENSION IF NOT EXISTS vector;

                DROP TABLE IF EXISTS semantic_kv_store CASCADE;

                CREATE TABLE IF NOT EXISTS semantic_kv_store (
                    user_id    TEXT        NOT NULL,
                    session_id TEXT        NOT NULL,
                    project_id TEXT        NOT NULL DEFAULT '*',
                    key        TEXT        NOT NULL,
                    value      JSONB,
                    embedding  vector({dimensions}),
                    metadata   JSONB,
                    updated_on TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    PRIMARY KEY (user_id, session_id, project_id, key)
                );

                CREATE INDEX IF NOT EXISTS semantic_kv_store_embedding_idx
                    ON semantic_kv_store USING hnsw (embedding vector_cosine_ops);

                CREATE INDEX IF NOT EXISTS semantic_kv_store_metadata_idx
                    ON semantic_kv_store USING gin (metadata);
            ";

                return this._postgreSqlRunner.ExecuteAsync(sql, null, cancellationToken);
            },
            onSuccess: (_, elapsedMs) => this._logger.LogDebug("Vector store schema initialization completed in {ElapsedMs}ms", elapsedMs),
            onError: (ex, elapsedMs) => this._logger.LogError(ex, "Error initializing vector store schema after {ElapsedMs}ms", elapsedMs));
    }

    public async Task<IReadOnlyList<SearchHit<TValue>>> SearchAsync<TValue>(Query query, CancellationToken cancellationToken = default)
    {
        if (query == null)
        {
            throw new ArgumentNullException(nameof(query));
        }

        using var activity = _telemetry.StartActivity("vectorstore.search");
        activity?.SetTag("vectorstore.operation", "search");
        activity?.SetTag("vectorstore.limit", query.Limit ?? 10);
        activity?.SetTag("vectorstore.has_metadata_filter", query.MetadataFilter != null);
        this._logger.LogDebug("Searching vector store with limit {Limit} and metadata filter present: {HasFilter}", query.Limit ?? 10, query.MetadataFilter != null);

        return await _telemetry.ExecuteAsync<IReadOnlyList<SearchHit<TValue>>>(
            "search",
            activity,
            async () =>
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
                  AND (
                      @allSessions
                      OR (session_id = @sid AND project_id = '*')
                      OR (@hasProjects AND session_id = '*' AND project_id = ANY(@pids))
                  )
                  AND (@hasKey       = FALSE OR key = @k)
                  AND (@hasFilter    = FALSE OR metadata @> @mFilter)
                ORDER BY distance ASC
                LIMIT @l;";

                bool allSessions = query.SessionId == null;
                var parameters = new Dictionary<string, object?>
                {
                    ["l"] = query.Limit ?? 10
                };

                parameters["uid"] = query.UserId;
                parameters["allSessions"] = allSessions;
                parameters["sid"] = query.SessionId ?? VectorStoreTelemetry.GlobalSession;

                bool hasProjects = (query.ProjectIds?.Count ?? 0) > 0;
                parameters["hasProjects"] = hasProjects;
                parameters["pids"] = new NpgsqlParameter("pids", NpgsqlDbType.Array | NpgsqlDbType.Text)
                {
                    Value = query.ProjectIds?.ToArray() ?? Array.Empty<string>()
                };

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

                activity?.SetTag("vectorstore.result_count", results.Count);
                return results;
            },
            onSuccess: (results, elapsedMs) => this._logger.LogDebug("Vector store search completed in {ElapsedMs}ms. Results returned: {ResultCount}", elapsedMs, results.Count),
            onError: (ex, elapsedMs) => this._logger.LogError(ex, "Error searching vector store after {ElapsedMs}ms", elapsedMs));
    }

    public async Task UpsertAsync<TValue>(string userId, string? sessionId, string key, TValue value, IDictionary<string, object>? metadata = null, string? projectId = null, CancellationToken cancellationToken = default)
    {
        using var activity = _telemetry.StartActivity("vectorstore.upsert");
        activity?.SetTag("vectorstore.operation", "upsert");
        activity?.SetTag("vectorstore.user_id", userId);
        activity?.SetTag("vectorstore.session_id", sessionId ?? "global");
        activity?.SetTag("vectorstore.key", key);
        activity?.SetTag("vectorstore.has_metadata", metadata != null);
        this._logger.LogDebug("Upserting vector store entry for user {UserId} session {SessionId} key {Key} with metadata present: {HasMetadata}", userId, sessionId ?? "global", key, metadata != null);

        await _telemetry.ExecuteAsync<int>(
            "upsert",
            activity,
            async () =>
            {
                string contentToEmbed = JsonSerializer.Serialize(value);
                var vectorArray = await this._embeddingGenerator.GenerateEmbeddingAsync(contentToEmbed, cancellationToken);
                string vectorLiteral = $"[{string.Join(',', vectorArray.ToArray().Select(v => v.ToString(CultureInfo.InvariantCulture)))}]";

                // Use EXCLUDED to update existing records on compound key conflict
                const string query = @"
                INSERT INTO semantic_kv_store (user_id, session_id, project_id, key, value, embedding, metadata)
                VALUES (@uid, @sid, @pid, @k, @v, @e::vector, @m)
                ON CONFLICT (user_id, session_id, project_id, key) DO UPDATE
                SET value     = EXCLUDED.value,
                    embedding = EXCLUDED.embedding,
                    metadata  = EXCLUDED.metadata;";

                return await this._postgreSqlRunner.ExecuteAsync(
                    query,
                    new Dictionary<string, object?>
                    {
                        ["uid"] = userId,
                        ["sid"] = VectorStoreTelemetry.ResolveSessionId(sessionId),
                        ["pid"] = VectorStoreTelemetry.ResolveProjectId(projectId),
                        ["k"] = key,
                        ["v"] = new NpgsqlParameter("v", NpgsqlDbType.Jsonb) { Value = contentToEmbed },
                        ["e"] = vectorLiteral,
                        ["m"] = metadata != null ? new NpgsqlParameter("m", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(metadata) } : DBNull.Value
                    },
                    cancellationToken);
            },
            onSuccess: (_, elapsedMs) => this._logger.LogDebug("Vector store upsert completed in {ElapsedMs}ms for user {UserId} session {SessionId} key {Key}", elapsedMs, userId, sessionId ?? "global", key),
            onError: (ex, elapsedMs) => this._logger.LogError(ex, "Error upserting vector store entry after {ElapsedMs}ms for user {UserId} session {SessionId} key {Key}", elapsedMs, userId, sessionId ?? "global", key));
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(string userId, string? sessionId, string key, string? projectId = null, CancellationToken cancellationToken = default)
    {
        using var activity = _telemetry.StartActivity("vectorstore.delete");
        activity?.SetTag("vectorstore.operation", "delete");
        activity?.SetTag("vectorstore.user_id", userId);
        activity?.SetTag("vectorstore.session_id", sessionId ?? "global");
        activity?.SetTag("vectorstore.key", key);
        this._logger.LogDebug("Deleting vector store entry for user {UserId} session {SessionId} key {Key}", userId, sessionId ?? "global", key);

        return await _telemetry.ExecuteAsync(
            "delete",
            activity,
            async () =>
            {
                int rowsAffected = await this._postgreSqlRunner.ExecuteAsync(
                    "DELETE FROM semantic_kv_store WHERE user_id = @uid AND session_id = @sid AND project_id = @pid AND key = @k;",
                    new Dictionary<string, object?> { ["uid"] = userId, ["sid"] = VectorStoreTelemetry.ResolveSessionId(sessionId), ["pid"] = VectorStoreTelemetry.ResolveProjectId(projectId), ["k"] = key },
                    cancellationToken);
                activity?.SetTag("vectorstore.deleted", rowsAffected > 0);
                return rowsAffected > 0;
            },
            onSuccess: (deleted, elapsedMs) => this._logger.LogDebug("Vector store delete completed in {ElapsedMs}ms for user {UserId} session {SessionId} key {Key}. Deleted: {Deleted}", elapsedMs, userId, sessionId ?? "global", key, deleted),
            onError: (ex, elapsedMs) => this._logger.LogError(ex, "Error deleting vector store entry after {ElapsedMs}ms for user {UserId} session {SessionId} key {Key}", elapsedMs, userId, sessionId ?? "global", key));
    }

    public async Task<IReadOnlyList<string>> ListProjectsAsync(string userId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT DISTINCT project_id
            FROM semantic_kv_store
            WHERE user_id = @uid AND project_id != '*'
            ORDER BY project_id
            """;

        return await this._postgreSqlRunner.QueryAsync<string>(
            sql,
            reader => Task.FromResult(reader.GetString(0)),
            new Dictionary<string, object?> { ["uid"] = userId },
            cancellationToken);
    }

    public async Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(
        string userId,
        string? sessionId,
        IReadOnlyList<string>? projectIds,
        CancellationToken cancellationToken = default)
    {
        string sid = sessionId ?? VectorStoreTelemetry.GlobalSession;
        bool hasProjects = (projectIds?.Count ?? 0) > 0;

        const string sql = """
            SELECT DISTINCT
                metadata->>'source_file' AS source_file,
                session_id,
                project_id
            FROM semantic_kv_store
            WHERE user_id = @uid
            AND metadata->>'source_file' IS NOT NULL
            AND (
                (session_id = '*' AND project_id = '*')
                OR (session_id = @sid AND project_id = '*')
                OR (@hasProjects AND session_id = '*' AND project_id = ANY(@pids))
            )
            ORDER BY source_file
            """;

        var parameters = new Dictionary<string, object?>
        {
            ["uid"] = userId,
            ["sid"] = sid,
            ["hasProjects"] = hasProjects,
            ["pids"] = new NpgsqlParameter("pids", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = projectIds?.ToArray() ?? Array.Empty<string>()
            }
        };

        return await this._postgreSqlRunner.QueryAsync<DocumentInfo>(
            sql,
            reader => Task.FromResult(new DocumentInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2))),
            parameters,
            cancellationToken);
    }

    private static async Task<SearchHit<TValue>> HydrateSearchHitAsync<TValue>(DbDataReader reader)
    {
        string userId = reader.GetString(0);
        string rawSession = reader.GetString(1);
        string? sessionId = rawSession == VectorStoreTelemetry.GlobalSession ? null : rawSession;

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
