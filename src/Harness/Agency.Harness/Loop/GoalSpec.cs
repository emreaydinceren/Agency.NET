namespace Agency.Harness.Loop;

/// <summary>
/// What "done" means for a loop run, plus the hard structural ceilings.
/// Passed to <c>enable_goalkeeper</c> (model-armed) or <c>GoalState.Arm</c> (host-armed).
/// </summary>
public sealed record GoalSpec
{
    /// <summary>
    /// The verifiable end state expressed in plain language (≤ 4 000 chars).
    /// Required — the Goalkeeper judges every turn against this string.
    /// </summary>
    public required string Condition { get; init; }

    /// <summary>
    /// Maximum number of continuation turns before the loop exits with
    /// <see cref="LoopOutcome.CapReached"/>. Enforced in code — not prompt-skippable.
    /// </summary>
    public int MaxTurns { get; init; } = 12;

    /// <summary>
    /// USD spend ceiling across the full loop. <see langword="null"/> = off.
    /// When exceeded the loop exits with <see cref="LoopOutcome.BudgetExceeded"/>.
    /// </summary>
    public decimal? Budget { get; init; }

    /// <summary>
    /// Total-token ceiling across the full loop. <see langword="null"/> = off.
    /// When exceeded the loop exits with <see cref="LoopOutcome.BudgetExceeded"/>.
    /// </summary>
    public long? TokenBudget { get; init; }

    /// <summary>
    /// Per-loop wall-clock timeout in seconds. <see langword="null"/> = off.
    /// Implemented as a linked <see cref="System.Threading.CancellationTokenSource"/>.
    /// </summary>
    public int? WallClockSeconds { get; init; }
}
