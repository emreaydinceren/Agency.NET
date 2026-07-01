
using System.Text.Json;
using Agency.Harness.Contexts;
using Agency.Harness.Hooks;
using Agency.Harness.Test.Fakes;

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

    /// <summary>
    /// A message with no pending tool calls yields <see cref="AgentResultStatus.Success"/>,
    /// even at a high iteration count.
    /// </summary>
    [Fact]
    public void DetermineStatus_ReturnsSuccess_WhenLastMessageHasNoToolCalls()
    {
        Context ctx = ContextAtIteration(5);

        AgentResultStatus status = Agent.DetermineStatus(ctx, TextOnlyMessage());

        Assert.Equal(AgentResultStatus.Success, status);
    }

    /// <summary>
    /// A message that still has pending tool calls yields <see cref="AgentResultStatus.MaxStepsReached"/>.
    /// </summary>
    [Fact]
    public void DetermineStatus_ReturnsMaxStepsReached_WhenStepLimitHitWithPendingToolCalls()
    {
        Context ctx = ContextAtIteration(5);

        AgentResultStatus status = Agent.DetermineStatus(ctx, ToolUseMessage());

        Assert.Equal(AgentResultStatus.MaxStepsReached, status);
    }

    /// <summary>
    /// A message with no pending tool calls yields <see cref="AgentResultStatus.Success"/>
    /// at a low iteration count too, confirming the iteration count itself has no bearing.
    /// </summary>
    [Fact]
    public void DetermineStatus_ReturnsSuccess_WhenIterationBelowLimitAndNoToolCalls()
    {
        Context ctx = ContextAtIteration(2);

        AgentResultStatus status = Agent.DetermineStatus(ctx, TextOnlyMessage());

        Assert.Equal(AgentResultStatus.Success, status);
    }

    /// <summary>
    /// The presence of pending tool calls alone drives <see cref="AgentResultStatus.MaxStepsReached"/>;
    /// the iteration count is not consulted.
    /// </summary>
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

    /// <summary>
    /// A message containing a single <c>TextContent</c> block returns that block's text verbatim.
    /// </summary>
    [Fact]
    public void ExtractFinalText_ReturnsSingleBlockText()
    {
        var msg = new ChatMessage(ChatRole.Assistant, [new TextContent("Hello!")]);

        string? result = Agent.ExtractFinalText(msg);

        Assert.Equal("Hello!", result);
    }

    /// <summary>
    /// Multiple <c>TextContent</c> blocks in one message are concatenated in order.
    /// </summary>
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

    /// <summary>
    /// A message consisting solely of tool-call content has no extractable text and returns
    /// <see langword="null"/>.
    /// </summary>
    [Fact]
    public void ExtractFinalText_ReturnsNull_WhenNoTextContentsPresent()
    {
        // A message that is purely tool calls has no extractable final text.
        var msg = new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("id-1", "tool")]);

        string? result = Agent.ExtractFinalText(msg);

        Assert.Null(result);
    }

    /// <summary>
    /// Text content blocks that are all empty strings concatenate to an empty string, which is
    /// normalized to <see langword="null"/> rather than returned as-is.
    /// </summary>
    [Fact]
    public void ExtractFinalText_ReturnsNull_WhenTextContentsAreAllEmpty()
    {
        var msg = new ChatMessage(ChatRole.Assistant,
            [new TextContent(""), new TextContent("")]);

        string? result = Agent.ExtractFinalText(msg);

        Assert.Null(result);
    }

    /// <summary>
    /// Reasoning and function-call content blocks are skipped; only the interleaved
    /// <c>TextContent</c> block contributes to the extracted text.
    /// </summary>
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

    /// <summary>
    /// A <see langword="null"/> arguments dictionary serializes to an empty JSON object,
    /// not a JSON null.
    /// </summary>
    [Fact]
    public void ToJsonElement_ReturnsEmptyObject_WhenArgumentsIsNull()
    {
        JsonElement result = Agent.ToJsonElement(null);

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Empty(result.EnumerateObject());
    }

    /// <summary>
    /// An empty arguments dictionary serializes to an empty JSON object.
    /// </summary>
    [Fact]
    public void ToJsonElement_ReturnsEmptyObject_WhenArgumentsIsEmpty()
    {
        JsonElement result = Agent.ToJsonElement(new Dictionary<string, object?>());

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Empty(result.EnumerateObject());
    }

    /// <summary>
    /// Each key/value pair in the arguments dictionary is serialized as a matching JSON
    /// property, preserving the original value's type.
    /// </summary>
    [Fact]
    public void ToJsonElement_SerializesKeyValuePairs()
    {
        var args = new Dictionary<string, object?> { ["x"] = 42, ["y"] = "hello" };

        JsonElement result = Agent.ToJsonElement(args);

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Equal(42, result.GetProperty("x").GetInt32());
        Assert.Equal("hello", result.GetProperty("y").GetString());
    }

    /// <summary>
    /// A dictionary value of <see langword="null"/> serializes to a JSON null property value.
    /// </summary>
    [Fact]
    public void ToJsonElement_HandlesNullValueInDictionary()
    {
        var args = new Dictionary<string, object?> { ["key"] = null };

        JsonElement result = Agent.ToJsonElement(args);

        Assert.Equal(JsonValueKind.Null, result.GetProperty("key").ValueKind);
    }

    // ── Agent.RaiseSessionEndAsync ────────────────────────────────────────────

    private static Context MakeSessionContext(string sessionId)
    {
        var ctx = new Context { Query = new QueryContext { Prompt = "test" } };
        ctx.Session = ctx.Session with { Id = sessionId };
        return ctx;
    }

    /// <summary>
    /// When an <see cref="AgentHooks.OnSessionEnd"/> hook is configured, invoking it passes the
    /// current session's id through the hook context.
    /// </summary>
    [Fact]
    public async Task RaiseSessionEndAsync_WithHook_InvokesHookWithSessionId()
    {
        string? capturedSessionId = null;
        var hooks = new AgentHooks
        {
            OnSessionEnd = (hc, _) =>
            {
                capturedSessionId = hc.SessionId;
                return Task.CompletedTask;
            },
        };

        var llm = new FakeChatClient();
        var agent = new Agent(llm, "model", hooks: hooks);
        Context ctx = MakeSessionContext("session-abc");

        await agent.RaiseSessionEndAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal("session-abc", capturedSessionId);
    }

    /// <summary>
    /// When no <see cref="AgentHooks.OnSessionEnd"/> hook is configured, raising the session-end
    /// event is a no-op that completes without throwing.
    /// </summary>
    [Fact]
    public async Task RaiseSessionEndAsync_NoHook_DoesNotThrow()
    {
        var llm = new FakeChatClient();
        var agent = new Agent(llm, "model");
        Context ctx = MakeSessionContext("session-xyz");

        // Must complete without throwing when no OnSessionEnd hook is set.
        await agent.RaiseSessionEndAsync(ctx, TestContext.Current.CancellationToken);
    }

    // ── EmptyToolRegistry ─────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="EmptyToolRegistry"/> exposes no tool definitions.
    /// </summary>
    [Fact]
    public void EmptyToolRegistry_ListDefinitions_ReturnsEmpty()
    {
        IToolRegistry registry = EmptyToolRegistry.Instance;

        Assert.Empty(registry.ListDefinitions());
    }

    /// <summary>
    /// Invoking any tool name against <see cref="EmptyToolRegistry"/> returns an error result
    /// that echoes the requested tool name back in its content.
    /// </summary>
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
