using Agency.Harness.Contexts;
using Agency.Harness.Test.Fakes;

namespace Agency.Harness.Test;

/// <summary>
/// Tests for D2 task 2.3: <see cref="ToolStartedEvent"/> is yielded per call immediately before
/// dispatch, and its <see cref="ToolStartedEvent.CallId"/> correlates 1:1 with the later
/// <see cref="ToolInvokedEvent.CallId"/> — both equal to the provider's own
/// <c>FunctionCallContent.CallId</c>, not a freshly generated id.
/// </summary>
public sealed class ToolEventCorrelationTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Builds a minimal context with the given prompt and tools.</summary>
    private static Context MakeContext(ToolContext tools) =>
        new()
        {
            Query = new QueryContext { Prompt = "Hello" },
            Tools = tools,
        };

    /// <summary>Builds a <see cref="ChatResponse"/> that contains only a text block.</summary>
    private static ChatResponse TextResponse(string text) =>
        new([new ChatMessage(ChatRole.Assistant, text)])
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            FinishReason = ChatFinishReason.Stop,
        };

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

    /// <summary>
    /// Two tool calls requested in a single assistant message each get a
    /// <see cref="ToolStartedEvent"/> that precedes either call's <see cref="ToolInvokedEvent"/>,
    /// and the ids correlate 1:1 with the provider's own <c>FunctionCallContent.CallId</c>
    /// — not freshly generated GUIDs.
    /// </summary>
    [Fact]
    public async Task RunAsync_TwoToolCallsInOneMessage_ToolStartedPrecedesToolInvoked_IdsCorrelateToProviderCallIds()
    {
        var toolA = new FakeTool("alpha", _ => new ToolResult("alpha result"));
        var toolB = new FakeTool("beta", _ => new ToolResult("beta result"));
        var registry = new ToolRegistry([toolA, toolB]);
        var llm = new FakeChatClient();

        const string callIdA = "provider-call-alpha";
        const string callIdB = "provider-call-beta";
        var toolCallResponse = new ChatResponse(
        [
            new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent(callIdA, "alpha"),
                new FunctionCallContent(callIdB, "beta"),
            ]),
        ])
        {
            Usage = new UsageDetails { InputTokenCount = 20, OutputTokenCount = 10 },
            FinishReason = ChatFinishReason.ToolCalls,
        };
        llm.EnqueueResponse(toolCallResponse);
        llm.EnqueueResponse(TextResponse("Both tools ran."));

        var ctx = MakeContext(new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model");
        var events = await RunToCompletion(agent, ctx, ct: TestContext.Current.CancellationToken);

        List<ToolStartedEvent> started = events.OfType<ToolStartedEvent>().ToList();
        List<ToolInvokedEvent> invoked = events.OfType<ToolInvokedEvent>().ToList();

        Assert.Equal(2, started.Count);
        Assert.Equal(2, invoked.Count);

        // Both ToolStartedEvents precede either ToolInvokedEvent (announced before dispatch begins).
        int lastStartedIndex = events.FindLastIndex(static e => e is ToolStartedEvent);
        int firstInvokedIndex = events.FindIndex(static e => e is ToolInvokedEvent);
        Assert.True(
            lastStartedIndex < firstInvokedIndex,
            "Both ToolStartedEvents must precede the first ToolInvokedEvent.");

        // Ids correlate 1:1 and equal the provider's own CallId — not new GUIDs.
        var startedIds = started.Select(static e => e.CallId).ToHashSet();
        var invokedIds = invoked.Select(static e => e.CallId).ToHashSet();
        Assert.Equal(new HashSet<string> { callIdA, callIdB }, startedIds);
        Assert.Equal(startedIds, invokedIds);

        // Names correlate with the ids they were announced/invoked under.
        Assert.Equal("alpha", started.Single(e => e.CallId == callIdA).ToolName);
        Assert.Equal("beta", started.Single(e => e.CallId == callIdB).ToolName);
        Assert.Equal("alpha", invoked.Single(e => e.CallId == callIdA).ToolName);
        Assert.Equal("beta", invoked.Single(e => e.CallId == callIdB).ToolName);
    }
}
