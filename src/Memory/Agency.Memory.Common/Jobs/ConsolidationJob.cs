namespace Agency.Memory.Common.Jobs;

/// <summary>
/// Payload enqueued to the consolidation channel after a distillation completes
/// for a session (per Spec §6.3).
/// </summary>
/// <param name="UserId">The user whose memory corpus should be consolidated.</param>
/// <param name="TriggeredBySessionId">The session whose distillation completion triggered this consolidation pass.</param>
public sealed record ConsolidationJob(
    string UserId,
    string TriggeredBySessionId);
