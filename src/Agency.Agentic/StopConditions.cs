namespace Agency.Agentic;

using Agency.Agentic.Contexts;

/// <summary>
/// A predicate evaluated after each assistant turn to decide whether the loop should halt.
/// </summary>
/// <param name="ctx">The current session context (iteration count, usage, cost, etc.).</param>
/// <param name="lastResponse">The most recent assistant message.</param>
/// <returns><see langword="true"/> to stop; <see langword="false"/> to continue.</returns>
public delegate bool StopCondition(Context ctx, AgentMessage lastResponse);

/// <summary>
/// Factory methods for the most common <see cref="StopCondition"/> delegates. Compose multiple conditions with
/// <see cref="Any"/>.
/// </summary>
public static class StopConditions
{
    /// <summary>Stops when <see cref="Context.IterationCount"/> reaches <paramref name="n"/>.</summary>
    public static StopCondition StepCountIs(int n) =>
        (ctx, _) => ctx.IterationCount >= n;

    /// <summary>Stops when the last assistant message contains no <see cref="ToolUseBlock"/>s.</summary>
    public static readonly StopCondition NoToolCalls =
        (_, msg) => !msg.Content.OfType<ToolUseBlock>().Any();

    /// <summary>Stops when accumulated cost reaches or exceeds <paramref name="usd"/>.</summary>
    public static StopCondition BudgetExceeded(decimal usd) =>
        (ctx, _) => ctx.TotalCostUsd >= usd;

    /// <summary>Stops when accumulated tokens reach or exceed <paramref name="total"/>.</summary>
    public static StopCondition TokensExceeded(long total) =>
        (ctx, _) => ctx.TotalUsage.TotalTokens >= total;

    /// <summary>Stops when any of the supplied <paramref name="conditions"/> returns <see langword="true"/>.</summary>
    public static StopCondition Any(params StopCondition[] conditions) =>
        (ctx, msg) => conditions.Any(c => c(ctx, msg));
}
