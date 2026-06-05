using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Agency.Embeddings.Common;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agency.Memory.Sql.Sqlite;

/// <summary>
/// SQLite implementation of <see cref="IMemoryStore"/>.
/// Embeddings are stored as JSON-array TEXT and cosine similarity is computed via
/// an in-process UDF registered on every connection.
/// </summary>
/// <remarks>
/// All write operations update the in-memory <c>LastWrittenAt</c> cache as well as the
/// <c>user_state</c> table so the retrieval gate (Spec §8.1) always sees a fresh value
/// even across process restarts (one-turn hydration penalty on cold start).
/// </remarks>
public sealed class SqliteMemoryStore : IMemoryStore
{
    /// <summary>The activity source name used for memory store telemetry.</summary>
    public const string ActivitySourceName = "Agency.Memory.Sql.Sqlite";

    /// <summary>The meter name used for memory store telemetry.</summary>
    public const string MeterName = "Agency.Memory.Sql.Sqlite";

    private static readonly ActivitySource _activitySource = new(ActivitySourceName);
    private static readonly Meter _meter = new(MeterName);

    private static readonly Counter<long> _upsertCount =
        _meter.CreateCounter<long>("memory.upsert.count", description: "Total upsert operations.");

    private static readonly Histogram<double> _upsertDuration =
        _meter.CreateHistogram<double>("memory.upsert.duration", unit: "ms", description: "Upsert duration in milliseconds.");

    private static readonly Counter<long> _searchCount =
        _meter.CreateCounter<long>("memory.search.count", description: "Total search operations.");

    private static readonly Histogram<double> _searchDuration =
        _meter.CreateHistogram<double>("memory.search.duration", unit: "ms", description: "Search duration in milliseconds.");

    private readonly string _connectionString;
    private readonly IEmbeddingGenerator _embedder;
    private readonly MemoryOptions _options;
    private readonly ILogger<SqliteMemoryStore> _logger;

    /// <summary>
    /// In-memory cache of <c>LastWrittenAt</c> per user.
    /// Keyed by <c>userId</c>. Written through on every mutation.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastWrittenCache = new();

    /// <summary>
    /// Creates a new <see cref="SqliteMemoryStore"/>.
    /// </summary>
    /// <param name="connectionString">The SQLite connection string.</param>
    /// <param name="embedder">The embedding generator used when a record arrives without an embedding.</param>
    /// <param name="options">Memory options.</param>
    /// <param name="logger">Logger instance.</param>
    public SqliteMemoryStore(
        string connectionString,
        IEmbeddingGenerator embedder,
        IOptions<MemoryOptions> options,
        ILogger<SqliteMemoryStore> logger)
    {
        this._connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        this._embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        this._options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        this._logger = logger ?? NullLogger<SqliteMemoryStore>.Instance;
    }

    /// <inheritdoc/>
    public async Task<Record> UpsertAsync(Record record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        using var activity = _activitySource.StartActivity("memory.upsert", ActivityKind.Client);
        var sw = Stopwatch.StartNew();

        try
        {
            var embedding = record.Embedding.IsEmpty
                ? await this._embedder.GenerateEmbeddingAsync(record.Title + "\n\n" + record.Value, ct)
                : record.Embedding;

            string id = string.IsNullOrEmpty(record.Id) || !Guid.TryParse(record.Id, out _)
                ? Guid.NewGuid().ToString()
                : record.Id;

            string now = DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
            string tagsJson = JsonSerializer.Serialize(record.Tags.ToArray());
            string embeddingText = VectorFunctions.FormatVector(embedding.ToArray());
            string? lastAccessedAtStr = record.LastAccessedAt.HasValue
                ? record.LastAccessedAt.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
                : null;

            const string sql = @"
                INSERT INTO records (
                    id, user_id, session_id, content_type, domain, key,
                    title, value, tags, importance, embedding,
                    created_at, updated_at, last_accessed_at)
                VALUES (
                    @id, @user_id, @session_id, @content_type, @domain, @key,
                    @title, @value, @tags, @importance, @embedding,
                    @now, @now, @last_accessed_at)
                ON CONFLICT (user_id, COALESCE(session_id, ''), domain, key) DO UPDATE SET
                    title            = excluded.title,
                    value            = excluded.value,
                    tags             = excluded.tags,
                    importance       = excluded.importance,
                    embedding        = excluded.embedding,
                    updated_at       = @now
                RETURNING id, created_at, updated_at;";

            await using var conn = await this.OpenConnectionAsync(ct);
            await using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@user_id", record.UserId);
            cmd.Parameters.AddWithValue("@session_id", (object?)record.SessionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@content_type", (int)record.ContentType);
            cmd.Parameters.AddWithValue("@domain", record.Domain);
            cmd.Parameters.AddWithValue("@key", record.Key);
            cmd.Parameters.AddWithValue("@title", record.Title);
            cmd.Parameters.AddWithValue("@value", record.Value);
            cmd.Parameters.AddWithValue("@tags", tagsJson);
            cmd.Parameters.AddWithValue("@importance", record.Importance);
            cmd.Parameters.AddWithValue("@embedding", embeddingText);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@last_accessed_at", (object?)lastAccessedAtStr ?? DBNull.Value);

            string assignedId;
            DateTimeOffset createdAt;
            DateTimeOffset updatedAt;

            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                await reader.ReadAsync(ct);
                assignedId = reader.GetString(0);
                createdAt = ParseTimestamp(reader.GetString(1));
                updatedAt = ParseTimestamp(reader.GetString(2));
            }

            var nowTs = DateTimeOffset.UtcNow;
            await BumpLastWrittenAtAsync(conn, record.UserId, nowTs, ct);

            sw.Stop();
            _upsertCount.Add(1, new TagList { { "status", "success" } });
            _upsertDuration.Record(sw.Elapsed.TotalMilliseconds);

            this._logger.LogDebug("Upserted record {Id} for user {UserId}", assignedId, record.UserId);

            return record with
            {
                Id = assignedId,
                CreatedAt = createdAt,
                UpdatedAt = updatedAt,
                Embedding = embedding,
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _upsertCount.Add(1, new TagList { { "status", "error" } });
            _upsertDuration.Record(sw.Elapsed.TotalMilliseconds);
            this._logger.LogError(ex, "Error upserting record for user {UserId}", record.UserId);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        using var activity = _activitySource.StartActivity("memory.search", ActivityKind.Client);
        var sw = Stopwatch.StartNew();

        try
        {
            string sql = BuildSearchSql(query);
            string queryVecText = VectorFunctions.FormatVector(query.QueryEmbedding.ToArray());

            await using var conn = await this.OpenConnectionAsync(ct);
            await using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@user_id", query.UserId);
            cmd.Parameters.AddWithValue("@query_vec", queryVecText);
            cmd.Parameters.AddWithValue("@top_k", query.TopK);

            if (query.ContentType.HasValue)
            {
                cmd.Parameters.AddWithValue("@content_type", (int)query.ContentType.Value);
            }

            if (query.Domain is not null)
            {
                cmd.Parameters.AddWithValue("@domain", query.Domain);
            }

            var hits = new List<SearchHit>();

            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    var r = ReadRecord(reader);
                    double distance = reader.GetDouble(14);
                    double similarity = 1.0 - distance;
                    hits.Add(new SearchHit(r, similarity));
                }
            }

            sw.Stop();
            _searchCount.Add(1, new TagList { { "status", "success" } });
            _searchDuration.Record(sw.Elapsed.TotalMilliseconds);

            // Fire-and-forget: bump last_accessed_at for hit rows
            if (hits.Count > 0)
            {
                var ids = hits.Select(h => h.Record.Id).ToArray();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await this.BumpLastAccessedAtAsync(ids, default);
                    }
                    catch (Exception ex)
                    {
                        this._logger.LogWarning(ex, "Failed to bump last_accessed_at for {Count} records", ids.Length);
                    }
                }, CancellationToken.None);
            }

            return hits;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _searchCount.Add(1, new TagList { { "status", "error" } });
            _searchDuration.Record(sw.Elapsed.TotalMilliseconds);
            this._logger.LogError(ex, "Error searching records for user {UserId}", query.UserId);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<Record?> GetByKeyAsync(
        string userId,
        string? sessionId,
        string domain,
        string key,
        CancellationToken ct = default)
    {
        string sql;
        if (sessionId is null)
        {
            sql = @"SELECT id, user_id, session_id, content_type, domain, key,
                           title, value, tags, importance, embedding,
                           created_at, updated_at, last_accessed_at
                    FROM records
                    WHERE user_id = @user_id AND session_id IS NULL AND domain = @domain AND key = @key
                    LIMIT 1;";
        }
        else
        {
            sql = @"SELECT id, user_id, session_id, content_type, domain, key,
                           title, value, tags, importance, embedding,
                           created_at, updated_at, last_accessed_at
                    FROM records
                    WHERE user_id = @user_id AND session_id = @session_id AND domain = @domain AND key = @key
                    LIMIT 1;";
        }

        await using var conn = await this.OpenConnectionAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@user_id", userId);
        if (sessionId is not null)
        {
            cmd.Parameters.AddWithValue("@session_id", sessionId);
        }

        cmd.Parameters.AddWithValue("@domain", domain);
        cmd.Parameters.AddWithValue("@key", key);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return ReadRecord(reader);
    }

    /// <inheritdoc/>
    public async Task<bool> ForgetAsync(string userId, string domain, string key, CancellationToken ct = default)
    {
        const string sql = @"
            DELETE FROM records
            WHERE user_id = @user_id AND domain = @domain AND key = @key;";

        await using var conn = await this.OpenConnectionAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@user_id", userId);
        cmd.Parameters.AddWithValue("@domain", domain);
        cmd.Parameters.AddWithValue("@key", key);

        int rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows > 0)
        {
            var now = DateTimeOffset.UtcNow;
            await BumpLastWrittenAtAsync(conn, userId, now, ct);
        }

        return rows > 0;
    }

    /// <inheritdoc/>
    public async Task<int> ForgetMeAsync(string userId, CancellationToken ct = default)
    {
        const string sql = "DELETE FROM records WHERE user_id = @user_id;";

        await using var conn = await this.OpenConnectionAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@user_id", userId);

        int rows = await cmd.ExecuteNonQueryAsync(ct);
        var now = DateTimeOffset.UtcNow;
        await BumpLastWrittenAtAsync(conn, userId, now, ct);

        return rows;
    }

    /// <inheritdoc/>
    public async Task<DateTimeOffset?> LastWrittenAtAsync(string userId, CancellationToken ct = default)
    {
        if (this._lastWrittenCache.TryGetValue(userId, out var cached))
        {
            return cached;
        }

        // Cache miss — hydrate from DB
        const string sql = "SELECT last_written_at FROM user_state WHERE user_id = @user_id;";
        await using var conn = await this.OpenConnectionAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@user_id", userId);

        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null or DBNull)
        {
            return null;
        }

        var ts = ParseTimestamp(result.ToString()!);
        this._lastWrittenCache[userId] = ts;
        return ts;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Record>> GetAllForUserAsync(string userId, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT id, user_id, session_id, content_type, domain, key,
                   title, value, tags, importance, embedding,
                   created_at, updated_at, last_accessed_at
            FROM records
            WHERE user_id = @user_id;";

        await using var conn = await this.OpenConnectionAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@user_id", userId);

        var results = new List<Record>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(ReadRecord(reader));
        }

        return results;
    }

    /// <inheritdoc/>
    public async Task<int> DeleteWhereTtlExceededAsync(
        ContentType contentType,
        TimeSpan ttl,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        // Compute cutoff in C# (TI-4: deterministic under virtual clock injected by the sweeper).
        string cutoff = (now - ttl).UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

        const string sql = @"
            DELETE FROM records
            WHERE content_type = @content_type
              AND updated_at < @cutoff
              AND (last_accessed_at IS NULL OR last_accessed_at < @cutoff);";

        await using var conn = await this.OpenConnectionAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@content_type", (int)contentType);
        cmd.Parameters.AddWithValue("@cutoff", cutoff);

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<int> DeleteWhereLowImportanceStaleAsync(
        double importanceThreshold,
        TimeSpan staleAge,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        // Compute cutoff in C# (TI-4: deterministic under virtual clock injected by the sweeper).
        string cutoff = (now - staleAge).UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

        const string sql = @"
            DELETE FROM records
            WHERE importance < @importance_threshold
              AND (last_accessed_at IS NULL OR last_accessed_at < @cutoff);";

        await using var conn = await this.OpenConnectionAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@importance_threshold", importanceThreshold);
        cmd.Parameters.AddWithValue("@cutoff", cutoff);

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<Record> MergeAsync(
        IReadOnlyList<string> idsToDelete,
        Record newRecord,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(idsToDelete);
        ArgumentNullException.ThrowIfNull(newRecord);

        var embedding = newRecord.Embedding.IsEmpty
            ? await this._embedder.GenerateEmbeddingAsync(newRecord.Title + "\n\n" + newRecord.Value, ct)
            : newRecord.Embedding;

        await using var conn = await this.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            // DELETE the listed ids.
            if (idsToDelete.Count > 0)
            {
                var paramNames = idsToDelete
                    .Select((_, i) => $"@id{i}")
                    .ToArray();

                string deleteSql = $"DELETE FROM records WHERE id IN ({string.Join(", ", paramNames)}) AND user_id = @user_id;";
                await using var deleteCmd = new SqliteCommand(deleteSql, conn, (SqliteTransaction)tx);
                deleteCmd.Parameters.AddWithValue("@user_id", newRecord.UserId);
                for (int i = 0; i < idsToDelete.Count; i++)
                {
                    deleteCmd.Parameters.AddWithValue(paramNames[i], idsToDelete[i]);
                }

                await deleteCmd.ExecuteNonQueryAsync(ct);
            }

            // INSERT the new record.
            string newId = string.IsNullOrEmpty(newRecord.Id) || !Guid.TryParse(newRecord.Id, out _)
                ? Guid.NewGuid().ToString()
                : newRecord.Id;

            string now = DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
            string tagsJson = JsonSerializer.Serialize(newRecord.Tags.ToArray());
            string embeddingText = VectorFunctions.FormatVector(embedding.ToArray());

            const string insertSql = @"
                INSERT INTO records (
                    id, user_id, session_id, content_type, domain, key,
                    title, value, tags, importance, embedding,
                    created_at, updated_at, last_accessed_at)
                VALUES (
                    @id, @user_id, @session_id, @content_type, @domain, @key,
                    @title, @value, @tags, @importance, @embedding,
                    @now, @now, NULL)
                RETURNING id, created_at, updated_at;";

            await using var insertCmd = new SqliteCommand(insertSql, conn, (SqliteTransaction)tx);
            insertCmd.Parameters.AddWithValue("@id", newId);
            insertCmd.Parameters.AddWithValue("@user_id", newRecord.UserId);
            insertCmd.Parameters.AddWithValue("@session_id", (object?)newRecord.SessionId ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("@content_type", (int)newRecord.ContentType);
            insertCmd.Parameters.AddWithValue("@domain", newRecord.Domain);
            insertCmd.Parameters.AddWithValue("@key", newRecord.Key);
            insertCmd.Parameters.AddWithValue("@title", newRecord.Title);
            insertCmd.Parameters.AddWithValue("@value", newRecord.Value);
            insertCmd.Parameters.AddWithValue("@tags", tagsJson);
            insertCmd.Parameters.AddWithValue("@importance", newRecord.Importance);
            insertCmd.Parameters.AddWithValue("@embedding", embeddingText);
            insertCmd.Parameters.AddWithValue("@now", now);

            string assignedId;
            DateTimeOffset createdAt;
            DateTimeOffset updatedAt;

            await using (var reader = await insertCmd.ExecuteReaderAsync(ct))
            {
                await reader.ReadAsync(ct);
                assignedId = reader.GetString(0);
                createdAt = ParseTimestamp(reader.GetString(1));
                updatedAt = ParseTimestamp(reader.GetString(2));
            }

            var nowTs = DateTimeOffset.UtcNow;
            string nowTsStr = nowTs.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

            const string bumpSql = @"
                INSERT INTO user_state (user_id, last_written_at)
                VALUES (@user_id, @ts)
                ON CONFLICT (user_id) DO UPDATE
                SET last_written_at = MAX(user_state.last_written_at, excluded.last_written_at);";

            await using var bumpCmd = new SqliteCommand(bumpSql, conn, (SqliteTransaction)tx);
            bumpCmd.Parameters.AddWithValue("@user_id", newRecord.UserId);
            bumpCmd.Parameters.AddWithValue("@ts", nowTsStr);
            await bumpCmd.ExecuteNonQueryAsync(ct);

            await tx.CommitAsync(ct);
            this._lastWrittenCache[newRecord.UserId] = nowTs;

            this._logger.LogInformation(
                "MergeAsync: deleted {Count} records and inserted {Id} for user {UserId}",
                idsToDelete.Count, assignedId, newRecord.UserId);

            return newRecord with
            {
                Id = assignedId,
                CreatedAt = createdAt,
                UpdatedAt = updatedAt,
                Embedding = embedding,
            };
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<Record?> UpdateRecordAsync(
        string recordId,
        string userId,
        string? newValue,
        double? newImportance,
        CancellationToken ct = default)
    {
        if (newValue is null && newImportance is null)
        {
            return null;
        }

        if (newImportance is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(newImportance), newImportance, "Importance must be in [0, 1].");
        }

        // Validate GUID format — non-GUID ids cannot match the TEXT primary key
        if (!Guid.TryParse(recordId, out _))
        {
            return null;
        }

        string now = DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

        // Build SET clause conditionally to avoid overwriting unchanged fields with NULL
        var setClauses = new StringBuilder();
        bool hasValue = newValue is not null;
        bool hasImportance = newImportance is not null;

        if (hasValue)
        {
            setClauses.Append("value = @new_value, ");
        }

        if (hasImportance)
        {
            setClauses.Append("importance = @new_importance, ");
        }

        setClauses.Append("updated_at = @now");

        string sql = $@"
            UPDATE records
            SET {setClauses}
            WHERE id = @id AND user_id = @user_id
            RETURNING id, user_id, session_id, content_type, domain, key,
                      title, value, tags, importance, embedding,
                      created_at, updated_at, last_accessed_at;";

        await using var conn = await this.OpenConnectionAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", recordId);
        cmd.Parameters.AddWithValue("@user_id", userId);
        cmd.Parameters.AddWithValue("@now", now);

        if (hasValue)
        {
            cmd.Parameters.AddWithValue("@new_value", newValue!);
        }

        if (hasImportance)
        {
            cmd.Parameters.AddWithValue("@new_importance", newImportance!.Value);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var record = ReadRecord(reader);
        var nowTs = DateTimeOffset.UtcNow;
        reader.Close();
        await BumpLastWrittenAtAsync(conn, userId, nowTs, ct);
        return record;
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteByIdAsync(string recordId, string userId, CancellationToken ct = default)
    {
        // Validate GUID format
        if (!Guid.TryParse(recordId, out _))
        {
            return false;
        }

        const string sql = "DELETE FROM records WHERE id = @id AND user_id = @user_id;";

        await using var conn = await this.OpenConnectionAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", recordId);
        cmd.Parameters.AddWithValue("@user_id", userId);

        int rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows > 0)
        {
            var now = DateTimeOffset.UtcNow;
            await BumpLastWrittenAtAsync(conn, userId, now, ct);
        }

        return rows > 0;
    }

    // ── private helpers ───────────────────────────────────────────────────────

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(this._connectionString);
        await conn.OpenAsync(ct);
        VectorFunctions.RegisterVectorFunctions(conn);
        return conn;
    }

    private static string BuildSearchSql(SearchQuery query)
    {
        var where = new StringBuilder("WHERE user_id = @user_id");

        if (query.ContentType.HasValue)
        {
            where.Append(" AND content_type = @content_type");
        }

        if (query.Domain is not null)
        {
            where.Append(" AND domain = @domain");
        }

        return $@"
            SELECT id, user_id, session_id, content_type, domain, key,
                   title, value, tags, importance, embedding,
                   created_at, updated_at, last_accessed_at,
                   vec_distance_cosine(embedding, @query_vec) AS distance
            FROM records
            {where}
            ORDER BY distance ASC
            LIMIT @top_k;";
    }

    private static Record ReadRecord(SqliteDataReader reader)
    {
        string id = reader.GetString(0);
        string userId = reader.GetString(1);
        string? sessionId = reader.IsDBNull(2) ? null : reader.GetString(2);
        var contentType = (ContentType)Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture);
        string domain = reader.GetString(4);
        string key = reader.GetString(5);
        string title = reader.GetString(6);
        string value = reader.GetString(7);
        string tagsRaw = reader.IsDBNull(8) ? "[]" : reader.GetString(8);
        string[] tags = JsonSerializer.Deserialize<string[]>(tagsRaw) ?? [];
        double importance = reader.GetDouble(9);
        string embeddingRaw = reader.GetString(10);
        float[] embeddingArr = string.IsNullOrEmpty(embeddingRaw) ? [] : VectorFunctions.ParseVector(embeddingRaw);
        var createdAt = ParseTimestamp(reader.GetString(11));
        var updatedAt = ParseTimestamp(reader.GetString(12));
        DateTimeOffset? lastAccessedAt = reader.IsDBNull(13) ? null : ParseTimestamp(reader.GetString(13));

        return Record.Create(
            id: id,
            userId: userId,
            sessionId: sessionId,
            contentType: contentType,
            domain: domain,
            key: key,
            title: title,
            value: value,
            tags: tags,
            importance: importance,
            createdAt: createdAt,
            updatedAt: updatedAt,
            lastAccessedAt: lastAccessedAt,
            embedding: embeddingArr.AsMemory());
    }

    private async Task BumpLastWrittenAtAsync(
        SqliteConnection conn,
        string userId,
        DateTimeOffset ts,
        CancellationToken ct)
    {
        string tsStr = ts.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

        const string sql = @"
            INSERT INTO user_state (user_id, last_written_at)
            VALUES (@user_id, @ts)
            ON CONFLICT (user_id) DO UPDATE
            SET last_written_at = MAX(user_state.last_written_at, excluded.last_written_at);";

        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@user_id", userId);
        cmd.Parameters.AddWithValue("@ts", tsStr);
        await cmd.ExecuteNonQueryAsync(ct);

        this._lastWrittenCache[userId] = ts;
    }

    private async Task BumpLastAccessedAtAsync(string[] ids, CancellationToken ct)
    {
        if (ids.Length == 0)
        {
            return;
        }

        string now = DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var paramNames = ids.Select((_, i) => $"@id{i}").ToArray();
        string sql = $"UPDATE records SET last_accessed_at = @now WHERE id IN ({string.Join(", ", paramNames)});";

        await using var conn = await this.OpenConnectionAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@now", now);
        for (int i = 0; i < ids.Length; i++)
        {
            cmd.Parameters.AddWithValue(paramNames[i], ids[i]);
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static DateTimeOffset ParseTimestamp(string raw)
    {
        var dt = DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return new DateTimeOffset(dt.UtcDateTime, TimeSpan.Zero);
    }
}