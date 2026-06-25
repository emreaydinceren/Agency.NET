namespace Agency.Harness.Loop;

/// <summary>
/// Session-scoped holder for the active goal. The <c>LoopRunner</c> reads it each turn;
/// the <see cref="EnableGoalkeeperTool"/> / <see cref="DisableGoalkeeperTool"/> tools and
/// the host mutate it. <see langword="null"/> = no goal armed = plain single-turn behaviour.
/// </summary>
internal sealed class GoalState
{
    /// <summary>
    /// Gets the currently armed goal, or <see langword="null"/> when no goal is active.
    /// </summary>
    public GoalSpec? Active { get; private set; }

    /// <summary>
    /// Gets a value indicating whether a goal is currently armed.
    /// Equivalent to <c>Active is not null</c>.
    /// </summary>
    public bool IsArmed => this.Active is not null;

    /// <summary>
    /// Arms the goalkeeper with the given <paramref name="spec"/>, replacing any prior goal.
    /// Idempotent — a second call replaces the first (newest goal wins, §6.4 / E-10).
    /// </summary>
    /// <param name="spec">The goal specification to arm.</param>
    public void Arm(GoalSpec spec) => this.Active = spec;

    /// <summary>
    /// Disarms the goalkeeper. Sets <see cref="Active"/> to <see langword="null"/>.
    /// Safe to call when no goal is armed (no-op).
    /// </summary>
    public void Clear() => this.Active = null;
}
