using Agency.Agentic;

namespace Agency.Memory.Common.Events;

/// <summary>
/// Emitted by <c>DistillerBackgroundService</c> when a distillation job completes successfully.
/// </summary>
/// <param name="UserId">The user whose turns were distilled.</param>
/// <param name="SessionId">The session whose turns were distilled.</param>
/// <param name="RecordsWritten">The number of memory records persisted.</param>
/// <param name="NewWatermark">The new <c>LastDistilledTurnIndex</c> after this job.</param>
public sealed record DistillationCompletedEvent(
    string UserId,
    string SessionId,
    int RecordsWritten,
    int NewWatermark) : AgentEvent;

/// <summary>
/// Emitted by <c>DistillerBackgroundService</c> when a distillation job fails permanently
/// (after retries exhausted or on a permanent error class).
/// </summary>
/// <param name="UserId">The user whose distillation failed.</param>
/// <param name="SessionId">The affected session.</param>
/// <param name="Reason">A human-readable description of the failure.</param>
/// <param name="DeadLettered">
/// <see langword="true"/> if the job was written to the dead-letter store;
/// <see langword="false"/> if the failure was not dead-lettered (e.g., programmer error).
/// </param>
public sealed record DistillationFailedEvent(
    string UserId,
    string SessionId,
    string Reason,
    bool DeadLettered) : AgentEvent;

/// <summary>
/// Emitted by <c>ConsolidatorBackgroundService</c> when a consolidation pass completes.
/// </summary>
/// <param name="UserId">The user whose records were consolidated.</param>
/// <param name="Merges">The number of records merged.</param>
/// <param name="Updates">The number of records updated.</param>
/// <param name="Deletes">The number of records deleted.</param>
public sealed record ConsolidationCompletedEvent(
    string UserId,
    int Merges,
    int Updates,
    int Deletes) : AgentEvent;
