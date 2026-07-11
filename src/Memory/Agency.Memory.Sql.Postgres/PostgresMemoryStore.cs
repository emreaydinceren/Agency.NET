using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Agency.Embeddings.Common;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace Agency.Memory.Sql.Postgres;

/// <summary>
/// PostgreSQL + pgvector implementation of <see cref="IMemoryStore"/>.
/// </summary>
/// <remarks>
/// All write operations update the in-memory <c>LastWrittenAt</c> cache as well as the
/// <c>user_state</c> table so the retrieval gate (Spec §8.1) always sees a fresh value
/// even across process restarts (one-turn hydration penalty on cold start).
/// </remarks>
public sealed partial class PostgresMemoryStore : IMemoryStore
{
    /// <summary>The activity source name used for memory store telemetry.</summary>
    public const string ActivitySourceName = "Agency.Memory.Sql.Postgres";

    /// <summary>The meter name used for memory store telemetry.</summary>
    public const string MeterName = "Agency.Memory.Sql.Postgres";

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

    private readonly NpgsqlDataSource _dataSource;
    private readonly IEmbeddingGenerator _embedder;
    private readonly MemoryOptions _options;
    private readonly ILogger<PostgresMemoryStore> _logger;

    /// <summary>
    /// In-memory cache of <c>LastWrittenAt</c> per user.
    /// Keyed by <c>userId</c>. Written through on every mutation.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastWrittenCache = new();

    /// <summary>
    /// Creates a new <see cref="PostgresMemoryStore"/>.
    /// </summary>
    /// <param name="dataSource">The Npgsql data source.</param>
    /// <param name="embedder">The embedding generator used when a record arrives without an embedding.</param>
    /// <param name="options">Memory options.</param>
    /// <param name="logger">Logger instance.</param>
    public PostgresMemoryStore(
        NpgsqlDataSource dataSource,
        IEmbeddingGenerator embedder,
        IOptions<MemoryOptions> options,
        ILogger<PostgresMemoryStore> logger)
    {
        this._dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        this._embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        this._options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        this._logger = logger ?? NullLogger<PostgresMemoryStore>.Instance;
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

            const string sql = @"
                INSERT INTO records (
                    id, user_id, session_id, content_type, domain, key,
                    title, value, tags, importance, embedding,
                    created_at, updated_at, last_accessed_at)
                VALUES (
                    COALESCE(@id, gen_random_uuid()), @user_id, @session_id, @content_type, @domain, @key,
                    @title, @value, @tags, @importance, @embedding,
                    now(), now(), @last_accessed_at)
                ON CONFLICT (user_id, COALESCE(session_id, ''), domain, key)
                DO UPDATE SET
                    title            = EXCLUDED.title,
                    value            = EXCLUDED.value,
                    tags             = EXCLUDED.tags,
                    importance       = EXCLUDED.importance,
                    embedding        = EXCLUDED.embedding,
                    updated_at       = now()
                RETURNING id::text, created_at, updated_at;";

            await using var conn = await this._dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)conn);

            if (!string.IsNullOrEmpty(record.Id) && Guid.TryParse(record.Id, out var parsedId))
            {
                cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = parsedId });
            }
            else
            {
                cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = DBNull.Value });
            }
            cmd.Parameters.AddWithValue("user_id", record.UserId);
            cmd.Parameters.AddWithValue("session_id", (object?)record.SessionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("content_type", (short)record.ContentType);
            cmd.Parameters.AddWithValue("domain", record.Domain);
            cmd.Parameters.AddWithValue("key", record.Key);
            cmd.Parameters.AddWithValue("title", record.Title);
            cmd.Parameters.AddWithValue("value", record.Value);
            cmd.Parameters.Add(new NpgsqlParameter<string[]>("tags", record.Tags.ToArray()));
            cmd.Parameters.AddWithValue("importance", record.Importance);
            cmd.Parameters.Add(new NpgsqlParameter("embedding", new Vector(embedding.ToArray())));
            cmd.Parameters.AddWithValue("last_accessed_at", (object?)record.LastAccessedAt?.UtcDateTime ?? DBNull.Value);

            string assignedId;
            DateTimeOffset createdAt;
            DateTimeOffset updatedAt;

            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                await reader.ReadAsync(ct);
                assignedId = reader.GetString(0);
                createdAt = new DateTimeOffset(reader.GetDateTime(1), TimeSpan.Zero);
                updatedAt = new DateTimeOffset(reader.GetDateTime(2), TimeSpan.Zero);
            }

            var now = DateTimeOffset.UtcNow;
            await BumpLastWrittenAtAsync(conn, record.UserId, now, ct);

            sw.Stop();
            _upsertCount.Add(1, new TagList { { "status", "success" } });
            _upsertDuration.Record(sw.Elapsed.TotalMilliseconds);

            this.LogUpsertedRecord(assignedId, record.UserId);

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
            this.LogErrorUpsertingRecord(ex, record.UserId);
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
            var sql = BuildSearchSql(query);

            await using var conn = await this._dataSource.OpenConnectionAsync(ct);
            // CA2100: BuildSearchSql only appends fixed literal clause fragments based on
            // boolean flags; every value is bound below via AddWithValue/NpgsqlParameter.
#pragma warning disable CA2100
            await using var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)conn);
#pragma warning restore CA2100

            cmd.Parameters.AddWithValue("user_id", query.UserId);
            cmd.Parameters.Add(new NpgsqlParameter("query_vec", new Vector(query.QueryEmbedding.ToArray())));
            cmd.Parameters.AddWithValue("top_k", query.TopK);

            if (query.ContentType.HasValue)
            {
                cmd.Parameters.AddWithValue("content_type", (short)query.ContentType.Value);
            }

            if (query.Domain is not null)
            {
                cmd.Parameters.AddWithValue("domain", query.Domain);
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
                        await BumpLastAccessedAtAsync(ids, default);
                    }
                    catch (Exception ex)
                    {
                        this.LogFailedToBumpLastAccessedAt(ex, ids.Length);
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
            this.LogErrorSearchingRecords(ex, query.UserId);
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
            sql = @"SELECT id::text, user_id, session_id, content_type, domain, key,
                           title, value, tags, importance, embedding,
                           created_at, updated_at, last_accessed_at
                    FROM records
                    WHERE user_id = @user_id AND session_id IS NULL AND domain = @domain AND key = @key
                    LIMIT 1;";
        }
        else
        {
            sql = @"SELECT id::text, user_id, session_id, content_type, domain, key,
                           title, value, tags, importance, embedding,
                           created_at, updated_at, last_accessed_at
                    FROM records
                    WHERE user_id = @user_id AND session_id = @session_id AND domain = @domain AND key = @key
                    LIMIT 1;";
        }

        await using var conn = await this._dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)conn);
        cmd.Parameters.AddWithValue("user_id", userId);
        if (sessionId is not null)
        {
            cmd.Parameters.AddWithValue("session_id", sessionId);
        }

        cmd.Parameters.AddWithValue("domain", domain);
        cmd.Parameters.AddWithValue("key", key);

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

        await using var conn = await this._dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)conn);
        cmd.Parameters.AddWithValue("user_id", userId);
        cmd.Parameters.AddWithValue("domain", domain);
        cmd.Parameters.AddWithValue("key", key);

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

        await using var conn = await this._dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)conn);
        cmd.Parameters.AddWithValue("user_id", userId);

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
        await using var conn = await this._dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)conn);
        cmd.Parameters.AddWithValue("user_id", userId);

        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null or DBNull)
        {
            return null;
        }

        var ts = new DateTimeOffset((DateTime)result, TimeSpan.Zero);
        this._lastWrittenCache[userId] = ts;
        return ts;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Record>> GetAllForUserAsync(string userId, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT id::text, user_id, session_id, content_type, domain, key,
                   title, value, tags, importance, embedding,
                   created_at, updated_at, last_accessed_at
            FROM records
            WHERE user_id = @user_id;";

        await using var conn = await this._dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)conn);
        cmd.Parameters.AddWithValue("user_id", userId);

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
        // Compare against the caller-supplied @now (the sweeper's injected clock) rather than the
        // database now() so the staleness window is deterministic under a virtual clock (TI-4).
        const string sql = @"
            DELETE FROM records
            WHERE content_type = @content_type
              AND updated_at < @now - @ttl
              AND (last_accessed_at IS NULL OR last_accessed_at < @now - @ttl);";

        await using var conn = await this._dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)conn);
        cmd.Parameters.AddWithValue("content_type", (short)contentType);
        cmd.Parameters.Add(new NpgsqlParameter("ttl", NpgsqlDbType.Interval) { Value = ttl });
        cmd.Parameters.AddWithValue("now", now.UtcDateTime);

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<int> DeleteWhereLowImportanceStaleAsync(
        double importanceThreshold,
        TimeSpan staleAge,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        // Compare against the caller-supplied @now (the sweeper's injected clock) rather than the
        // database now() so the staleness window is deterministic under a virtual clock (TI-4).
        const string sql = @"
            DELETE FROM records
            WHERE importance < @importance_threshold
              AND (last_accessed_at IS NULL OR last_accessed_at < @now - @stale_age);";

        await using var conn = await this._dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)conn);
        cmd.Parameters.AddWithValue("importance_threshold", importanceThreshold);
        cmd.Parameters.Add(new NpgsqlParameter("stale_age", NpgsqlDbType.Interval) { Value = staleAge });
        cmd.Parameters.AddWithValue("now", now.UtcDateTime);

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

        await using var conn = await this._dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            // DELETE the listed ids.
            if (idsToDelete.Count > 0)
            {
                var guids = idsToDelete
                    .Select(id => Guid.TryParse(id, out var g) ? (Guid?)g : null)
                    .Where(g => g.HasValue)
                    .Select(g => g!.Value)
                    .ToArray();

                if (guids.Length > 0)
                {
                    const string deleteSql = "DELETE FROM records WHERE id = ANY(@ids) AND user_id = @user_id;";
                    await using var deleteCmd = new NpgsqlCommand(deleteSql, conn, tx);
                    deleteCmd.Parameters.Add(new NpgsqlParameter("ids", guids));
                    deleteCmd.Parameters.AddWithValue("user_id", newRecord.UserId);
                    await deleteCmd.ExecuteNonQueryAsync(ct);
                }
            }

            // INSERT the new record.
            const string insertSql = @"
                INSERT INTO records (
                    id, user_id, session_id, content_type, domain, key,
                    title, value, tags, importance, embedding,
                    created_at, updated_at, last_accessed_at)
                VALUES (
                    @id, @user_id, @session_id, @content_type, @domain, @key,
                    @title, @value, @tags, @importance, @embedding,
                    now(), now(), NULL)
                RETURNING id::text, created_at, updated_at;";

            Guid newId = string.IsNullOrEmpty(newRecord.Id) || !Guid.TryParse(newRecord.Id, out var parsedNewId)
                ? Guid.NewGuid()
                : parsedNewId;

            await using var insertCmd = new NpgsqlCommand(insertSql, conn, tx);
            insertCmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = newId });
            insertCmd.Parameters.AddWithValue("user_id", newRecord.UserId);
            insertCmd.Parameters.AddWithValue("session_id", (object?)newRecord.SessionId ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("content_type", (short)newRecord.ContentType);
            insertCmd.Parameters.AddWithValue("domain", newRecord.Domain);
            insertCmd.Parameters.AddWithValue("key", newRecord.Key);
            insertCmd.Parameters.AddWithValue("title", newRecord.Title);
            insertCmd.Parameters.AddWithValue("value", newRecord.Value);
            insertCmd.Parameters.Add(new NpgsqlParameter<string[]>("tags", newRecord.Tags.ToArray()));
            insertCmd.Parameters.AddWithValue("importance", newRecord.Importance);
            insertCmd.Parameters.Add(new NpgsqlParameter("embedding", new Vector(embedding.ToArray())));

            string assignedId;
            DateTimeOffset createdAt;
            DateTimeOffset updatedAt;

            await using (var reader = await insertCmd.ExecuteReaderAsync(ct))
            {
                await reader.ReadAsync(ct);
                assignedId = reader.GetString(0);
                createdAt = new DateTimeOffset(reader.GetDateTime(1), TimeSpan.Zero);
                updatedAt = new DateTimeOffset(reader.GetDateTime(2), TimeSpan.Zero);
            }

            var nowTs = DateTimeOffset.UtcNow;
            const string bumpSql = @"
                INSERT INTO user_state (user_id, last_written_at)
                VALUES (@user_id, @ts)
                ON CONFLICT (user_id) DO UPDATE
                SET last_written_at = GREATEST(user_state.last_written_at, EXCLUDED.last_written_at);";

            await using var bumpCmd = new NpgsqlCommand(bumpSql, conn, tx);
            bumpCmd.Parameters.AddWithValue("user_id", newRecord.UserId);
            bumpCmd.Parameters.AddWithValue("ts", nowTs.UtcDateTime);
            await bumpCmd.ExecuteNonQueryAsync(ct);

            await tx.CommitAsync(ct);
            this._lastWrittenCache[newRecord.UserId] = nowTs;

            this.LogMergeCompleted(idsToDelete.Count, assignedId, newRecord.UserId);

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

        if (!Guid.TryParse(recordId, out var guid))
        {
            return null;
        }

        const string sql = @"
            UPDATE records
            SET value      = COALESCE(@new_value, value),
                importance = COALESCE(@new_importance, importance),
                updated_at = now()
            WHERE id = @id AND user_id = @user_id
            RETURNING id::text, user_id, session_id, content_type, domain, key,
                      title, value, tags, importance, embedding,
                      created_at, updated_at, last_accessed_at;";

        await using var conn = await this._dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)conn);
        cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = guid });
        cmd.Parameters.AddWithValue("user_id", userId);
        cmd.Parameters.AddWithValue("new_value", (object?)newValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("new_importance", (object?)newImportance ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var record = ReadRecord(reader);
        var now = DateTimeOffset.UtcNow;
        reader.Close();
        await BumpLastWrittenAtAsync(conn, userId, now, ct);
        return record;
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteByIdAsync(string recordId, string userId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(recordId, out var guid))
        {
            return false;
        }

        const string sql = "DELETE FROM records WHERE id = @id AND user_id = @user_id;";

        await using var conn = await this._dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)conn);
        cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = guid });
        cmd.Parameters.AddWithValue("user_id", userId);

        int rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows > 0)
        {
            var now = DateTimeOffset.UtcNow;
            await BumpLastWrittenAtAsync(conn, userId, now, ct);
        }

        return rows > 0;
    }

    /// <summary>Logs that a record was upserted.</summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "Upserted record {Id} for user {UserId}")]
    private partial void LogUpsertedRecord(string id, string userId);

    /// <summary>Logs that upserting a record failed.</summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "Error upserting record for user {UserId}")]
    private partial void LogErrorUpsertingRecord(Exception ex, string userId);

    /// <summary>Logs that the fire-and-forget bump of last_accessed_at for search hits failed.</summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to bump last_accessed_at for {Count} records")]
    private partial void LogFailedToBumpLastAccessedAt(Exception ex, int count);

    /// <summary>Logs that searching records failed.</summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "Error searching records for user {UserId}")]
    private partial void LogErrorSearchingRecords(Exception ex, string userId);

    /// <summary>Logs that a merge operation deleted the superseded records and inserted the merged record.</summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "MergeAsync: deleted {Count} records and inserted {Id} for user {UserId}")]
    private partial void LogMergeCompleted(int count, string id, string userId);

    // ── private helpers ───────────────────────────────────────────────────────

    private static string BuildSearchSql(SearchQuery query)
    {
        var where = new System.Text.StringBuilder("WHERE user_id = @user_id");

        if (query.ContentType.HasValue)
        {
            where.Append(" AND content_type = @content_type");
        }

        if (query.Domain is not null)
        {
            where.Append(" AND domain = @domain");
        }

        return $@"
            SELECT id::text, user_id, session_id, content_type, domain, key,
                   title, value, tags, importance, embedding,
                   created_at, updated_at, last_accessed_at,
                   embedding <=> @query_vec AS distance
            FROM records
            {where}
            ORDER BY distance ASC
            LIMIT @top_k;";
    }

    private static Record ReadRecord(Npgsql.NpgsqlDataReader reader)
    {
        string id = reader.GetString(0);
        string userId = reader.GetString(1);
        string? sessionId = reader.IsDBNull(2) ? null : reader.GetString(2);
        var contentType = (ContentType)(short)reader.GetValue(3);
        string domain = reader.GetString(4);
        string key = reader.GetString(5);
        string title = reader.GetString(6);
        string value = reader.GetString(7);
        string[] tags = reader.GetValue(8) as string[] ?? [];
        double importance = reader.GetDouble(9);
        var vector = reader.GetValue(10) as Vector;
        float[] embedding = vector?.ToArray() ?? [];
        var createdAt = new DateTimeOffset(reader.GetDateTime(11), TimeSpan.Zero);
        var updatedAt = new DateTimeOffset(reader.GetDateTime(12), TimeSpan.Zero);
        DateTimeOffset? lastAccessedAt = reader.IsDBNull(13) ? null : new DateTimeOffset(reader.GetDateTime(13), TimeSpan.Zero);

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
            embedding: embedding.AsMemory());
    }

    private async Task BumpLastWrittenAtAsync(
        NpgsqlConnection conn,
        string userId,
        DateTimeOffset ts,
        CancellationToken ct)
    {
        const string sql = @"
            INSERT INTO user_state (user_id, last_written_at)
            VALUES (@user_id, @ts)
            ON CONFLICT (user_id) DO UPDATE
            SET last_written_at = GREATEST(user_state.last_written_at, EXCLUDED.last_written_at);";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("user_id", userId);
        cmd.Parameters.AddWithValue("ts", ts.UtcDateTime);
        await cmd.ExecuteNonQueryAsync(ct);

        this._lastWrittenCache[userId] = ts;
    }

    private async Task BumpLastAccessedAtAsync(string[] ids, CancellationToken ct)
    {
        if (ids.Length == 0)
        {
            return;
        }

        var guids = ids
            .Select(id => Guid.TryParse(id, out var g) ? (Guid?)g : null)
            .Where(g => g.HasValue)
            .Select(g => g!.Value)
            .ToArray();

        if (guids.Length == 0)
        {
            return;
        }

        const string sql = "UPDATE records SET last_accessed_at = now() WHERE id = ANY(@ids);";

        await using var conn = await this._dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)conn);
        cmd.Parameters.AddWithValue("ids", guids);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
