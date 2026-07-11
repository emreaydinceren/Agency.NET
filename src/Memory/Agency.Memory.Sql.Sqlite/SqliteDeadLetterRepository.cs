using System.Globalization;
using System.Text.Json;
using Agency.Memory.Common.Storage;
using Microsoft.Data.Sqlite;

namespace Agency.Memory.Sql.Sqlite;

/// <summary>
/// Persists failed distillation and consolidation jobs to the <c>dead_letter</c> table
/// for operational inspection. The table is never read by the live pipeline.
/// </summary>
/// <remarks>
/// Records are written by the Distiller and Consolidator when a job exhausts its retries
/// or encounters a permanent error (Spec §8.6). The contents are meant for manual inspection
/// and potential replay tooling in future versions.
/// </remarks>
public sealed class SqliteDeadLetterRepository : IDeadLetterStore
{
    private readonly string _connectionString;

    /// <summary>
    /// Creates a new <see cref="SqliteDeadLetterRepository"/>.
    /// </summary>
    /// <param name="connectionString">The SQLite connection string.</param>
    public SqliteDeadLetterRepository(string connectionString)
    {
        this._connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    /// <summary>
    /// Writes a failed job to the dead-letter table.
    /// </summary>
    /// <param name="userId">The user whose job failed.</param>
    /// <param name="sessionId">The session, or <see langword="null"/> for consolidation jobs.</param>
    /// <param name="jobKind">A short label identifying the job type (e.g. <c>"distillation"</c>).</param>
    /// <param name="payload">The job payload; serialised to JSON.</param>
    /// <param name="exception">The exception that caused the failure.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteAsync(
        string userId,
        string? sessionId,
        string jobKind,
        object payload,
        Exception exception,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exception);

        const string sql = @"
            INSERT INTO dead_letter (id, user_id, session_id, job_kind, job_payload, error, stack, created_at)
            VALUES (@id, @user_id, @session_id, @job_kind, @job_payload, @error, @stack, @created_at);";

        string json = JsonSerializer.Serialize(payload);
        string now = DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

        await using var conn = new SqliteConnection(this._connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("@user_id", userId);
        cmd.Parameters.AddWithValue("@session_id", (object?)sessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@job_kind", jobKind);
        cmd.Parameters.AddWithValue("@job_payload", json);
        cmd.Parameters.AddWithValue("@error", exception.Message);
        cmd.Parameters.AddWithValue("@stack", (object?)exception.StackTrace ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@created_at", now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Returns all dead-letter entries created after the specified cutoff time,
    /// ordered by <c>created_at</c> ascending.
    /// </summary>
    /// <param name="cutoff">Only return entries created after this time.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of dead-letter entries as raw DTOs.</returns>
    public async Task<IReadOnlyList<DeadLetterEntry>> ListSinceAsync(
        DateTimeOffset cutoff,
        CancellationToken ct = default)
    {
        const string sql = @"
            SELECT id, user_id, session_id, job_kind, job_payload, error, created_at
            FROM dead_letter
            WHERE created_at > @cutoff
            ORDER BY created_at ASC;";

        string cutoffStr = cutoff.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

        await using var conn = new SqliteConnection(this._connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@cutoff", cutoffStr);

        var results = new List<DeadLetterEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string createdAtRaw = reader.GetString(6);
            var createdAt = DateTimeOffset.Parse(createdAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            createdAt = new DateTimeOffset(createdAt.UtcDateTime, TimeSpan.Zero);

            results.Add(new DeadLetterEntry(
                Id: reader.GetString(0),
                UserId: reader.GetString(1),
                SessionId: reader.IsDBNull(2) ? null : reader.GetString(2),
                JobKind: reader.GetString(3),
                JobPayloadJson: reader.GetString(4),
                Error: reader.GetString(5),
                CreatedAt: createdAt));
        }

        return results;
    }
}

/// <summary>
/// A dead-letter table row, returned by <see cref="SqliteDeadLetterRepository.ListSinceAsync"/>.
/// </summary>
/// <param name="Id">The surrogate primary key.</param>
/// <param name="UserId">The affected user.</param>
/// <param name="SessionId">The session, if applicable.</param>
/// <param name="JobKind">Short label for the job type.</param>
/// <param name="JobPayloadJson">The job payload serialised as JSON.</param>
/// <param name="Error">The error message.</param>
/// <param name="CreatedAt">When the entry was written.</param>
public sealed record DeadLetterEntry(
    string Id,
    string UserId,
    string? SessionId,
    string JobKind,
    string JobPayloadJson,
    string Error,
    DateTimeOffset CreatedAt);