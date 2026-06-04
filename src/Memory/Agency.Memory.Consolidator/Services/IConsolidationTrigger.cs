namespace Agency.Memory.Consolidator.Services;

/// <summary>
/// Provides an explicit entry point for hosts that configure
/// <see cref="Common.Options.ConsolidationTrigger.Manual"/> mode.
/// </summary>
/// <remarks>
/// When <see cref="Common.Options.ConsolidatorOptions.Trigger"/> is
/// <see cref="Common.Options.ConsolidationTrigger.Manual"/>, the consolidator does
/// <em>not</em> auto-run on <c>DistillationCompletedEvent</c>. Hosts must call
/// <see cref="RequestAsync"/> to trigger a consolidation pass for a specific user.
/// </remarks>
public interface IConsolidationTrigger
{
    /// <summary>
    /// Enqueues a consolidation pass for the specified user.
    /// </summary>
    /// <param name="userId">The user to consolidate memory for.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RequestAsync(string userId, CancellationToken ct = default);
}
