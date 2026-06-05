using System.Collections.Concurrent;
using System.Globalization;
using Agency.Memory.Common.Storage;
using Microsoft.Data.Sqlite;

namespace Agency.Memory.Sql.Sqlite;

/// <summary>
/// Persists and retrieves per-session distillation watermarks backed by SQLite.
/// A watermark records the last conversation-turn index that was successfully distilled
/// for a given <c>(userId, sessionId)</c> pair, enabling idempotent re-runs after process restarts.
/// </summary>
/// <remarks>
/// Values are monotonically non-decreasing: <see cref="AdvanceAsync"/> uses
/// <c>MAX(stored, candidate)</c> semantics and never moves the watermark backwards.
/// An in-memory cache is maintained for hot-path reads; a fresh DB read is performed on cache miss
/// to hydrate after restart.
/// </remarks>
public sealed class SqliteWatermarkRepository : IWatermarkStore
{
    private readonly string _connectionString;

    /// <summary>Cache keyed by <c>"userId:sessionId"</c>.</summary>
    private readonly ConcurrentDictionary<string, int> _cache = new();

    /// <summary>
    /// Creates a new <see cref="SqliteWatermarkRepository"/>.
    /// </summary>
    /// <param name="connectionString">The SQLite connection string used to open connections.</param>
    public SqliteWatermarkRepository(string connectionString)
    {
        this._connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    /// <summary>
    /// Gets the current watermark for the given session.
    /// Returns 0 if no watermark has been recorded yet.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The last distilled turn index, or 0 if unknown.</returns>
    public async Task<int> GetAsync(string userId, string sessionId, CancellationToken ct = default)
    {
        var cacheKey = CacheKey(userId, sessionId);
        if (this._cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        const string sql = @"
            SELECT last_distilled_turn_idx
            FROM watermarks
            WHERE user_id = @user_id AND session_id = @session_id;";

        await using var conn = new SqliteConnection(this._connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@user_id", userId);
        cmd.Parameters.AddWithValue("@session_id", sessionId);

        var result = await cmd.ExecuteScalarAsync(ct);
        int value = result is null or DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
        this._cache[cacheKey] = value;
        return value;
    }

    /// <summary>
    /// Advances the watermark to <paramref name="candidate"/> if it is greater than the stored value.
    /// Uses <c>MAX(stored, candidate)</c> semantics so the watermark never moves backwards.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="candidate">The candidate new watermark value.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The effective (post-update) watermark value.</returns>
    public async Task<int> AdvanceAsync(string userId, string sessionId, int candidate, CancellationToken ct = default)
    {
        string now = DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

        const string sql = @"
            INSERT INTO watermarks (user_id, session_id, last_distilled_turn_idx, last_updated_at)
            VALUES (@user_id, @session_id, @idx, @now)
            ON CONFLICT (user_id, session_id) DO UPDATE
            SET last_distilled_turn_idx =
                    MAX(watermarks.last_distilled_turn_idx, excluded.last_distilled_turn_idx),
                last_updated_at = @now
            RETURNING last_distilled_turn_idx;";

        await using var conn = new SqliteConnection(this._connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@user_id", userId);
        cmd.Parameters.AddWithValue("@session_id", sessionId);
        cmd.Parameters.AddWithValue("@idx", candidate);
        cmd.Parameters.AddWithValue("@now", now);

        var result = await cmd.ExecuteScalarAsync(ct);
        int effective = result is null or DBNull ? candidate : Convert.ToInt32(result, CultureInfo.InvariantCulture);
        this._cache[CacheKey(userId, sessionId)] = effective;
        return effective;
    }

    /// <summary>
    /// Deletes the watermark row for the given session (optional cleanup on session disposal).
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task DeleteAsync(string userId, string sessionId, CancellationToken ct = default)
    {
        const string sql = "DELETE FROM watermarks WHERE user_id = @user_id AND session_id = @session_id;";

        await using var conn = new SqliteConnection(this._connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@user_id", userId);
        cmd.Parameters.AddWithValue("@session_id", sessionId);
        await cmd.ExecuteNonQueryAsync(ct);

        this._cache.TryRemove(CacheKey(userId, sessionId), out _);
    }

    private static string CacheKey(string userId, string sessionId) => $"{userId}:{sessionId}";
}