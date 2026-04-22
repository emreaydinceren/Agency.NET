namespace Agency.VectorStore.Sql.Postgre;

using Agency.Embeddings.Common;
using Agency.Sql.Postgre;
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

public class PostgreKVStore : IKVStore
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

            CREATE TABLE IF NOT EXISTS semantic_kv_store (
                key TEXT PRIMARY KEY,
                value JSONB NOT NULL,
                embedding vector({dimensions}) NOT NULL,
                metadata JSONB,
                updated_on TIMESTAMPTZ DEFAULT NOW()
            );

            -- HNSW index for high-speed similarity search
            CREATE INDEX IF NOT EXISTS idx_vector_search
            ON semantic_kv_store USING hnsw (embedding vector_cosine_ops);

            -- GIN index for high-speed JSON metadata filtering
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
            SELECT key, value, metadata,
                   CASE WHEN @qVector::vector IS NULL THEN 0.0 ELSE (embedding <=> @qVector::vector) END AS distance,
                   updated_on
            FROM semantic_kv_store
            WHERE (@hasKey = FALSE OR key = @k)
            AND (@hasFilter = FALSE OR metadata @> @mFilter)
            ORDER BY distance ASC
            LIMIT @l;";

            var parameters = new Dictionary<string, object?>
            {
                ["l"] = query.Limit ?? 10
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

    public async Task UpsertAsync<TValue>(string key, TValue value, IDictionary<string, object>? metadata = null, CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("vectorstore.upsert", ActivityKind.Client);
        activity?.SetTag("vectorstore.operation", "upsert");
        activity?.SetTag("vectorstore.key", key);
        activity?.SetTag("vectorstore.has_metadata", metadata != null);

        var stopwatch = Stopwatch.StartNew();
        this._logger.LogDebug("Upserting vector store entry with key {Key} and metadata present: {HasMetadata}", key, metadata != null);

        try
        {
            string contentToEmbed = JsonSerializer.Serialize(value);
            var vectorArray = await this._embeddingGenerator.GenerateEmbeddingAsync(contentToEmbed, cancellationToken);
            string vectorLiteral = $"[{string.Join(',', vectorArray.ToArray().Select(v => v.ToString(CultureInfo.InvariantCulture)))}]";

            // Use EXCLUDED to update existing records on Key conflict
            const string query = @"
            INSERT INTO semantic_kv_store (key, value, embedding, metadata)
            VALUES (@k, @v, @e::vector, @m)
            ON CONFLICT (key) DO UPDATE
            SET value = EXCLUDED.value,
                embedding = EXCLUDED.embedding,
                metadata = EXCLUDED.metadata;";

            await this._postgreSqlRunner.ExecuteAsync(
                query,
                new Dictionary<string, object?>
                {
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
            this._logger.LogDebug("Vector store upsert completed in {ElapsedMs}ms for key {Key}", stopwatch.Elapsed.TotalMilliseconds, key);
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

            this._logger.LogError(ex, "Error upserting vector store entry after {ElapsedMs}ms for key {Key}", stopwatch.Elapsed.TotalMilliseconds, key);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("vectorstore.delete", ActivityKind.Client);
        activity?.SetTag("vectorstore.operation", "delete");
        activity?.SetTag("vectorstore.key", key);

        var stopwatch = Stopwatch.StartNew();
        this._logger.LogDebug("Deleting vector store entry with key {Key}", key);

        try
        {
            int rowsAffected = await this._postgreSqlRunner.ExecuteAsync(
                "DELETE FROM semantic_kv_store WHERE key = @k;",
                new Dictionary<string, object?> { ["k"] = key },
                cancellationToken);

            stopwatch.Stop();
            _operationCount.Add(1, new TagList { { "operation", "delete" }, { "status", "success" } });
            _operationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "delete" } });

            activity?.SetTag("vectorstore.deleted", rowsAffected > 0);
            activity?.SetStatus(ActivityStatusCode.Ok);
            this._logger.LogDebug("Vector store delete completed in {ElapsedMs}ms for key {Key}. Deleted: {Deleted}", stopwatch.Elapsed.TotalMilliseconds, key, rowsAffected > 0);
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

            this._logger.LogError(ex, "Error deleting vector store entry after {ElapsedMs}ms for key {Key}", stopwatch.Elapsed.TotalMilliseconds, key);
            throw;
        }
    }

    private static async Task<SearchHit<TValue>> HydrateSearchHitAsync<TValue>(DbDataReader reader)
    {
        string? valueJson = reader.GetValue(1) switch
        {
            null => null,
            DBNull => null,
            JsonDocument document => document.RootElement.GetRawText(),
            JsonElement element => element.GetRawText(),
            _ => reader.GetValue(1)?.ToString()
        };

        string? metadataJson = reader.GetValue(2) switch
        {
            null => null,
            DBNull => null,
            JsonDocument document => document.RootElement.GetRawText(),
            JsonElement element => element.GetRawText(),
            _ => reader.GetValue(2)?.ToString()
        };

        return new SearchHit<TValue>(
            Key: reader.GetString(0),
            Value: string.IsNullOrWhiteSpace(valueJson) ? default! : JsonSerializer.Deserialize<TValue>(valueJson)!,
            Metadata: JsonMetadataHelpers.DeserializeMetadata(metadataJson),
            Distance: reader.GetDouble(3),
            UpdatedOn: reader.IsDBNull(4) ? DateTimeOffset.UtcNow : new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero)
        );
    }
}
