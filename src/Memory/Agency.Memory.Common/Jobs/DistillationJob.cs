namespace Agency.Memory.Common.Jobs;

/// <summary>
/// Payload enqueued to the distillation channel when one of the three distillation
/// triggers fires (per Spec §6.2).
/// </summary>
/// <param name="UserId">The owning user.</param>
/// <param name="SessionId">The session whose turns are to be distilled.</param>
/// <param name="Trigger">Why distillation was triggered.</param>
/// <param name="UpToTurnIndex">Snapshot of the last turn index at trigger time; the Distiller processes turns in (watermark, UpToTurnIndex].</param>
/// <param name="TriggerSummary">Optional hint from the triggering agent (e.g., goal description from <c>MarkGoalComplete</c>).</param>
public sealed record DistillationJob(
    string UserId,
    string SessionId,
    DistillationTrigger Trigger,
    int UpToTurnIndex,
    string? TriggerSummary = null);
