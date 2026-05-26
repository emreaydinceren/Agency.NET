
using Agency.Agentic.Contexts;

namespace Agency.Agentic.Test;
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

    [Fact]
    public void StepCountIs_ReturnsFalse_WhenIterationBelowLimit()
    {
        StopCondition stop = StopConditions.StepCountIs(5);

        Assert.False(stop(MakeContext(iteration: 4), TextMessage()));
    }

    [Fact]
    public void StepCountIs_ReturnsTrue_WhenIterationEqualsLimit()
    {
        StopCondition stop = StopConditions.StepCountIs(5);

        Assert.True(stop(MakeContext(iteration: 5), TextMessage()));
    }

    [Fact]
    public void StepCountIs_ReturnsTrue_WhenIterationExceedsLimit()
    {
        StopCondition stop = StopConditions.StepCountIs(5);

        Assert.True(stop(MakeContext(iteration: 99), TextMessage()));
    }

    // ── NoToolCalls ───────────────────────────────────────────────────────────

    [Fact]
    public void NoToolCalls_ReturnsTrue_WhenMessageContainsOnlyText()
    {
        Assert.True(StopConditions.NoToolCalls(MakeContext(), TextMessage()));
    }

    [Fact]
    public void NoToolCalls_ReturnsFalse_WhenMessageContainsFunctionCallContent()
    {
        Assert.False(StopConditions.NoToolCalls(MakeContext(), ToolUseMessage()));
    }

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

    [Fact]
    public void BudgetExceeded_ReturnsFalse_WhenCostBelowLimit()
    {
        StopCondition stop = StopConditions.BudgetExceeded(1.00m);

        Assert.False(stop(MakeContext(cost: 0.99m), TextMessage()));
    }

    [Fact]
    public void BudgetExceeded_ReturnsTrue_WhenCostEqualsLimit()
    {
        StopCondition stop = StopConditions.BudgetExceeded(1.00m);

        Assert.True(stop(MakeContext(cost: 1.00m), TextMessage()));
    }

    [Fact]
    public void BudgetExceeded_ReturnsTrue_WhenCostExceedsLimit()
    {
        StopCondition stop = StopConditions.BudgetExceeded(1.00m);

        Assert.True(stop(MakeContext(cost: 2.50m), TextMessage()));
    }

    // ── TokensExceeded ────────────────────────────────────────────────────────

    [Fact]
    public void TokensExceeded_ReturnsFalse_WhenTotalTokensBelowLimit()
    {
        StopCondition stop = StopConditions.TokensExceeded(1000);

        Assert.False(stop(MakeContext(inputTokens: 400, outputTokens: 400), TextMessage()));
    }

    [Fact]
    public void TokensExceeded_ReturnsTrue_WhenTotalTokensEqualsLimit()
    {
        StopCondition stop = StopConditions.TokensExceeded(1000);

        Assert.True(stop(MakeContext(inputTokens: 600, outputTokens: 400), TextMessage()));
    }

    [Fact]
    public void TokensExceeded_ReturnsTrue_WhenTotalTokensExceedsLimit()
    {
        StopCondition stop = StopConditions.TokensExceeded(1000);

        Assert.True(stop(MakeContext(inputTokens: 800, outputTokens: 600), TextMessage()));
    }

    // ── Any ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Any_ReturnsFalse_WhenAllConditionsAreFalse()
    {
        StopCondition combined = StopConditions.Any(
            StopConditions.StepCountIs(100),
            StopConditions.BudgetExceeded(1000m));

        Assert.False(combined(MakeContext(iteration: 1, cost: 0.01m), TextMessage()));
    }

    [Fact]
    public void Any_ReturnsTrue_WhenFirstConditionIsTrue()
    {
        StopCondition combined = StopConditions.Any(
            StopConditions.StepCountIs(1),
            StopConditions.BudgetExceeded(1000m));

        Assert.True(combined(MakeContext(iteration: 1), TextMessage()));
    }

    [Fact]
    public void Any_ReturnsTrue_WhenSecondConditionIsTrue()
    {
        StopCondition combined = StopConditions.Any(
            StopConditions.StepCountIs(100),
            StopConditions.NoToolCalls);

        Assert.True(combined(MakeContext(iteration: 1), TextMessage()));
    }

    [Fact]
    public void Any_ReturnsTrue_WhenAllConditionsAreTrue()
    {
        StopCondition combined = StopConditions.Any(
            StopConditions.NoToolCalls,
            StopConditions.StepCountIs(1));

        Assert.True(combined(MakeContext(iteration: 1), TextMessage()));
    }

    // ── Default combination ───────────────────────────────────────────────────

    [Fact]
    public void DefaultStop_NoToolCallsOrMaxSteps_TriggersOnNoToolCalls()
    {
        StopCondition defaultStop = StopConditions.Any(
            StopConditions.NoToolCalls,
            StopConditions.StepCountIs(20));

        Assert.True(defaultStop(MakeContext(iteration: 1), TextMessage()));
    }

    [Fact]
    public void DefaultStop_DoesNotTrigger_WhenToolsReturnedAndStepsLow()
    {
        StopCondition defaultStop = StopConditions.Any(
            StopConditions.NoToolCalls,
            StopConditions.StepCountIs(20));

        Assert.False(defaultStop(MakeContext(iteration: 1), ToolUseMessage()));
    }
}
