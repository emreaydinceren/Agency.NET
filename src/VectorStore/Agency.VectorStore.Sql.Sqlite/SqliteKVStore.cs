namespace Agency.VectorStore.Sql.Sqlite;

using Agency.Embeddings.Common;
using Agency.Sql.Sqlite;
using Agency.VectorStore.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;

/// <summary>
/// An <see cref="IVectorStore"/> backed by SQLite that stores embeddings as JSON-array TEXT columns
/// and uses a cosine-distance UDF registered via <see cref="RegisterVectorFunctions"/> for similarity search.
/// Metadata filtering is applied in-process after the SQL query because SQLite has no native JSONB
/// containment operator.
/// </summary>
public sealed class SqliteKVStore : IVectorStore
{
    /// <summary>The activity source name used for vector store telemetry.</summary>
    public const string ActivitySourceName = "Agency.VectorStore.Sql.Sqlite";

    /// <summary>The meter name used for vector store telemetry.</summary>
    public const string MeterName = "Agency.VectorStore.Sql.Sqlite";

    private static readonly ActivitySource _activitySource = new(ActivitySourceName);
    private static readonly Meter _meter = new(MeterName);

    private static readonly Counter<long> _operationCount =
        _meter.CreateCounter<long>("vectorstore.operations", unit: "{operation}", description: "Total number of vector store operations executed.");

    private static readonly Histogram<double> _operationDuration =
        _meter.CreateHistogram<double>("vectorstore.duration", unit: "ms", description: "Duration of vector store operations in milliseconds.");

    private readonly ILogger<SqliteKVStore> _logger;
    private readonly IEmbeddingGenerator _embeddingGenerator;
    private readonly SqliteRunner _sqliteRunner;

    private const string GlobalSession = "*";
    private static string ResolveSessionId(string? sessionId) => sessionId ?? GlobalSession;

    /// <summary>
    /// Creates a new <see cref="SqliteKVStore"/>.
    /// </summary>
    /// <param name="embeddingGenerator">Used to generate embedding vectors for stored values and search queries.</param>
    /// <param name="sqliteRunner">
    ///   A runner whose <c>onConnectionOpen</c> callback should include <see cref="RegisterVectorFunctions"/>
    ///   so that the <c>vec_distance_cosine</c> UDF is available on every connection.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public SqliteKVStore(
        IEmbeddingGenerator embeddingGenerator,
        SqliteRunner sqliteRunner,
        ILogger<SqliteKVStore>? logger = null)
    {
        this._embeddingGenerator = embeddingGenerator ?? throw new ArgumentNullException(nameof(embeddingGenerator));
        this._sqliteRunner = sqliteRunner ?? throw new ArgumentNullException(nameof(sqliteRunner));
        this._logger = logger ?? NullLogger<SqliteKVStore>.Instance;
    }

    /// <summary>
    /// Registers the <c>vec_distance_cosine</c> scalar function on a SQLite connection.
    /// Pass this as the <c>onConnectionOpen</c> callback when constructing a <see cref="SqliteRunner"/>
    /// that will be used with <see cref="SqliteKVStore"/>.
    /// </summary>
    public static void RegisterVectorFunctions(SqliteConnection connection)
    {
        connection.CreateFunction("vec_distance_cosine", (string v1, string v2) =>
        {
            float[] a = ParseVector(v1);
            float[] b = ParseVector(v2);
            double dot = a.Zip(b).Sum(p => (double)p.First * p.Second);
            double normA = Math.Sqrt(a.Sum(x => (double)x * x));
            double normB = Math.Sqrt(b.Sum(x => (double)x * x));
            if (normA == 0 || normB == 0)
            {
                return 1.0;
            }

            return 1.0 - (dot / (normA * normB));
        });
    }

    /// <summary>
    /// Creates the <c>semantic_kv_store</c> table and a key index if they do not already exist.
    /// </summary>
    public async Task InitializeSchemaAsync(int dimensions = 1536, CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("vectorstore.initialize", ActivityKind.Client);
        activity?.SetTag("vectorstore.operation", "initialize");
        activity?.SetTag("vectorstore.dimensions", dimensions);

        var stopwatch = Stopwatch.StartNew();
        this._logger.LogDebug("Initializing SQLite vector store schema with {Dimensions} dimensions", dimensions);

        try
        {
            await this._sqliteRunner.ExecuteAsync(
                """
                CREATE TABLE IF NOT EXISTS semantic_kv_store (
                    user_id    TEXT NOT NULL,
                    session_id TEXT NOT NULL,
                    key        TEXT NOT NULL,
                    value      TEXT NOT NULL,
                    embedding  TEXT NOT NULL,
                    metadata   TEXT,
                    updated_on TEXT DEFAULT (datetime('now')),
                    PRIMARY KEY (user_id, session_id, key)
                )
                """,
                null,
                cancellationToken);

            stopwatch.Stop();
            _operationCount.Add(1, new TagList { { "operation", "initialize" }, { "status", "success" } });
            _operationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "initialize" } });

            activity?.SetStatus(ActivityStatusCode.Ok);
            this._logger.LogDebug("SQLite vector store schema initialization completed in {ElapsedMs}ms", stopwatch.Elapsed.TotalMilliseconds);
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

            this._logger.LogError(ex, "Error initializing SQLite vector store schema after {ElapsedMs}ms", stopwatch.Elapsed.TotalMilliseconds);
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

        using var activity = _activitySource.StartActivity("vectorstore.search", ActivityKind.Client);
        activity?.SetTag("vectorstore.operation", "search");
        activity?.SetTag("vectorstore.limit", query.Limit ?? 10);
        activity?.SetTag("vectorstore.has_metadata_filter", query.MetadataFilter != null);

        var stopwatch = Stopwatch.StartNew();
        this._logger.LogDebug("Searching SQLite vector store with limit {Limit}, metadata filter: {HasFilter}", query.Limit ?? 10, query.MetadataFilter != null);

        try
        {
            // When a metadata filter is present we skip the SQL LIMIT so that C#-side filtering
            // can apply it after the containment check. LIMIT -1 means "no limit" in SQLite.
            int sqlLimit = query.MetadataFilter != null ? -1 : (query.Limit ?? 10);

            const string sql = """
                SELECT user_id, session_id, key, value, metadata,
                       CASE WHEN @qVector IS NULL THEN 0.0 ELSE vec_distance_cosine(embedding, @qVector) END AS distance,
                       updated_on
                FROM semantic_kv_store
                WHERE user_id = @uid
                  AND (@hasSessionId = 0 OR session_id = @sid)
                  AND (@hasKey       = 0 OR key = @k)
                ORDER BY distance ASC
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
                var embedding = await this._embeddingGenerator.GenerateEmbeddingAsync(query.Value, cancellationToken);
                parameters["qVector"] = FormatVector(embedding.ToArray());
            }
            else
            {
                parameters["qVector"] = null;
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

            activity?.SetTag("vectorstore.result_count", results.Count);
            activity?.SetStatus(ActivityStatusCode.Ok);

            this._logger.LogDebug("SQLite vector store search completed in {ElapsedMs}ms. Results: {ResultCount}", stopwatch.Elapsed.TotalMilliseconds, results.Count);
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

            this._logger.LogError(ex, "Error searching SQLite vector store after {ElapsedMs}ms", stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task UpsertAsync<TValue>(string userId, string? sessionId, string key, TValue value, IDictionary<string, object>? metadata = null, CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("vectorstore.upsert", ActivityKind.Client);
        activity?.SetTag("vectorstore.operation", "upsert");
        activity?.SetTag("vectorstore.userId", userId);
        activity?.SetTag("vectorstore.sessionId", sessionId);
        activity?.SetTag("vectorstore.key", key);
        activity?.SetTag("vectorstore.has_metadata", metadata != null);

        var stopwatch = Stopwatch.StartNew();
        this._logger.LogDebug("Upserting SQLite vector store entry with userId {UserId}, sessionId {SessionId}, key {Key}", userId, sessionId, key);

        try
        {
            string contentToEmbed = JsonSerializer.Serialize(value);
            var vectorArray = await this._embeddingGenerator.GenerateEmbeddingAsync(contentToEmbed, cancellationToken);
            string vectorLiteral = FormatVector(vectorArray.ToArray());
            string? metadataJson = metadata != null ? JsonSerializer.Serialize(metadata) : null;

            const string upsertSql = """
                INSERT INTO semantic_kv_store (user_id, session_id, key, value, embedding, metadata)
                VALUES (@uid, @sid, @k, @v, @e, @m)
                ON CONFLICT (user_id, session_id, key) DO UPDATE
                SET value      = excluded.value,
                    embedding  = excluded.embedding,
                    metadata   = excluded.metadata,
                    updated_on = datetime('now')
                """;

            await this._sqliteRunner.ExecuteAsync(
                upsertSql,
                new Dictionary<string, object?>
                {
                    ["uid"] = userId,
                    ["sid"] = ResolveSessionId(sessionId),
                    ["k"] = key,
                    ["v"] = contentToEmbed,
                    ["e"] = vectorLiteral,
                    ["m"] = metadataJson
                },
                cancellationToken);

            stopwatch.Stop();
            _operationCount.Add(1, new TagList { { "operation", "upsert" }, { "status", "success" } });
            _operationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "upsert" } });

            activity?.SetStatus(ActivityStatusCode.Ok);
            this._logger.LogDebug("SQLite vector store upsert completed in {ElapsedMs}ms for userId {UserId}, sessionId {SessionId}, key {Key}", stopwatch.Elapsed.TotalMilliseconds, userId, sessionId, key);
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

            this._logger.LogError(ex, "Error upserting SQLite vector store entry after {ElapsedMs}ms for userId {UserId}, sessionId {SessionId}, key {Key}", stopwatch.Elapsed.TotalMilliseconds, userId, sessionId, key);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(string userId, string? sessionId, string key, CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("vectorstore.delete", ActivityKind.Client);
        activity?.SetTag("vectorstore.operation", "delete");
        activity?.SetTag("vectorstore.userId", userId);
        activity?.SetTag("vectorstore.sessionId", sessionId);
        activity?.SetTag("vectorstore.key", key);

        var stopwatch = Stopwatch.StartNew();
        this._logger.LogDebug("Deleting SQLite vector store entry with userId {UserId}, sessionId {SessionId}, key {Key}", userId, sessionId, key);

        try
        {
            int rowsAffected = await this._sqliteRunner.ExecuteAsync(
                "DELETE FROM semantic_kv_store WHERE user_id = @uid AND session_id = @sid AND key = @k;",
                new Dictionary<string, object?> { ["uid"] = userId, ["sid"] = ResolveSessionId(sessionId), ["k"] = key },
                cancellationToken);

            stopwatch.Stop();
            _operationCount.Add(1, new TagList { { "operation", "delete" }, { "status", "success" } });
            _operationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "delete" } });

            activity?.SetTag("vectorstore.deleted", rowsAffected > 0);
            activity?.SetStatus(ActivityStatusCode.Ok);
            this._logger.LogDebug("SQLite vector store delete completed in {ElapsedMs}ms for userId {UserId}, sessionId {SessionId}, key {Key}. Deleted: {Deleted}", stopwatch.Elapsed.TotalMilliseconds, userId, sessionId, key, rowsAffected > 0);
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

            this._logger.LogError(ex, "Error deleting SQLite vector store entry after {ElapsedMs}ms for userId {UserId}, sessionId {SessionId}, key {Key}", stopwatch.Elapsed.TotalMilliseconds, userId, sessionId, key);
            throw;
        }
    }

    private static SearchHit<TValue> HydrateSearchHit<TValue>(DbDataReader reader)
    {
        string userId = reader.GetString(0);
        string? sessionId = reader.GetString(1) == GlobalSession ? null : reader.GetString(1);
        string key = reader.GetString(2);
        string valueJson = reader.GetString(3);
        string? metadataJson = reader.IsDBNull(4) ? null : reader.GetString(4);
        double distance = Convert.ToDouble(reader.GetValue(5));

        DateTimeOffset updatedOn = DateTimeOffset.UtcNow;
        if (!reader.IsDBNull(6))
        {
            string raw = reader.GetString(6);
            if (DateTime.TryParseExact(raw, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime dt))
            {
                updatedOn = new DateTimeOffset(dt, TimeSpan.Zero);
            }
        }

        return new SearchHit<TValue>(
            UserId: userId,
            SessionId: sessionId,
            Key: key,
            Value: JsonSerializer.Deserialize<TValue>(valueJson)!,
            Metadata: JsonMetadataHelpers.DeserializeMetadata(metadataJson),
            Distance: distance,
            UpdatedOn: updatedOn);
    }

    /// <summary>
    /// Returns true when <paramref name="metadata"/> satisfies every constraint in <paramref name="filter"/>.
    /// For array-valued filter entries (e.g. <c>{"tags": ["medical"]}</c>), each element in the filter
    /// array must appear somewhere in the corresponding metadata array (subset containment).
    /// For scalar entries, the values are compared as strings.
    /// </summary>
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

    private static string FormatVector(float[] vector)
        => $"[{string.Join(',', vector.Select(v => v.ToString(CultureInfo.InvariantCulture)))}]";

    private static float[] ParseVector(string raw)
        => raw.Trim('[', ']').Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
}
