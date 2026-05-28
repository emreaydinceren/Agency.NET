namespace Agency.Memory.Distiller.Services;

/// <summary>
/// Narrow interface over watermark persistence operations needed by the Distiller.
/// </summary>
/// <remarks>
/// Exists to decouple <see cref="DistillerBackgroundService"/> from the concrete
/// <see cref="Agency.Memory.Sql.Postgres.WatermarkRepository"/> so the Distiller is unit-testable
/// without a database.
/// </remarks>
internal interface IWatermarkStore
{
    /// <summary>
    /// Gets the current watermark for the given session, or 0 if none has been recorded.
    /// </summary>
    Task<int> GetAsync(string userId, string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Advances the watermark to <paramref name="candidate"/> if it is greater than the stored value.
    /// </summary>
    Task<int> AdvanceAsync(string userId, string sessionId, int candidate, CancellationToken ct = default);
}
