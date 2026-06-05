namespace Agency.Memory.Common.Storage;

/// <summary>
/// Narrow abstraction over dead-letter persistence, implemented by each storage provider.
/// Failed distillation and consolidation jobs are written here for operational inspection;
/// the live pipeline never reads from it.
/// </summary>
/// <remarks>
/// Lives in <c>Agency.Memory.Common</c> (alongside <see cref="IMemoryStore"/>) so the Distiller can
/// depend on it without referencing any concrete provider, and so a host can select a provider at
/// composition time.
/// </remarks>
public interface IDeadLetterStore
{
    /// <summary>
    /// Writes a failed job to the dead-letter store.
    /// </summary>
    /// <param name="userId">The user whose job failed.</param>
    /// <param name="sessionId">The session, or <see langword="null"/> for consolidation jobs.</param>
    /// <param name="jobKind">A short label identifying the job type (e.g. <c>"distillation"</c>).</param>
    /// <param name="payload">The job payload; serialised by the provider.</param>
    /// <param name="error">The exception that caused the failure.</param>
    /// <param name="ct">Cancellation token.</param>
    Task WriteAsync(
        string userId,
        string? sessionId,
        string jobKind,
        object payload,
        Exception error,
        CancellationToken ct = default);
}
