using Agency.Harness;

namespace Agency.Memory.Common.Events;

/// <summary>
/// Terminal marker for a distillation job that has reached a final outcome — either
/// <see cref="DistillationCompletedEvent"/> (success) or <see cref="DistillationFailedEvent"/>
/// (permanent failure / dead-letter).
/// </summary>
/// <remarks>
/// Subscribe to this base type via <see cref="IAsyncEventBus.Subscribe{T}"/> to observe a
/// distillation job <em>settling</em> regardless of its outcome — for example to release a waiter
/// without racing the completed and failed event types separately. This gives the distiller the
/// same always-emit-a-terminal-event symmetry the consolidator already has with
/// <see cref="ConsolidationCompletedEvent"/> (TI-8.1).
/// </remarks>
/// <param name="UserId">The user whose distillation settled.</param>
/// <param name="SessionId">The affected session.</param>
public abstract record DistillationSettledEvent(string UserId, string SessionId) : AgentEvent;

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
    int NewWatermark) : DistillationSettledEvent(UserId, SessionId);

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
    bool DeadLettered) : DistillationSettledEvent(UserId, SessionId);

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

/// <summary>
/// Emitted by the consolidator sub-agent each time it mutates the memory store through one of
/// its tools (<c>Memory_Merge</c> / <c>Memory_Update</c> / <c>Memory_Delete</c>).
/// </summary>
/// <remarks>
/// This is a first-class causal observable for autonomous memory changes (TI-8.3). Hosts are
/// expected to surface these to the user — telling the user when the agent has reorganised its
/// own long-term memory. That is unconventional for a background process but intentional for this
/// product: memory edits the user never typed should be transparent.
/// </remarks>
/// <param name="UserId">The user whose memory was mutated.</param>
/// <param name="Operation">The mutation kind: <c>"Merge"</c>, <c>"Update"</c>, or <c>"Delete"</c>.</param>
/// <param name="Detail">A human-readable description of the change (the tool's result text).</param>
public sealed record MemoryMutatedEvent(
    string UserId,
    string Operation,
    string Detail) : AgentEvent;
