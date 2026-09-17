using Agency.Acp.Errors;
using Agency.Harness;
using dotacp.protocol;

namespace Agency.Acp.Turns;

/// <summary>
/// Maps a terminal <see cref="AgentResultEvent"/> to the ACP <see cref="StopReason"/> the client
/// sees, per spec §8.3. <see cref="AgentResultStatus.Error"/> is deliberately NOT mapped to a stop
/// reason: it throws an <see cref="AcpJsonRpcException"/> instead, because a harness error must
/// become a JSON-RPC error response, never a silent <c>end_turn</c> (spec P6).
/// </summary>
/// <remarks>
/// Cancellation has no terminal <see cref="AgentResultEvent"/> to map — the harness signals it by
/// throwing <see cref="OperationCanceledException"/> (spec §6.4, §8.3), and a turn timeout signals
/// by throwing <see cref="TimeoutException"/> instead (spec §6.9 D-5) so it is distinguishable from
/// a user cancel. <see cref="TurnDriver"/> handles both directly in its own exception handling
/// (using <see cref="ForTimeout"/> for the latter) rather than routing them through
/// <see cref="MapTerminal"/>, which only ever sees a real terminal event.
/// </remarks>
internal static class StopReasonMapper
{
    /// <summary>
    /// Maps <paramref name="result"/> to the <see cref="StopReason"/> reported to the client.
    /// </summary>
    /// <exception cref="AcpJsonRpcException">
    /// <paramref name="result"/>'s status is <see cref="AgentResultStatus.Error"/>, or
    /// <see cref="AgentResultStatus.BudgetExceeded"/> — never produced in practice (spec §8.3), but
    /// mapped to a loud failure rather than silently, on the same P6 principle as <c>Error</c>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="result"/>'s status is <see cref="AgentResultStatus.AwaitingPermission"/>.
    /// That status is always consumed by the permission bridge (spec §6.4) before a terminal event
    /// reaches this mapper — reaching here with it is a driver bug, not a mappable outcome.
    /// </exception>
    public static StopReason MapTerminal(AgentResultEvent result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Status switch
        {
            AgentResultStatus.Success => StopReason.EndTurn,
            AgentResultStatus.MaxStepsReached => StopReason.MaxTurnRequests,
            AgentResultStatus.Truncated => StopReason.MaxTokens,
            AgentResultStatus.Error => throw new AcpJsonRpcException(
                ErrorCode.InternalError, result.FinalText ?? "The agent turn failed."),
            AgentResultStatus.BudgetExceeded => throw new AcpJsonRpcException(
                ErrorCode.InternalError,
                "The agent turn stopped on a budget condition, which Agency.Acp does not price (spec §3)."),
            AgentResultStatus.AwaitingPermission => throw new InvalidOperationException(
                "AwaitingPermission must be consumed by the permission bridge before mapping; it must never reach StopReasonMapper."),
            _ => throw new ArgumentOutOfRangeException(nameof(result), result.Status, "Unknown AgentResultStatus."),
        };
    }

    /// <summary>
    /// Builds the JSON-RPC error reported when the turn's own timeout fired (spec §6.9 D-5, §8.3) —
    /// distinct from a plain <see cref="StopReason.Cancelled"/> response so a supervisor can tell
    /// "the model hung" from "the human hit Stop".
    /// </summary>
    public static AcpJsonRpcException ForTimeout(TimeoutException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return new AcpJsonRpcException(ErrorCode.InternalError, ex.Message);
    }
}
