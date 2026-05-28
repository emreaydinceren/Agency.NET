using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Agency.Memory.Sql.Postgres;

/// <summary>
/// Persists failed distillation and consolidation jobs to the <c>dead_letter</c> table
/// for operational inspection. The table is never read by the live pipeline.
/// </summary>
/// <remarks>
/// Records are written by the Distiller and Consolidator when a job exhausts its retries
/// or encounters a permanent error (Spec §8.6). The contents are meant for manual inspection
/// and potential replay tooling in future versions.
/// </remarks>
public sealed class DeadLetterRepository
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>
    /// Creates a new <see cref="DeadLetterRepository"/>.
    /// </summary>
    /// <param name="dataSource">The Npgsql data source.</param>
    public DeadLetterRepository(NpgsqlDataSource dataSource)
    {
        this._dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    /// <summary>
    /// Writes a failed job to the dead-letter table.
    /// </summary>
    /// <param name="userId">The user whose job failed.</param>
    /// <param name="sessionId">The session, or <see langword="null"/> for consolidation jobs.</param>
    /// <param name="jobKind">A short label identifying the job type (e.g. <c>"distillation"</c>).</param>
    /// <param name="payload">The job payload; serialised to JSONB.</param>
    /// <param name="error">The exception that caused the failure.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteAsync(
        string userId,
        string? sessionId,
        string jobKind,
        object payload,
        Exception error,
        CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO dead_letter (user_id, session_id, job_kind, job_payload, error, stack, created_at)
            VALUES (@user_id, @session_id, @job_kind, @job_payload, @error, @stack, now());";

        string json = JsonSerializer.Serialize(payload);

        await using var conn = await this._dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)conn);
        cmd.Parameters.AddWithValue("user_id", userId);
        cmd.Parameters.AddWithValue("session_id", (object?)sessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("job_kind", jobKind);
        cmd.Parameters.Add(new NpgsqlParameter("job_payload", NpgsqlDbType.Jsonb) { Value = json });
        cmd.Parameters.AddWithValue("error", error.Message);
        cmd.Parameters.AddWithValue("stack", (object?)error.StackTrace ?? DBNull.Value);
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
            SELECT id::text, user_id, session_id, job_kind, job_payload, error, created_at
            FROM dead_letter
            WHERE created_at > @cutoff
            ORDER BY created_at ASC;";

        await using var conn = await this._dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)conn);
        cmd.Parameters.AddWithValue("cutoff", cutoff.UtcDateTime);

        var results = new List<DeadLetterEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string payloadJson = reader.GetValue(4) switch
            {
                System.Text.Json.JsonDocument doc => doc.RootElement.GetRawText(),
                System.Text.Json.JsonElement el => el.GetRawText(),
                _ => reader.GetValue(4)?.ToString() ?? "{}"
            };

            results.Add(new DeadLetterEntry(
                Id: reader.GetString(0),
                UserId: reader.GetString(1),
                SessionId: reader.IsDBNull(2) ? null : reader.GetString(2),
                JobKind: reader.GetString(3),
                JobPayloadJson: payloadJson,
                Error: reader.GetString(5),
                CreatedAt: new DateTimeOffset(reader.GetDateTime(6), TimeSpan.Zero)));
        }

        return results;
    }
}

/// <summary>
/// A dead-letter table row, returned by <see cref="DeadLetterRepository.ListSinceAsync"/>.
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
