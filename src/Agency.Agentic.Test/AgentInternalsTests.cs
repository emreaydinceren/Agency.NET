namespace Agency.Agentic.Test;

using Agency.Agentic.Contexts;
using System.Text.Json;

/// <summary>
/// Unit tests for <see langword="internal"/> helpers exposed via
/// <c>[InternalsVisibleTo("Agency.Agentic.Test")]</c>.
/// These test logic that would otherwise only be exercised indirectly through the full loop.
/// </summary>
public sealed class AgentInternalsTests
{
    // ── Agent.DetermineStatus ─────────────────────────────────────────────────

    private static Context ContextAtIteration(int iteration)
    {
        var ctx = new Context { Query = new QueryContext { Prompt = "test" } };
        ctx.IterationCount = iteration;
        return ctx;
    }

    private static AgentMessage TextOnlyMessage() =>
        new(MessageRole.Assistant, [new TextBlock("All done.")]);

    private static AgentMessage ToolUseMessage() =>
        new(MessageRole.Assistant,
            [new ToolUseBlock("id-1", "tool", JsonDocument.Parse("{}").RootElement)]);

    [Fact]
    public void DetermineStatus_ReturnsSuccess_WhenLastMessageHasNoToolCalls()
    {
        // StopCountIs fires (iteration == limit), but no pending tool calls → clean finish.
        Context ctx = ContextAtIteration(5);

        AgentResultStatus status = Agent.DetermineStatus(ctx, TextOnlyMessage());

        Assert.Equal(AgentResultStatus.Success, status);
    }

    [Fact]
    public void DetermineStatus_ReturnsMaxStepsReached_WhenStepLimitHitWithPendingToolCalls()
    {
        // Loop was forced to stop mid-flight while the assistant still wanted more tools.
        Context ctx = ContextAtIteration(5);

        AgentResultStatus status = Agent.DetermineStatus(ctx, ToolUseMessage());

        Assert.Equal(AgentResultStatus.MaxStepsReached, status);
    }

    [Fact]
    public void DetermineStatus_ReturnsSuccess_WhenIterationBelowLimitAndNoToolCalls()
    {
        // Stopped early by NoToolCalls, not by step count — should be Success.
        Context ctx = ContextAtIteration(2);

        AgentResultStatus status = Agent.DetermineStatus(ctx, TextOnlyMessage());

        Assert.Equal(AgentResultStatus.Success, status);
    }

    [Fact]
    public void DetermineStatus_ReturnsMaxStepsReached_WhenToolCallsPresentRegardlessOfIteration()
    {
        // DetermineStatus is called after any stop condition fires. If the assistant's
        // last message still has pending tool calls — whether stopped by BudgetExceeded,
        // step count, or any other predicate — the agent didn't finish cleanly.
        Context ctx = ContextAtIteration(2);

        AgentResultStatus status = Agent.DetermineStatus(ctx, ToolUseMessage());

        Assert.Equal(AgentResultStatus.MaxStepsReached, status);
    }

    // ── Agent.ExtractFinalText ────────────────────────────────────────────────

    [Fact]
    public void ExtractFinalText_ReturnsSingleBlockText()
    {
        var msg = new AgentMessage(MessageRole.Assistant, [new TextBlock("Hello!")]);

        string? result = Agent.ExtractFinalText(msg);

        Assert.Equal("Hello!", result);
    }

    [Fact]
    public void ExtractFinalText_ConcatenatesMultipleTextBlocks()
    {
        // Multiple TextBlocks in one message are joined — this happens with
        // interleaved ThinkingBlocks between text segments.
        var msg = new AgentMessage(MessageRole.Assistant,
        [
            new TextBlock("Part one. "),
            new ThinkingBlock("internal reasoning"),
            new TextBlock("Part two."),
        ]);

        string? result = Agent.ExtractFinalText(msg);

        Assert.Equal("Part one. Part two.", result);
    }

    [Fact]
    public void ExtractFinalText_ReturnsNull_WhenNoTextBlocksPresent()
    {
        // A message that is purely tool calls has no extractable final text.
        var msg = new AgentMessage(MessageRole.Assistant,
            [new ToolUseBlock("id-1", "tool", JsonDocument.Parse("{}").RootElement)]);

        string? result = Agent.ExtractFinalText(msg);

        Assert.Null(result);
    }

    [Fact]
    public void ExtractFinalText_ReturnsNull_WhenTextBlocksAreAllEmpty()
    {
        var msg = new AgentMessage(MessageRole.Assistant,
            [new TextBlock(""), new TextBlock("")]);

        string? result = Agent.ExtractFinalText(msg);

        Assert.Null(result);
    }

    [Fact]
    public void ExtractFinalText_IgnoresThinkingAndToolUseBlocks()
    {
        var msg = new AgentMessage(MessageRole.Assistant,
        [
            new ThinkingBlock("internal thought"),
            new TextBlock("Visible answer."),
            new ToolUseBlock("id-1", "tool", JsonDocument.Parse("{}").RootElement),
        ]);

        string? result = Agent.ExtractFinalText(msg);

        Assert.Equal("Visible answer.", result);
    }

    // ── EmptyToolRegistry ─────────────────────────────────────────────────────

    [Fact]
    public void EmptyToolRegistry_ListDefinitions_ReturnsEmpty()
    {
        IToolRegistry registry = EmptyToolRegistry.Instance;

        Assert.Empty(registry.ListDefinitions());
    }

    [Fact]
    public async Task EmptyToolRegistry_InvokeAsync_ReturnsErrorResult()
    {
        IToolRegistry registry = EmptyToolRegistry.Instance;

        ToolResult result = await registry.InvokeAsync(
            "anything", JsonDocument.Parse("{}").RootElement, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("anything", result.Content);
    }
}
