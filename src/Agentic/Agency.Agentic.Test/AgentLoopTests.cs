using System.Text.Json;
using Agency.Agentic.Contexts;
using Agency.Agentic.Test.Fakes;

namespace Agency.Agentic.Test;

/// <summary>
/// Functional tests for the <see cref="Agent"/> loop.
/// All tests use a <see cref="FakeChatClient"/> — no real LLM is needed.
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

    /// <summary>Builds a <see cref="ChatResponse"/> that contains only a text block.</summary>
    private static ChatResponse TextResponse(string text, int inputTokens = 10, int outputTokens = 5) =>
        new([new ChatMessage(ChatRole.Assistant, text)])
        {
            Usage = new UsageDetails { InputTokenCount = inputTokens, OutputTokenCount = outputTokens },
            FinishReason = ChatFinishReason.Stop,
        };

    /// <summary>Builds a <see cref="ChatResponse"/> that requests one or more tool calls.</summary>
    private static ChatResponse ToolCallResponse(params (string id, string name)[] calls)
    {
        var contents = calls
            .Select(c => (AIContent)new FunctionCallContent(c.id, c.name))
            .ToList();

        return new ChatResponse([new ChatMessage(ChatRole.Assistant, contents)])
        {
            Usage = new UsageDetails { InputTokenCount = 20, OutputTokenCount = 10 },
            FinishReason = ChatFinishReason.ToolCalls,
        };
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
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("Paris."));

        var agent = new Agent(llm, "claude-3-5-sonnet");
        var events = await RunToCompletion(agent, MakeContext(), ct: TestContext.Current.CancellationToken);

        Assert.IsType<SessionStartedEvent>(events[0]);
    }

    [Fact]
    public async Task RunAsync_AlwaysEmitsAgentResultEventLast()
    {
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("Done."));

        var agent = new Agent(llm, "claude-3-5-sonnet");
        var events = await RunToCompletion(agent, MakeContext(), ct: TestContext.Current.CancellationToken);

        Assert.IsType<AgentResultEvent>(events[^1]);
    }

    [Fact]
    public async Task RunAsync_SessionStartedEvent_HasNonEmptySessionId()
    {
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("ok"));

        var agent = new Agent(llm, "model");
        var events = await RunToCompletion(agent, MakeContext(), ct: TestContext.Current.CancellationToken);

        var sessionEvent = Assert.IsType<SessionStartedEvent>(events[0]);
        Assert.NotEmpty(sessionEvent.SessionId);
    }

    // ── Single-turn happy path ─────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_SingleTurn_EmitsCorrectEventSequence()
    {
        // Arrange
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("Paris is the capital of France."));

        var agent = new Agent(llm, "model");

        // Act
        var events = await RunToCompletion(agent, MakeContext("What is the capital of France?"), ct: TestContext.Current.CancellationToken);

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
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("Done."));

        var agent = new Agent(llm, "model");
        var events = await RunToCompletion(agent, MakeContext(), ct: TestContext.Current.CancellationToken);

        var result = events.OfType<AgentResultEvent>().Single();
        Assert.Equal(AgentResultStatus.Success, result.Status);
    }

    [Fact]
    public async Task RunAsync_SingleTurn_FinalTextMatchesAssistantResponse()
    {
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("The answer is 42."));

        var agent = new Agent(llm, "model");
        var events = await RunToCompletion(agent, MakeContext(), ct: TestContext.Current.CancellationToken);

        var result = events.OfType<AgentResultEvent>().Single();
        Assert.Equal("The answer is 42.", result.FinalText);
    }

    [Fact]
    public async Task RunAsync_SingleTurn_AccumulatesTokenUsage()
    {
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("ok", inputTokens: 100, outputTokens: 50));

        var agent = new Agent(llm, "model");
        var events = await RunToCompletion(agent, MakeContext(), ct: TestContext.Current.CancellationToken);

        var result = events.OfType<AgentResultEvent>().Single();
        Assert.Equal(100, result.TotalUsage.InputTokens);
        Assert.Equal(50, result.TotalUsage.OutputTokens);
    }

    // ── Conversation seeding ───────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_SeedsConversation_WithUserPromptAsFirstMessage()
    {
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("ok"));

        var ctx = MakeContext("My specific prompt");
        var agent = new Agent(llm, "model");
        await RunToCompletion(agent, ctx, ct: TestContext.Current.CancellationToken);

        // The first message sent to the LLM must be the user's prompt.
        var firstSentMessages = llm.ReceivedMessages[0];
        Assert.Equal(ChatRole.User, firstSentMessages[0].Role);
        var textContent = Assert.IsType<TextContent>(firstSentMessages[0].Contents[0]);
        Assert.Equal("My specific prompt", textContent.Text);
    }

    [Fact]
    public async Task RunAsync_DoesNotDuplicatePrompt_WhenConversationAlreadyHasMessages()
    {
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("ok"));

        var ctx = MakeContext("Prompt");
        var existingMsg = new ChatMessage(ChatRole.User, "Existing message");
        ctx.Conversation.Append(existingMsg);

        var agent = new Agent(llm, "model");
        await RunToCompletion(agent, ctx, ct: TestContext.Current.CancellationToken);

        // Only the pre-existing message — the prompt should NOT be re-seeded.
        Assert.Single(llm.ReceivedMessages[0]);
        var textContent = Assert.IsType<TextContent>(llm.ReceivedMessages[0][0].Contents[0]);
        Assert.Equal("Existing message", textContent.Text);
    }

    // ── Tool call → execution → continue ──────────────────────────────────────

    [Fact]
    public async Task RunAsync_ToolCall_InvokesTool_AndContinuesLoop()
    {
        // Arrange
        var tool = new FakeTool("search");
        var registry = new ToolRegistry([tool]);
        var llm = new FakeChatClient();

        // Turn 1: LLM requests a tool call.
        llm.EnqueueResponse(ToolCallResponse(("use-1", "search")));
        // Turn 2: LLM sees the result and finishes.
        llm.EnqueueResponse(TextResponse("Search is done."));

        var ctx = MakeContext(tools: new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model");

        // Act
        await RunToCompletion(agent, ctx, ct: TestContext.Current.CancellationToken);

        // Assert: tool was invoked once, LLM was called twice
        Assert.Equal(1, tool.InvokeCount);
        Assert.Equal(2, llm.GetResponseCallCount);
    }

    [Fact]
    public async Task RunAsync_ToolCall_EmitsToolInvokedEvent()
    {
        var tool = new FakeTool("calculator", _ => new ToolResult("42"));
        var registry = new ToolRegistry([tool]);
        var llm = new FakeChatClient();

        llm.EnqueueResponse(ToolCallResponse(("use-1", "calculator")));
        llm.EnqueueResponse(TextResponse("The result is 42."));

        var ctx = MakeContext(tools: new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model");
        var events = await RunToCompletion(agent, ctx, ct: TestContext.Current.CancellationToken);

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
        var llm = new FakeChatClient();

        llm.EnqueueResponse(ToolCallResponse(("use-abc", "search")));
        llm.EnqueueResponse(TextResponse("Done."));

        var ctx = MakeContext(tools: new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model");
        await RunToCompletion(agent, ctx, ct: TestContext.Current.CancellationToken);

        // The second LLM call must include a Tool-role message with the FunctionResultContent.
        var turn2Messages = llm.ReceivedMessages[1];
        var toolResultMessage = turn2Messages.Last(m => m.Role == ChatRole.Tool);
        var resultContent = Assert.IsType<FunctionResultContent>(toolResultMessage.Contents[0]);

        Assert.Equal("use-abc", resultContent.CallId);
        Assert.Equal("result content", resultContent.Result?.ToString());
    }

    // ── Parallel tool execution ────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_MultipleToolCalls_InvokesAllToolsInParallel()
    {
        var toolA = new FakeTool("tool_a", _ => new ToolResult("A result"));
        var toolB = new FakeTool("tool_b", _ => new ToolResult("B result"));
        var registry = new ToolRegistry([toolA, toolB]);
        var llm = new FakeChatClient();

        // Single assistant turn that requests two tool calls simultaneously.
        llm.EnqueueResponse(ToolCallResponse(("id-1", "tool_a"), ("id-2", "tool_b")));
        llm.EnqueueResponse(TextResponse("Both results received."));

        var ctx = MakeContext(tools: new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model");
        var events = await RunToCompletion(agent, ctx, ct: TestContext.Current.CancellationToken);

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
        var llm = new FakeChatClient();

        llm.EnqueueResponse(ToolCallResponse(("id-1", "tool_a"), ("id-2", "tool_b")));
        llm.EnqueueResponse(TextResponse("Done."));

        var ctx = MakeContext(tools: new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model");
        await RunToCompletion(agent, ctx, ct: TestContext.Current.CancellationToken);

        // Regardless of execution order, result messages must match original call IDs in sequence.
        var toolResultMsgs = llm.ReceivedMessages[1]
            .Where(m => m.Role == ChatRole.Tool)
            .ToList();

        Assert.Equal(2, toolResultMsgs.Count);
        var r0 = Assert.IsType<FunctionResultContent>(toolResultMsgs[0].Contents[0]);
        var r1 = Assert.IsType<FunctionResultContent>(toolResultMsgs[1].Contents[0]);
        Assert.Equal("id-1", r0.CallId);
        Assert.Equal("id-2", r1.CallId);
    }

    // ── Tool failure handling ──────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ToolThrows_CapturesErrorAsToolResultBlock()
    {
        var brokenTool = new FakeTool("broken", _ => throw new InvalidOperationException("Tool exploded"));
        var registry = new ToolRegistry([brokenTool]);
        var llm = new FakeChatClient();

        llm.EnqueueResponse(ToolCallResponse(("use-1", "broken")));
        llm.EnqueueResponse(TextResponse("I saw the error and I handled it."));

        var ctx = MakeContext(tools: new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model");
        var events = await RunToCompletion(agent, ctx, ct: TestContext.Current.CancellationToken);

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
        var llm = new FakeChatClient();

        llm.EnqueueResponse(ToolCallResponse(("id-1", "broken"), ("id-2", "good")));
        llm.EnqueueResponse(TextResponse("Handled both."));

        var ctx = MakeContext(tools: new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model");
        await RunToCompletion(agent, ctx, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, brokenTool.InvokeCount);
        Assert.Equal(1, goodTool.InvokeCount);
    }

    // ── Max steps ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_MaxStepsReached_EmitsMaxStepsReachedStatus()
    {
        var tool = new FakeTool("looping_tool");
        var registry = new ToolRegistry([tool]);
        var llm = new FakeChatClient();

        llm.EnqueueResponse(ToolCallResponse(("id-1", "looping_tool")));
        llm.EnqueueResponse(ToolCallResponse(("id-2", "looping_tool")));

        var ctx = MakeContext(tools: new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model",
            stopWhen: StopConditions.StepCountIs(2));
        var events = await RunToCompletion(agent, ctx, ct: TestContext.Current.CancellationToken);

        var result = events.OfType<AgentResultEvent>().Single();
        Assert.Equal(AgentResultStatus.MaxStepsReached, result.Status);
    }

    [Fact]
    public async Task RunAsync_MaxStepsReached_IterationCountMatchesLimit()
    {
        var tool = new FakeTool("t");
        var registry = new ToolRegistry([tool]);
        var llm = new FakeChatClient();

        llm.EnqueueResponse(ToolCallResponse(("id-1", "t")));
        llm.EnqueueResponse(ToolCallResponse(("id-2", "t")));

        var ctx = MakeContext(tools: new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model",
            stopWhen: StopConditions.StepCountIs(2));
        await RunToCompletion(agent, ctx, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, ctx.IterationCount);
    }

    // ── IterationCompleted events ──────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_EmitsIterationCompletedEvent_PerTurn()
    {
        var tool = new FakeTool("t");
        var registry = new ToolRegistry([tool]);
        var llm = new FakeChatClient();

        llm.EnqueueResponse(ToolCallResponse(("id-1", "t")));
        llm.EnqueueResponse(TextResponse("Done."));

        var ctx = MakeContext(tools: new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model");
        var events = await RunToCompletion(agent, ctx, ct: TestContext.Current.CancellationToken);

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
        var llm = new FakeChatClient();

        llm.EnqueueResponse(ToolCallResponse(("id-1", "t")));
        llm.EnqueueResponse(TextResponse("Done."));

        var ctx = MakeContext(tools: new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model");
        await RunToCompletion(agent, ctx, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, llm.ReceivedSystemPrompts.Count);
        Assert.All(llm.ReceivedSystemPrompts, p => Assert.NotEmpty(p));
    }

    // ── Cancellation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_Cancellation_ThrowsOperationCanceledException()
    {
        var llm = new FakeChatClient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var agent = new Agent(llm, "model");
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
        var llm = new FakeChatClient();

        llm.EnqueueResponse(new ChatResponse([
            new ChatMessage(ChatRole.Assistant, [
                new FunctionCallContent("id-1", "t"),
            ])])
        {
            Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 50 },
            FinishReason = ChatFinishReason.ToolCalls,
        });

        llm.EnqueueResponse(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Done.")])
        {
            Usage = new UsageDetails { InputTokenCount = 200, OutputTokenCount = 80 },
            FinishReason = ChatFinishReason.Stop,
        });

        var ctx = MakeContext(tools: new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model");
        var events = await RunToCompletion(agent, ctx, ct: TestContext.Current.CancellationToken);

        var result = events.OfType<AgentResultEvent>().Single();
        Assert.Equal(300, result.TotalUsage.InputTokens);   // 100 + 200
        Assert.Equal(130, result.TotalUsage.OutputTokens);  // 50 + 80
    }

    // ── Truncation (finish_reason=length) ─────────────────────────────────────

    [Fact]
    public async Task RunAsync_WhenResponseTruncated_EmitsErrorResult()
    {
        var llm = new FakeChatClient();
        llm.EnqueueResponse(new ChatResponse([new ChatMessage(ChatRole.Assistant, "...cut")])
        {
            Usage = new UsageDetails { InputTokenCount = 3350, OutputTokenCount = 746 },
            FinishReason = ChatFinishReason.Length,
        });

        var agent = new Agent(llm, "model");
        var events = await RunToCompletion(agent, MakeContext(), ct: TestContext.Current.CancellationToken);

        var result = Assert.IsType<AgentResultEvent>(events[^1]);
        Assert.Equal(AgentResultStatus.Error, result.Status);
        Assert.NotNull(result.FinalText);
        Assert.Contains("truncated", result.FinalText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3,350", result.FinalText);  // input token count surfaced in message
    }

    [Fact]
    public async Task RunAsync_WhenResponseTruncated_DoesNotInvokeTools()
    {
        var invoked = false;
        var registry = new ToolRegistry();
        registry.Register(new FakeTool("some_tool", _ =>
        {
            invoked = true;
            return new ToolResult("done", false);
        }));

        var llm = new FakeChatClient();
        llm.EnqueueResponse(new ChatResponse([new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call-1", "some_tool")])])
        {
            Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 50 },
            FinishReason = ChatFinishReason.Length,
        });

        var agent = new Agent(llm, "model");
        var ctx = MakeContext(tools: new ToolContext { Registry = registry });
        await RunToCompletion(agent, ctx, ct: TestContext.Current.CancellationToken);

        Assert.False(invoked);
    }
}
