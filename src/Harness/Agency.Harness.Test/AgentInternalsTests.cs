
using System.Text.Json;
using Agency.Harness.Contexts;

namespace Agency.Harness.Test;
/// <summary>
/// Unit tests for <see langword="internal"/> helpers exposed via
/// <c>[InternalsVisibleTo("Agency.Harness.Test")]</c>.
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

    private static ChatMessage TextOnlyMessage() =>
        new(ChatRole.Assistant, [new TextContent("All done.")]);

    private static ChatMessage ToolUseMessage() =>
        new(ChatRole.Assistant, [new FunctionCallContent("id-1", "tool")]);

    [Fact]
    public void DetermineStatus_ReturnsSuccess_WhenLastMessageHasNoToolCalls()
    {
        Context ctx = ContextAtIteration(5);

        AgentResultStatus status = Agent.DetermineStatus(ctx, TextOnlyMessage());

        Assert.Equal(AgentResultStatus.Success, status);
    }

    [Fact]
    public void DetermineStatus_ReturnsMaxStepsReached_WhenStepLimitHitWithPendingToolCalls()
    {
        Context ctx = ContextAtIteration(5);

        AgentResultStatus status = Agent.DetermineStatus(ctx, ToolUseMessage());

        Assert.Equal(AgentResultStatus.MaxStepsReached, status);
    }

    [Fact]
    public void DetermineStatus_ReturnsSuccess_WhenIterationBelowLimitAndNoToolCalls()
    {
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
        var msg = new ChatMessage(ChatRole.Assistant, [new TextContent("Hello!")]);

        string? result = Agent.ExtractFinalText(msg);

        Assert.Equal("Hello!", result);
    }

    [Fact]
    public void ExtractFinalText_ConcatenatesMultipleTextContents()
    {
        // Multiple TextContent items in one message are joined — this happens with
        // interleaved TextReasoningContent between text segments.
        var msg = new ChatMessage(ChatRole.Assistant,
        [
            new TextContent("Part one. "),
            new TextReasoningContent("internal reasoning"),
            new TextContent("Part two."),
        ]);

        string? result = Agent.ExtractFinalText(msg);

        Assert.Equal("Part one. Part two.", result);
    }

    [Fact]
    public void ExtractFinalText_ReturnsNull_WhenNoTextContentsPresent()
    {
        // A message that is purely tool calls has no extractable final text.
        var msg = new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("id-1", "tool")]);

        string? result = Agent.ExtractFinalText(msg);

        Assert.Null(result);
    }

    [Fact]
    public void ExtractFinalText_ReturnsNull_WhenTextContentsAreAllEmpty()
    {
        var msg = new ChatMessage(ChatRole.Assistant,
            [new TextContent(""), new TextContent("")]);

        string? result = Agent.ExtractFinalText(msg);

        Assert.Null(result);
    }

    [Fact]
    public void ExtractFinalText_IgnoresReasoningAndFunctionCallContents()
    {
        var msg = new ChatMessage(ChatRole.Assistant,
        [
            new TextReasoningContent("internal thought"),
            new TextContent("Visible answer."),
            new FunctionCallContent("id-1", "tool"),
        ]);

        string? result = Agent.ExtractFinalText(msg);

        Assert.Equal("Visible answer.", result);
    }

    // ── Agent.ToJsonElement ───────────────────────────────────────────────────

    [Fact]
    public void ToJsonElement_ReturnsEmptyObject_WhenArgumentsIsNull()
    {
        JsonElement result = Agent.ToJsonElement(null);

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Empty(result.EnumerateObject());
    }

    [Fact]
    public void ToJsonElement_ReturnsEmptyObject_WhenArgumentsIsEmpty()
    {
        JsonElement result = Agent.ToJsonElement(new Dictionary<string, object?>());

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Empty(result.EnumerateObject());
    }

    [Fact]
    public void ToJsonElement_SerializesKeyValuePairs()
    {
        var args = new Dictionary<string, object?> { ["x"] = 42, ["y"] = "hello" };

        JsonElement result = Agent.ToJsonElement(args);

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Equal(42, result.GetProperty("x").GetInt32());
        Assert.Equal("hello", result.GetProperty("y").GetString());
    }

    [Fact]
    public void ToJsonElement_HandlesNullValueInDictionary()
    {
        var args = new Dictionary<string, object?> { ["key"] = null };

        JsonElement result = Agent.ToJsonElement(args);

        Assert.Equal(JsonValueKind.Null, result.GetProperty("key").ValueKind);
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
            "anything", System.Text.Json.JsonDocument.Parse("{}").RootElement, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("anything", result.Content);
    }
}
