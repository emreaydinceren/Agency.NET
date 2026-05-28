namespace Agency.Memory.Distiller.Services;

/// <summary>
/// Narrow interface over dead-letter persistence operations needed by the Distiller.
/// </summary>
/// <remarks>
/// Exists to decouple <see cref="DistillerBackgroundService"/> from the concrete
/// <see cref="Agency.Memory.Sql.Postgres.DeadLetterRepository"/> so the Distiller is unit-testable
/// without a database.
/// </remarks>
internal interface IDeadLetterStore
{
    /// <summary>
    /// Writes a failed job to the dead-letter store.
    /// </summary>
    Task WriteAsync(
        string userId,
        string? sessionId,
        string jobKind,
        object payload,
        Exception error,
        CancellationToken ct = default);
}
