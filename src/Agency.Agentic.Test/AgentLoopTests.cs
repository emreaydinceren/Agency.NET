using System.Text.Json;
using Agency.Agentic.Test.Fakes;

namespace Agency.Agentic.Test;

/// <summary>
/// Functional tests for the <see cref="Agent"/> loop.
/// All tests use a <see cref="FakeLlmClient"/> — no real LLM is needed.
/// </summary>
public sealed class AgentLoopTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Builds a minimal context with the given prompt and optional tools.</summary>
    private static Context MakeContext(string prompt = "Hello", ToolContext? tools = null) =>
        new()
        {
            Query = new QueryContext { Prompt = prompt },
            Tools = tools ?? ToolContext.Empty,
        };

    /// <summary>Builds an <see cref="AgentLlmResponse"/> that contains only a text block.</summary>
    private static AgentLlmResponse TextResponse(string text, int inputTokens = 10, int outputTokens = 5) =>
        new(
            new AgentMessage(MessageRole.Assistant, [new TextBlock(text)]),
            StopReason.EndTurn,
            new LlmTokenUsage(inputTokens, outputTokens));

    /// <summary>Builds an <see cref="AgentLlmResponse"/> that requests one or more tool calls.</summary>
    private static AgentLlmResponse ToolCallResponse(params (string id, string name)[] calls)
    {
        IReadOnlyList<ContentBlock> blocks = calls
            .Select(c => (ContentBlock)new ToolUseBlock(c.id, c.name, JsonDocument.Parse("{}").RootElement))
            .ToList();

        return new AgentLlmResponse(
            new AgentMessage(MessageRole.Assistant, blocks),
            StopReason.ToolUse,
            new LlmTokenUsage(20, 10));
    }

    /// <summary>Collects all events from the agent's async enumerable into a list.</summary>
    private static async Task<List<AgentEvent>> RunToCompletion(
        Agent agent, Context ctx, CancellationToken ct = default)
    {
        var events = new List<AgentEvent>();
        await foreach (var evt in agent.RunAsync(ctx, ct))
        {
            events.Add(evt);
        }

        return events;
    }

    // ── Session bookends ───────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_AlwaysEmitsSessionStartedEventFirst()
    {
        var llm = new FakeLlmClient();
        llm.EnqueueAgentResponse(TextResponse("Paris."));

        var agent = new Agent(llm, "claude-3-5-sonnet", stream: false);
        var events = await RunToCompletion(agent, MakeContext());

        Assert.IsType<SessionStartedEvent>(events[0]);
    }

    [Fact]
    public async Task RunAsync_AlwaysEmitsAgentResultEventLast()
    {
        var llm = new FakeLlmClient();
        llm.EnqueueAgentResponse(TextResponse("Done."));

        var agent = new Agent(llm, "claude-3-5-sonnet", stream: false);
        var events = await RunToCompletion(agent, MakeContext());

        Assert.IsType<AgentResultEvent>(events[^1]);
    }

    [Fact]
    public async Task RunAsync_SessionStartedEvent_HasNonEmptySessionId()
    {
        var llm = new FakeLlmClient();
        llm.EnqueueAgentResponse(TextResponse("ok"));

        var agent = new Agent(llm, "model", stream: false);
        var events = await RunToCompletion(agent, MakeContext());

        var sessionEvent = Assert.IsType<SessionStartedEvent>(events[0]);
        Assert.NotEmpty(sessionEvent.SessionId);
    }

    // ── Single-turn happy path ─────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_SingleTurn_EmitsCorrectEventSequence()
    {
        // Arrange
        var llm = new FakeLlmClient();
        llm.EnqueueAgentResponse(TextResponse("Paris is the capital of France."));

        var agent = new Agent(llm, "model", stream: false);

        // Act
        var events = await RunToCompletion(agent, MakeContext("What is the capital of France?"));

        // Assert: SessionStarted → AssistantTurn → IterationCompleted → AgentResult
        Assert.Collection(events,
            e => Assert.IsType<SessionStartedEvent>(e),
            e => Assert.IsType<AssistantTurnEvent>(e),
            e => Assert.IsType<IterationCompletedEvent>(e),
            e => Assert.IsType<AgentResultEvent>(e));
    }

    [Fact]
    public async Task RunAsync_SingleTurn_ResultStatusIsSuccess()
    {
        var llm = new FakeLlmClient();
        llm.EnqueueAgentResponse(TextResponse("Done."));

        var agent = new Agent(llm, "model", stream: false);
        var events = await RunToCompletion(agent, MakeContext());

        var result = events.OfType<AgentResultEvent>().Single();
        Assert.Equal(AgentResultStatus.Success, result.Status);
    }

    [Fact]
    public async Task RunAsync_SingleTurn_FinalTextMatchesAssistantResponse()
    {
        var llm = new FakeLlmClient();
        llm.EnqueueAgentResponse(TextResponse("The answer is 42."));

        var agent = new Agent(llm, "model", stream: false);
        var events = await RunToCompletion(agent, MakeContext());

        var result = events.OfType<AgentResultEvent>().Single();
        Assert.Equal("The answer is 42.", result.FinalText);
    }

    [Fact]
    public async Task RunAsync_SingleTurn_AccumulatesTokenUsage()
    {
        var llm = new FakeLlmClient();
        llm.EnqueueAgentResponse(TextResponse("ok", inputTokens: 100, outputTokens: 50));

        var agent = new Agent(llm, "model", stream: false);
        var events = await RunToCompletion(agent, MakeContext());

        var result = events.OfType<AgentResultEvent>().Single();
        Assert.Equal(100, result.TotalUsage.InputTokens);
        Assert.Equal(50, result.TotalUsage.OutputTokens);
    }

    // ── Conversation seeding ───────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_SeedsConversation_WithUserPromptAsFirstMessage()
    {
        var llm = new FakeLlmClient();
        llm.EnqueueAgentResponse(TextResponse("ok"));

        var ctx = MakeContext("My specific prompt");
        var agent = new Agent(llm, "model", stream: false);
        await RunToCompletion(agent, ctx);

        // The first message sent to the LLM must be the user's prompt.
        var firstSentMessages = llm.ReceivedMessages[0];
        Assert.Equal(MessageRole.User, firstSentMessages[0].Role);
        var textBlock = Assert.IsType<TextBlock>(firstSentMessages[0].Content[0]);
        Assert.Equal("My specific prompt", textBlock.Text);
    }

    [Fact]
    public async Task RunAsync_DoesNotDuplicatePrompt_WhenConversationAlreadyHasMessages()
    {
        var llm = new FakeLlmClient();
        llm.EnqueueAgentResponse(TextResponse("ok"));

        var ctx = MakeContext("Prompt");
        var existingMsg = new AgentMessage(MessageRole.User, [new TextBlock("Existing message")]);
        ctx.Conversation.Append(existingMsg);

        var agent = new Agent(llm, "model", stream: false);
        await RunToCompletion(agent, ctx);

        // Only the pre-existing message — the prompt should NOT be re-seeded.
        Assert.Single(llm.ReceivedMessages[0]);
        Assert.Equal("Existing message", ((TextBlock)llm.ReceivedMessages[0][0].Content[0]).Text);
    }

    // ── Tool call → execution → continue ──────────────────────────────────────

    [Fact]
    public async Task RunAsync_ToolCall_InvokesTool_AndContinuesLoop()
    {
        // Arrange
        var tool = new FakeTool("search");
        var registry = new ToolRegistry([tool]);
        var llm = new FakeLlmClient();

        // Turn 1: LLM requests a tool call.
        llm.EnqueueAgentResponse(ToolCallResponse(("use-1", "search")));
        // Turn 2: LLM sees the result and finishes.
        llm.EnqueueAgentResponse(TextResponse("Search is done."));

        var ctx = MakeContext(tools: new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model", stream: false);

        // Act
        await RunToCompletion(agent, ctx);

        // Assert: tool was invoked once
        Assert.Equal(1, tool.InvokeCount);
        // LLM was called twice
        Assert.Equal(2, llm.SendAgentCallCount);
    }

    [Fact]
    public async Task RunAsync_ToolCall_EmitsToolInvokedEvent()
    {
        var tool = new FakeTool("calculator", _ => new ToolResult("42"));
        var registry = new ToolRegistry([tool]);
        var llm = new FakeLlmClient();

        llm.EnqueueAgentResponse(ToolCallResponse(("use-1", "calculator")));
        llm.EnqueueAgentResponse(TextResponse("The result is 42."));

        var ctx = MakeContext(tools: new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model", stream: false);
        var events = await RunToCompletion(agent, ctx);

        var toolEvent = events.OfType<ToolInvokedEvent>().Single();
        Assert.Equal("calculator", toolEvent.ToolName);
        Assert.Equal("42", toolEvent.Result.Content);
        Assert.False(toolEvent.Result.IsError);
    }

    [Fact]
    public async Task RunAsync_ToolCall_AppendsPairedToolResultToConversation()
    {
        var tool = new FakeTool("search", _ => new ToolResult("result content"));
        var registry = new ToolRegistry([tool]);
        var llm = new FakeLlmClient();

        llm.EnqueueAgentResponse(ToolCallResponse(("use-abc", "search")));
        llm.EnqueueAgentResponse(TextResponse("Done."));

        var ctx = MakeContext(tools: new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model", stream: false);
        await RunToCompletion(agent, ctx);

        // The second LLM call must include a user message with the ToolResultBlock.
        var turn2Messages = llm.ReceivedMessages[1];
        var toolResultMessage = turn2Messages.Last(m => m.Role == MessageRole.User);
        var resultBlock = toolResultMessage.Content.OfType<ToolResultBlock>().Single();

        Assert.Equal("use-abc", resultBlock.ToolUseId);
        Assert.Equal("result content", resultBlock.Content);
        Assert.False(resultBlock.IsError);
    }

    // ── Parallel tool execution ────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_MultipleToolCalls_InvokesAllToolsInParallel()
    {
        var toolA = new FakeTool("tool_a", _ => new ToolResult("A result"));
        var toolB = new FakeTool("tool_b", _ => new ToolResult("B result"));
        var registry = new ToolRegistry([toolA, toolB]);
        var llm = new FakeLlmClient();

        // Single assistant turn that requests two tool calls simultaneously.
        llm.EnqueueAgentResponse(ToolCallResponse(("id-1", "tool_a"), ("id-2", "tool_b")));
        llm.EnqueueAgentResponse(TextResponse("Both results received."));

        var ctx = MakeContext(tools: new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model", stream: false);
        var events = await RunToCompletion(agent, ctx);

        Assert.Equal(1, toolA.InvokeCount);
        Assert.Equal(1, toolB.InvokeCount);

        var toolEvents = events.OfType<ToolInvokedEvent>().ToList();
        Assert.Equal(2, toolEvents.Count);
    }

    [Fact]
    public async Task RunAsync_MultipleToolCalls_ResultBlocksPreserveOrder()
    {
        var toolA = new FakeTool("tool_a", _ => new ToolResult("A"));
        var toolB = new FakeTool("tool_b", _ => new ToolResult("B"));
        var registry = new ToolRegistry([toolA, toolB]);
        var llm = new FakeLlmClient();

        llm.EnqueueAgentResponse(ToolCallResponse(("id-1", "tool_a"), ("id-2", "tool_b")));
        llm.EnqueueAgentResponse(TextResponse("Done."));

        var ctx = MakeContext(tools: new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model", stream: false);
        await RunToCompletion(agent, ctx);

        // Regardless of execution order, result blocks must match original tool-use IDs in sequence.
        var resultMsg = llm.ReceivedMessages[1].Last(m => m.Role == MessageRole.User);
        var resultBlocks = resultMsg.Content.OfType<ToolResultBlock>().ToList();

        Assert.Equal(2, resultBlocks.Count);
        Assert.Equal("id-1", resultBlocks[0].ToolUseId);
        Assert.Equal("id-2", resultBlocks[1].ToolUseId);
    }

    // ── Tool failure handling ──────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ToolThrows_CapturesErrorAsToolResultBlock()
    {
        var brokenTool = new FakeTool("broken", _ => throw new InvalidOperationException("Tool exploded"));
        var registry = new ToolRegistry([brokenTool]);
        var llm = new FakeLlmClient();

        llm.EnqueueAgentResponse(ToolCallResponse(("use-1", "broken")));
        llm.EnqueueAgentResponse(TextResponse("I saw the error and I handled it."));

        var ctx = MakeContext(tools: new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model", stream: false);
        var events = await RunToCompletion(agent, ctx);

        // The ToolInvokedEvent must mark the result as an error.
        var toolEvent = events.OfType<ToolInvokedEvent>().Single();
        Assert.True(toolEvent.Result.IsError);
        Assert.Contains("Tool exploded", toolEvent.Result.Content);

        // The loop must continue and reach a successful result.
        var result = events.OfType<AgentResultEvent>().Single();
        Assert.Equal(AgentResultStatus.Success, result.Status);
    }

    [Fact]
    public async Task RunAsync_ToolThrows_OtherToolsInSameTurnAreNotCancelled()
    {
        var brokenTool = new FakeTool("broken", _ => throw new InvalidOperationException("fail"));
        var goodTool = new FakeTool("good", _ => new ToolResult("ok"));
        var registry = new ToolRegistry([brokenTool, goodTool]);
        var llm = new FakeLlmClient();

        llm.EnqueueAgentResponse(ToolCallResponse(("id-1", "broken"), ("id-2", "good")));
        llm.EnqueueAgentResponse(TextResponse("Handled both."));

        var ctx = MakeContext(tools: new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model", stream: false);
        await RunToCompletion(agent, ctx);

        // Both tools should have been invoked, despite one failing.
        Assert.Equal(1, brokenTool.InvokeCount);
        Assert.Equal(1, goodTool.InvokeCount);
    }

    // ── Max steps ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_MaxStepsReached_EmitsMaxStepsReachedStatus()
    {
        var tool = new FakeTool("looping_tool");
        var registry = new ToolRegistry([tool]);
        var llm = new FakeLlmClient();

        // Both iterations return tool calls — the loop never sees "no tool calls".
        llm.EnqueueAgentResponse(ToolCallResponse(("id-1", "looping_tool")));
        llm.EnqueueAgentResponse(ToolCallResponse(("id-2", "looping_tool")));

        var ctx = MakeContext(tools: new ToolContext { Registry = registry });
        // Stop after 2 steps.
        var agent = new Agent(llm, "model",
            stopWhen: StopConditions.StepCountIs(2),
            stream: false);
        var events = await RunToCompletion(agent, ctx);

        var result = events.OfType<AgentResultEvent>().Single();
        Assert.Equal(AgentResultStatus.MaxStepsReached, result.Status);
    }

    [Fact]
    public async Task RunAsync_MaxStepsReached_IterationCountMatchesLimit()
    {
        var tool = new FakeTool("t");
        var registry = new ToolRegistry([tool]);
        var llm = new FakeLlmClient();

        llm.EnqueueAgentResponse(ToolCallResponse(("id-1", "t")));
        llm.EnqueueAgentResponse(ToolCallResponse(("id-2", "t")));

        var ctx = MakeContext(tools: new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model",
            stopWhen: StopConditions.StepCountIs(2),
            stream: false);
        await RunToCompletion(agent, ctx);

        Assert.Equal(2, ctx.IterationCount);
    }

    // ── IterationCompleted events ──────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_EmitsIterationCompletedEvent_PerTurn()
    {
        var tool = new FakeTool("t");
        var registry = new ToolRegistry([tool]);
        var llm = new FakeLlmClient();

        // Two LLM calls: first requests a tool, second finishes.
        llm.EnqueueAgentResponse(ToolCallResponse(("id-1", "t")));
        llm.EnqueueAgentResponse(TextResponse("Done."));

        var ctx = MakeContext(tools: new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model", stream: false);
        var events = await RunToCompletion(agent, ctx);

        var iterationEvents = events.OfType<IterationCompletedEvent>().ToList();
        Assert.Equal(2, iterationEvents.Count);
        Assert.Equal(1, iterationEvents[0].Iteration);
        Assert.Equal(2, iterationEvents[1].Iteration);
    }

    // ── System prompt rebuilt every iteration (D3) ─────────────────────────────

    [Fact]
    public async Task RunAsync_SystemPromptIsSentOnEveryLlmCall()
    {
        var tool = new FakeTool("t");
        var registry = new ToolRegistry([tool]);
        var llm = new FakeLlmClient();

        llm.EnqueueAgentResponse(ToolCallResponse(("id-1", "t")));
        llm.EnqueueAgentResponse(TextResponse("Done."));

        var ctx = MakeContext(tools: new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model", stream: false);
        await RunToCompletion(agent, ctx);

        // Both LLM calls must have received a non-empty system prompt.
        Assert.Equal(2, llm.ReceivedSystemPrompts.Count);
        Assert.All(llm.ReceivedSystemPrompts, p => Assert.NotEmpty(p));
    }

    // ── Cancellation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_Cancellation_ThrowsOperationCanceledException()
    {
        var llm = new FakeLlmClient();
        // No responses queued — cancellation should happen before the first LLM call.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var agent = new Agent(llm, "model", stream: false);
        var ctx = MakeContext();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in agent.RunAsync(ctx, cts.Token))
            {
            }
        });
    }

    // ── Token usage accumulation ───────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_AccumulatesTokenUsage_AcrossMultipleTurns()
    {
        var tool = new FakeTool("t");
        var registry = new ToolRegistry([tool]);
        var llm = new FakeLlmClient();

        llm.EnqueueAgentResponse(new AgentLlmResponse(
            new AgentMessage(MessageRole.Assistant,
                [new ToolUseBlock("id-1", "t", JsonDocument.Parse("{}").RootElement)]),
            StopReason.ToolUse,
            new LlmTokenUsage(100, 50)));

        llm.EnqueueAgentResponse(new AgentLlmResponse(
            new AgentMessage(MessageRole.Assistant, [new TextBlock("Done.")]),
            StopReason.EndTurn,
            new LlmTokenUsage(200, 80)));

        var ctx = MakeContext(tools: new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model", stream: false);
        var events = await RunToCompletion(agent, ctx);

        var result = events.OfType<AgentResultEvent>().Single();
        Assert.Equal(300, result.TotalUsage.InputTokens);   // 100 + 200
        Assert.Equal(130, result.TotalUsage.OutputTokens);  // 50 + 80
    }
}
