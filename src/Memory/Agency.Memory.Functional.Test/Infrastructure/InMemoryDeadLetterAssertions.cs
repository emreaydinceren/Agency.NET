using Agency.Memory.Sql.Postgres;
using Npgsql;
using Xunit;

namespace Agency.Memory.Functional.Test.Infrastructure;

/// <summary>
/// Test helper that queries the <c>dead_letter</c> Postgres table by
/// <c>(jobKind, userId)</c> and asserts presence or absence of entries,
/// with optional partial matching on the <c>error</c> column.
/// </summary>
/// <remarks>
/// Used by Group 5 (Failure &amp; Recovery) tests together with
/// <see cref="FaultInjectingLlmClient"/> to verify that the Distiller and
/// Consolidator correctly dead-letter permanently-failed jobs
/// (Memory-Specifications.md §8.6).
/// </remarks>
internal sealed class InMemoryDeadLetterAssertions
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>
    /// Initialises a new <see cref="InMemoryDeadLetterAssertions"/> bound to
    /// <paramref name="dataSource"/>.
    /// </summary>
    /// <param name="dataSource">The Npgsql data source for the test schema.</param>
    internal InMemoryDeadLetterAssertions(NpgsqlDataSource dataSource)
    {
        this._dataSource = dataSource
            ?? throw new ArgumentNullException(nameof(dataSource));
    }

    // ── Query helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all dead-letter entries that match <paramref name="jobKind"/>
    /// and <paramref name="userId"/>, ordered by <c>created_at</c> ascending.
    /// </summary>
    /// <param name="jobKind">The job-kind label (e.g. <c>"distillation"</c>).</param>
    /// <param name="userId">The user identifier to filter on.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching dead-letter entries.</returns>
    internal async Task<IReadOnlyList<DeadLetterEntry>> QueryAsync(
        string jobKind,
        string userId,
        CancellationToken ct = default)
    {
        const string sql = @"
            SELECT id::text, user_id, session_id, job_kind, job_payload::text, error, created_at
            FROM dead_letter
            WHERE job_kind = @job_kind AND user_id = @user_id
            ORDER BY created_at ASC;";

        await using var conn = await this._dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, (NpgsqlConnection)conn);
        cmd.Parameters.AddWithValue("job_kind", jobKind);
        cmd.Parameters.AddWithValue("user_id", userId);

        var results = new List<DeadLetterEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new DeadLetterEntry(
                Id: reader.GetString(0),
                UserId: reader.GetString(1),
                SessionId: reader.IsDBNull(2) ? null : reader.GetString(2),
                JobKind: reader.GetString(3),
                JobPayloadJson: reader.GetString(4),
                Error: reader.GetString(5),
                CreatedAt: new DateTimeOffset(reader.GetDateTime(6), TimeSpan.Zero)));
        }

        return results;
    }

    // ── Assertion helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Asserts that at least one dead-letter entry exists for the given
    /// <paramref name="jobKind"/> and <paramref name="userId"/>.
    /// Optionally checks that the <c>error</c> column contains
    /// <paramref name="errorSubstring"/> (case-insensitive).
    /// </summary>
    /// <param name="jobKind">The job-kind label to filter on.</param>
    /// <param name="userId">The user identifier to filter on.</param>
    /// <param name="errorSubstring">
    /// Optional substring the <c>error</c> column must contain, or
    /// <see langword="null"/> to skip the substring check.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="Xunit.Sdk.XunitException">
    /// Thrown when the assertion fails.
    /// </exception>
    internal async Task AssertHasEntryAsync(
        string jobKind,
        string userId,
        string? errorSubstring = null,
        CancellationToken ct = default)
    {
        IReadOnlyList<DeadLetterEntry> entries = await this.QueryAsync(jobKind, userId, ct);

        Assert.True(
            entries.Count > 0,
            $"Expected at least one dead-letter entry for jobKind='{jobKind}', userId='{userId}' but found none.");

        if (errorSubstring is not null)
        {
            bool anyMatch = entries.Any(e =>
                e.Error.Contains(errorSubstring, StringComparison.OrdinalIgnoreCase));

            Assert.True(
                anyMatch,
                $"No dead-letter entry for jobKind='{jobKind}', userId='{userId}' " +
                $"has an error containing '{errorSubstring}'. " +
                $"Actual errors: {string.Join("; ", entries.Select(e => e.Error))}");
        }
    }

    /// <summary>
    /// Asserts that no dead-letter entry exists for the given
    /// <paramref name="jobKind"/> and <paramref name="userId"/>.
    /// </summary>
    /// <param name="jobKind">The job-kind label to filter on.</param>
    /// <param name="userId">The user identifier to filter on.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="Xunit.Sdk.XunitException">
    /// Thrown when the assertion fails (i.e. entries are present).
    /// </exception>
    internal async Task AssertNoEntryAsync(
        string jobKind,
        string userId,
        CancellationToken ct = default)
    {
        IReadOnlyList<DeadLetterEntry> entries = await this.QueryAsync(jobKind, userId, ct);

        Assert.True(
            entries.Count == 0,
            $"Expected no dead-letter entries for jobKind='{jobKind}', userId='{userId}' " +
            $"but found {entries.Count}. " +
            $"Errors: {string.Join("; ", entries.Select(e => e.Error))}");
    }
}
