namespace Agency.Memory.Common.Storage;

/// <summary>
/// Narrow abstraction over distillation-watermark persistence, implemented by each storage provider.
/// A watermark records the last conversation-turn index successfully distilled for a
/// <c>(userId, sessionId)</c> pair, enabling idempotent re-runs after process restarts.
/// </summary>
/// <remarks>
/// Lives in <c>Agency.Memory.Common</c> (alongside <see cref="IMemoryStore"/>) so the Distiller can
/// depend on it without referencing any concrete provider, and so a host can select a provider at
/// composition time.
/// </remarks>
public interface IWatermarkStore
{
    /// <summary>
    /// Gets the current watermark for the given session, or 0 if none has been recorded.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The last distilled turn index, or 0 if unknown.</returns>
    Task<int> GetAsync(string userId, string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Advances the watermark to <paramref name="candidate"/> if it is greater than the stored value.
    /// Never moves the watermark backwards.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="candidate">The candidate new watermark value.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The effective (post-update) watermark value.</returns>
    Task<int> AdvanceAsync(string userId, string sessionId, int candidate, CancellationToken ct = default);
}
