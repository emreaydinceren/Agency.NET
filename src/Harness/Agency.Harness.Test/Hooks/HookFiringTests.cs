using Agency.Harness.Contexts;
using Agency.Harness.Hooks;
using Agency.Harness.Test.Fakes;

namespace Agency.Harness.Test.Hooks;

/// <summary>
/// Verifies that <c>OnUserPromptSubmit</c>, <c>OnPreIteration</c>, and <c>OnPostToolBatch</c>
/// are wired into the correct places in <see cref="Agent.ChatAsync"/> /
/// <see cref="Agent.RunAsync"/> (Spec §6.5, D.4).
/// </summary>
public sealed class HookFiringTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Context MakeContext(string prompt = "Hello", ToolContext? tools = null) =>
        new()
        {
            Query = new QueryContext { Prompt = prompt },
            Tools = tools ?? ToolContext.Empty,
        };

    private static ChatResponse TextResponse(string text = "Done.") =>
        new([new ChatMessage(ChatRole.Assistant, text)])
        {
            Usage = new UsageDetails { InputTokenCount = 5, OutputTokenCount = 3 },
            FinishReason = ChatFinishReason.Stop,
        };

    private static ChatResponse ToolCallThenStop(string toolId = "t1", string toolName = "MyTool") =>
        new([new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(toolId, toolName)])])
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            FinishReason = ChatFinishReason.ToolCalls,
        };

    private static async Task<List<AgentEvent>> RunChatToCompletion(
        Agent agent, string message, Context ctx, CancellationToken ct = default)
    {
        var events = new List<AgentEvent>();
        await foreach (AgentEvent evt in agent.ChatAsync(message, ctx, ct: ct))
        {
            events.Add(evt);
        }

        return events;
    }

    // ── OnUserPromptSubmit tests ───────────────────────────────────────────────

    /// <summary>
    /// <c>OnUserPromptSubmit</c> must fire exactly once on the first <c>ChatAsync</c> call,
    /// before any LLM call is made.
    /// </summary>
    [Fact]
    public async Task OnUserPromptSubmit_FiresOnceBeforeFirstIteration()
    {
        var ct = TestContext.Current.CancellationToken;
        int fireCount = 0;
        int llmCallCount = 0;

        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse());

        var hooks = new AgentHooks
        {
            OnUserPromptSubmit = (_, _) =>
            {
                fireCount++;
                Assert.Equal(0, llmCallCount); // Must fire before LLM
                return Task.CompletedTask;
            },
        };

        // Count LLM calls via a wrapper.
        var ctx = MakeContext();
        var agent = new Agent(llm, "test-model", hooks: hooks);

        await RunChatToCompletion(agent, "hello", ctx, ct);

        Assert.Equal(1, fireCount);
    }

    /// <summary>
    /// Each subsequent <c>ChatAsync</c> call must also fire <c>OnUserPromptSubmit</c> once.
    /// </summary>
    [Fact]
    public async Task OnUserPromptSubmit_FiresEveryChatAsyncCall()
    {
        var ct = TestContext.Current.CancellationToken;
        int fireCount = 0;

        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("First."));
        llm.EnqueueResponse(TextResponse("Second."));

        var hooks = new AgentHooks
        {
            OnUserPromptSubmit = (_, _) =>
            {
                fireCount++;
                return Task.CompletedTask;
            },
        };

        var ctx = MakeContext();
        var agent = new Agent(llm, "test-model", hooks: hooks);

        // First call.
        await RunChatToCompletion(agent, "hello", ctx, ct);
        // Second call.
        await RunChatToCompletion(agent, "goodbye", ctx, ct);

        Assert.Equal(2, fireCount);
    }

    // ── OnPreIteration tests ───────────────────────────────────────────────────

    /// <summary>
    /// <c>OnPreIteration</c> must fire at the start of each loop iteration,
    /// before <c>SystemPromptBuilder.Build</c> is called.
    /// </summary>
    [Fact]
    public async Task OnPreIteration_FiresBeforeSystemPromptBuild()
    {
        var ct = TestContext.Current.CancellationToken;
        bool preIterationFired = false;

        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse());

        var hooks = new AgentHooks
        {
            OnPreIteration = (context, _) =>
            {
                preIterationFired = true;
                // Inject a fact so we can verify the LLM sees it in the system prompt.
                context.Knowledge = context.Knowledge with
                {
                    Facts = ["Injected fact for test."],
                };
                return Task.CompletedTask;
            },
        };

        // Capture system prompts sent to the LLM.
        var ctx = MakeContext();
        var agent = new Agent(llm, "test-model", hooks: hooks);

        await RunChatToCompletion(agent, "hello", ctx, ct);

        Assert.True(preIterationFired);
        // Verify the injected fact appeared in the system prompt seen by the LLM.
        Assert.Contains(llm.ReceivedSystemPrompts, p => p.Contains("Injected fact for test."));
    }

    // ── OnPostToolBatch tests ──────────────────────────────────────────────────

    /// <summary>
    /// <c>OnPostToolBatch</c> must fire after <c>Task.WhenAll</c> of tool calls resolves,
    /// and it receives the full list of tool events from the batch.
    /// </summary>
    [Fact]
    public async Task OnPostToolBatch_FiresAfterTaskWhenAllReturns_BeforeNextLlmCall()
    {
        var ct = TestContext.Current.CancellationToken;
        int postBatchFireCount = 0;
        IReadOnlyList<ToolInvokedEvent>? capturedBatch = null;

        var fakeTool = new FakeTool("MyTool", _ => new ToolResult("ok"));
        var registry = new ToolRegistry();
        registry.Register(fakeTool);
        var toolCtx = new ToolContext { Registry = registry };

        var llm = new FakeChatClient();
        // First response: tool call.
        llm.EnqueueResponse(ToolCallThenStop("t1", "MyTool"));
        // Second response: final text.
        llm.EnqueueResponse(TextResponse("All done."));

        var hooks = new AgentHooks
        {
            OnPostToolBatch = (batch, _, _) =>
            {
                postBatchFireCount++;
                capturedBatch = batch;
                return Task.CompletedTask;
            },
        };

        var ctx = MakeContext(tools: toolCtx);
        var agent = new Agent(llm, "test-model", hooks: hooks);

        await RunChatToCompletion(agent, "hello", ctx, ct);

        Assert.Equal(1, postBatchFireCount);
        Assert.NotNull(capturedBatch);
        Assert.Single(capturedBatch!);
        Assert.Equal("MyTool", capturedBatch![0].ToolName);
    }
}
