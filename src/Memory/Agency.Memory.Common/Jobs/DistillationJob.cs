using Agency.Harness.Contexts;

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
/// <param name="Focus">Snapshot of <see cref="Context.Focus"/> at trigger time (Spec §6.7.1 / P2). Defaults to <see cref="FocusContext.Empty"/> when no focus was active.</param>
public sealed record DistillationJob(
    string UserId,
    string SessionId,
    DistillationTrigger Trigger,
    int UpToTurnIndex,
    string? TriggerSummary = null,
    FocusContext? Focus = null);
