
using Agency.Harness.Contexts;

namespace Agency.Harness.Test;
/// <summary>
/// Unit tests for the <see cref="StopConditions"/> factory methods and delegates.
/// </summary>
public sealed class StopConditionsTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Context MakeContext(
        int iteration = 0,
        decimal cost = 0m,
        long inputTokens = 0,
        long outputTokens = 0)
    {
        var ctx = new Context { Query = new QueryContext { Prompt = "test" } };
        ctx.IterationCount = iteration;
        ctx.TotalCostUsd = cost;
        ctx.TotalUsage = new LlmTokenUsage(inputTokens, outputTokens);
        return ctx;
    }

    private static ChatMessage TextMessage(string text = "done") =>
        new(ChatRole.Assistant, [new TextContent(text)]);

    private static ChatMessage ToolUseMessage(string toolName = "my_tool") =>
        new(ChatRole.Assistant, [new FunctionCallContent("id-1", toolName)]);

    // ── StepCountIs ───────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="StopConditions.StepCountIs"/> does not fire while the iteration count is below
    /// the configured limit.
    /// </summary>
    [Fact]
    public void StepCountIs_ReturnsFalse_WhenIterationBelowLimit()
    {
        StopCondition stop = StopConditions.StepCountIs(5);

        Assert.False(stop(MakeContext(iteration: 4), TextMessage()));
    }

    /// <summary>
    /// <see cref="StopConditions.StepCountIs"/> fires once the iteration count equals the
    /// configured limit.
    /// </summary>
    [Fact]
    public void StepCountIs_ReturnsTrue_WhenIterationEqualsLimit()
    {
        StopCondition stop = StopConditions.StepCountIs(5);

        Assert.True(stop(MakeContext(iteration: 5), TextMessage()));
    }

    /// <summary>
    /// <see cref="StopConditions.StepCountIs"/> stays true once the iteration count has exceeded
    /// the configured limit.
    /// </summary>
    [Fact]
    public void StepCountIs_ReturnsTrue_WhenIterationExceedsLimit()
    {
        StopCondition stop = StopConditions.StepCountIs(5);

        Assert.True(stop(MakeContext(iteration: 99), TextMessage()));
    }

    // ── NoToolCalls ───────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="StopConditions.NoToolCalls"/> fires for a text-only assistant message.
    /// </summary>
    [Fact]
    public void NoToolCalls_ReturnsTrue_WhenMessageContainsOnlyText()
    {
        Assert.True(StopConditions.NoToolCalls(MakeContext(), TextMessage()));
    }

    /// <summary>
    /// <see cref="StopConditions.NoToolCalls"/> does not fire for a message consisting solely of
    /// a function-call block.
    /// </summary>
    [Fact]
    public void NoToolCalls_ReturnsFalse_WhenMessageContainsFunctionCallContent()
    {
        Assert.False(StopConditions.NoToolCalls(MakeContext(), ToolUseMessage()));
    }

    /// <summary>
    /// <see cref="StopConditions.NoToolCalls"/> does not fire when a message mixes text with a
    /// function-call block — any pending tool call counts.
    /// </summary>
    [Fact]
    public void NoToolCalls_ReturnsFalse_WhenMessageContainsTextAndFunctionCallContent()
    {
        var mixed = new ChatMessage(
            ChatRole.Assistant,
            [
                new TextContent("I will call a tool."),
                new FunctionCallContent("id-1", "tool_a"),
            ]);

        Assert.False(StopConditions.NoToolCalls(MakeContext(), mixed));
    }

    // ── BudgetExceeded ────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="StopConditions.BudgetExceeded"/> does not fire while accumulated cost is below
    /// the configured limit.
    /// </summary>
    [Fact]
    public void BudgetExceeded_ReturnsFalse_WhenCostBelowLimit()
    {
        StopCondition stop = StopConditions.BudgetExceeded(1.00m);

        Assert.False(stop(MakeContext(cost: 0.99m), TextMessage()));
    }

    /// <summary>
    /// <see cref="StopConditions.BudgetExceeded"/> fires once accumulated cost equals the
    /// configured limit.
    /// </summary>
    [Fact]
    public void BudgetExceeded_ReturnsTrue_WhenCostEqualsLimit()
    {
        StopCondition stop = StopConditions.BudgetExceeded(1.00m);

        Assert.True(stop(MakeContext(cost: 1.00m), TextMessage()));
    }

    /// <summary>
    /// <see cref="StopConditions.BudgetExceeded"/> stays true once accumulated cost has exceeded
    /// the configured limit.
    /// </summary>
    [Fact]
    public void BudgetExceeded_ReturnsTrue_WhenCostExceedsLimit()
    {
        StopCondition stop = StopConditions.BudgetExceeded(1.00m);

        Assert.True(stop(MakeContext(cost: 2.50m), TextMessage()));
    }

    // ── TokensExceeded ────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="StopConditions.TokensExceeded"/> does not fire while the sum of input and
    /// output tokens is below the configured limit.
    /// </summary>
    [Fact]
    public void TokensExceeded_ReturnsFalse_WhenTotalTokensBelowLimit()
    {
        StopCondition stop = StopConditions.TokensExceeded(1000);

        Assert.False(stop(MakeContext(inputTokens: 400, outputTokens: 400), TextMessage()));
    }

    /// <summary>
    /// <see cref="StopConditions.TokensExceeded"/> fires once the sum of input and output tokens
    /// equals the configured limit.
    /// </summary>
    [Fact]
    public void TokensExceeded_ReturnsTrue_WhenTotalTokensEqualsLimit()
    {
        StopCondition stop = StopConditions.TokensExceeded(1000);

        Assert.True(stop(MakeContext(inputTokens: 600, outputTokens: 400), TextMessage()));
    }

    /// <summary>
    /// <see cref="StopConditions.TokensExceeded"/> stays true once the sum of input and output
    /// tokens has exceeded the configured limit.
    /// </summary>
    [Fact]
    public void TokensExceeded_ReturnsTrue_WhenTotalTokensExceedsLimit()
    {
        StopCondition stop = StopConditions.TokensExceeded(1000);

        Assert.True(stop(MakeContext(inputTokens: 800, outputTokens: 600), TextMessage()));
    }

    // ── Any ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="StopConditions.Any"/> returns <see langword="false"/> when none of its combined
    /// conditions fire.
    /// </summary>
    [Fact]
    public void Any_ReturnsFalse_WhenAllConditionsAreFalse()
    {
        StopCondition combined = StopConditions.Any(
            StopConditions.StepCountIs(100),
            StopConditions.BudgetExceeded(1000m));

        Assert.False(combined(MakeContext(iteration: 1, cost: 0.01m), TextMessage()));
    }

    /// <summary>
    /// <see cref="StopConditions.Any"/> short-circuits to <see langword="true"/> when the first
    /// combined condition fires.
    /// </summary>
    [Fact]
    public void Any_ReturnsTrue_WhenFirstConditionIsTrue()
    {
        StopCondition combined = StopConditions.Any(
            StopConditions.StepCountIs(1),
            StopConditions.BudgetExceeded(1000m));

        Assert.True(combined(MakeContext(iteration: 1), TextMessage()));
    }

    /// <summary>
    /// <see cref="StopConditions.Any"/> returns <see langword="true"/> when only the second
    /// combined condition fires.
    /// </summary>
    [Fact]
    public void Any_ReturnsTrue_WhenSecondConditionIsTrue()
    {
        StopCondition combined = StopConditions.Any(
            StopConditions.StepCountIs(100),
            StopConditions.NoToolCalls);

        Assert.True(combined(MakeContext(iteration: 1), TextMessage()));
    }

    /// <summary>
    /// <see cref="StopConditions.Any"/> returns <see langword="true"/> when every combined
    /// condition fires.
    /// </summary>
    [Fact]
    public void Any_ReturnsTrue_WhenAllConditionsAreTrue()
    {
        StopCondition combined = StopConditions.Any(
            StopConditions.NoToolCalls,
            StopConditions.StepCountIs(1));

        Assert.True(combined(MakeContext(iteration: 1), TextMessage()));
    }

    // ── Default combination ───────────────────────────────────────────────────

    /// <summary>
    /// The default combination of <see cref="StopConditions.NoToolCalls"/> and
    /// <see cref="StopConditions.StepCountIs"/> stops the loop as soon as a tool-free message
    /// arrives, even far below the step limit.
    /// </summary>
    [Fact]
    public void DefaultStop_NoToolCallsOrMaxSteps_TriggersOnNoToolCalls()
    {
        StopCondition defaultStop = StopConditions.Any(
            StopConditions.NoToolCalls,
            StopConditions.StepCountIs(20));

        Assert.True(defaultStop(MakeContext(iteration: 1), TextMessage()));
    }

    /// <summary>
    /// The default combination of <see cref="StopConditions.NoToolCalls"/> and
    /// <see cref="StopConditions.StepCountIs"/> does not stop the loop while a tool call is still
    /// pending and the step limit has not been reached.
    /// </summary>
    [Fact]
    public void DefaultStop_DoesNotTrigger_WhenToolsReturnedAndStepsLow()
    {
        StopCondition defaultStop = StopConditions.Any(
            StopConditions.NoToolCalls,
            StopConditions.StepCountIs(20));

        Assert.False(defaultStop(MakeContext(iteration: 1), ToolUseMessage()));
    }
}
