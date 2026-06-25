namespace Agency.Harness.Loop;

/// <summary>
/// Default caps and Goalkeeper-client configuration for a <c>LoopRunner</c>.
/// A <see cref="GoalSpec"/> armed during a run overrides these per-cap defaults for that run (§9).
/// Deliverable F will bind this from <c>appsettings.json</c> / <c>IConfiguration</c> and add
/// OpenTelemetry options; keep this minimal for now.
/// </summary>
public sealed class LoopOptions
{
    /// <summary>
    /// Name of the <see cref="IChatClient"/> used for the Goalkeeper.
    /// Should differ from the worker client to mitigate self-preference bias (§6.3, E-4).
    /// <see langword="null"/> = use the same client resolution as the worker.
    /// </summary>
    public string? GoalkeeperClientName { get; init; }

    /// <summary>
    /// Model id passed to the Goalkeeper's <see cref="IChatClient"/> via
    /// <see cref="Microsoft.Extensions.AI.ChatOptions.ModelId"/> (gotcha 7).
    /// <see langword="null"/> = no explicit model override.
    /// </summary>
    public string? GoalkeeperModel { get; init; }

    /// <summary>
    /// Default hard ceiling on continuation turns when no <see cref="GoalSpec.MaxTurns"/>
    /// is supplied by the armed goal. The armed goal always wins.
    /// </summary>
    public int MaxTurns { get; init; } = 12;

    /// <summary>
    /// Default USD spend ceiling across the full loop. <see langword="null"/> = off.
    /// Overridden per-run by <see cref="GoalSpec.Budget"/>.
    /// </summary>
    public decimal? Budget { get; init; }

    /// <summary>
    /// Default total-token ceiling. <see langword="null"/> = off.
    /// Overridden per-run by <see cref="GoalSpec.TokenBudget"/>.
    /// </summary>
    public long? TokenBudget { get; init; }

    /// <summary>
    /// Default per-loop wall-clock timeout in seconds. <see langword="null"/> = off.
    /// Overridden per-run by <see cref="GoalSpec.WallClockSeconds"/>.
    /// </summary>
    public int? WallClockSeconds { get; init; }

    /// <summary>
    /// Optional extra strictness instructions appended to the Goalkeeper system prompt
    /// (how strict the evaluation should be, what counts as proof).
    /// </summary>
    public string? GoalkeeperRubric { get; init; }
}
