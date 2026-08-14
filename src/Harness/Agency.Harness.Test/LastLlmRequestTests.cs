using Agency.Harness.Contexts;
using Agency.Harness.Hooks;
using Agency.Harness.Test.Fakes;
using System.Text.Json;

namespace Agency.Harness.Test;

/// <summary>
/// Tests for the per-iteration capture of the submitted request on
/// <see cref="Context.LastLlmRequest"/>. All tests use a <see cref="FakeChatClient"/>.
/// </summary>
public sealed class LastLlmRequestTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Context MakeContext(string prompt = "Hello", ToolContext? tools = null) =>
        new()
        {
            Query = new QueryContext { Prompt = prompt },
            Tools = tools ?? ToolContext.Empty,
        };

    private static ChatResponse TextResponse(string text) =>
        new([new ChatMessage(ChatRole.Assistant, text)])
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            FinishReason = ChatFinishReason.Stop,
        };

    private static ChatResponse ToolCallResponse(string id, string name) =>
        new([new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(id, name)])])
        {
            Usage = new UsageDetails { InputTokenCount = 20, OutputTokenCount = 10 },
            FinishReason = ChatFinishReason.ToolCalls,
        };

    /// <summary>A response carrying neither a tool call nor text — the loop retries these.</summary>
    private static ChatResponse DegenerateResponse() =>
        new([new ChatMessage(ChatRole.Assistant, [])])
        {
            Usage = new UsageDetails { InputTokenCount = 5, OutputTokenCount = 0 },
            FinishReason = ChatFinishReason.Stop,
        };

    private static async Task RunToCompletion(Agent agent, Context ctx, CancellationToken ct)
    {
        await foreach (var _ in agent.RunAsync(ctx, ct))
        {
        }
    }

    /// <summary>Runs one turn and returns the deserialized capture, asserting one was taken.</summary>
    private static LlmRequestSnapshot Captured(Context ctx)
    {
        byte[] bytes = Assert.IsType<byte[]>(ctx.LastLlmRequest);
        return Assert.IsType<LlmRequestSnapshot>(LlmRequestSnapshotCodec.Deserialize(bytes));
    }

    // ── Capture fidelity ──────────────────────────────────────────────────────

    /// <summary>
    /// The capture round-trips and matches what the client actually received: the same system
    /// prompt and the same number of messages.
    /// </summary>
    [Fact]
    public async Task LastLlmRequest_MatchesWhatTheClientReceived()
    {
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("Paris."));

        var ctx = MakeContext("What is the capital of France?");
        var agent = new Agent(llm, "test-model", "TestClient");
        await RunToCompletion(agent, ctx, TestContext.Current.CancellationToken);

        LlmRequestSnapshot snapshot = Captured(ctx);

        Assert.Equal(llm.ReceivedSystemPrompts[^1], snapshot.SystemPrompt);
        Assert.Equal(llm.ReceivedMessages[^1].Count, snapshot.Messages.Count);
        Assert.Equal("test-model", snapshot.ModelId);
        Assert.Equal("TestClient", snapshot.ClientType);
        Assert.Equal(1, snapshot.Iteration);
    }

    /// <summary>
    /// The capture is serialized, so the assistant reply appended after the call (Agent.cs, step 5)
    /// does not leak into it. Guards against reverting to a reference to the live message list,
    /// which <c>InMemoryConversationManager</c> exposes directly.
    /// </summary>
    [Fact]
    public async Task LastLlmRequest_IsFrozenAgainstLaterAppends()
    {
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("Done."));

        var ctx = MakeContext();
        var agent = new Agent(llm, "test-model");
        await RunToCompletion(agent, ctx, TestContext.Current.CancellationToken);

        LlmRequestSnapshot snapshot = Captured(ctx);

        Assert.True(
            snapshot.Messages.Count < ctx.Conversation.Messages.Count,
            "The assistant reply appended after the call must not appear in the capture.");
    }

    /// <summary>
    /// Context written by an <c>OnPreIteration</c> hook reaches the capture. This is the whole
    /// point of capturing at the call site: retrieval runs inside the iteration, so a projection
    /// built before the turn cannot see what it injected.
    /// </summary>
    [Fact]
    public async Task LastLlmRequest_IncludesContextInjectedByPreIterationHook()
    {
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("Noted."));

        var hooks = new AgentHooks
        {
            OnPreIteration = (c, _) =>
            {
                c.Knowledge = c.Knowledge with { Facts = ["injected-fact"] };
                return Task.CompletedTask;
            },
        };

        var ctx = MakeContext();
        var agent = new Agent(llm, "test-model", hooks: hooks);
        await RunToCompletion(agent, ctx, TestContext.Current.CancellationToken);

        Assert.Contains("injected-fact", Captured(ctx).SystemPrompt, StringComparison.Ordinal);
    }

    // ── Serialization contract ────────────────────────────────────────────────

    /// <summary>
    /// Polymorphic <see cref="AIContent"/> survives the round trip as its concrete type, so a
    /// renderer can switch on content type exactly as it does for a live conversation.
    /// </summary>
    [Fact]
    public async Task LastLlmRequest_RoundTripsToolCallAndToolResultContent()
    {
        var tool = new FakeTool("search", _ => new ToolResult("result content"));
        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse("use-abc", "search"));
        llm.EnqueueResponse(TextResponse("Done."));

        var ctx = MakeContext(tools: new ToolContext { Registry = new ToolRegistry([tool]) });
        var agent = new Agent(llm, "test-model");
        await RunToCompletion(agent, ctx, TestContext.Current.CancellationToken);

        LlmRequestSnapshot snapshot = Captured(ctx);

        var call = snapshot.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Single();
        Assert.Equal("use-abc", call.CallId);
        Assert.Equal("search", call.Name);

        var result = snapshot.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .Single();
        Assert.Equal("use-abc", result.CallId);
    }

    /// <summary>Tool definitions and their JSON schemas survive the round trip.</summary>
    [Fact]
    public async Task LastLlmRequest_RoundTripsToolDefinitions()
    {
        const string schema = """{"type":"object","properties":{"query":{"type":"string"}}}""";
        var tool = new FakeTool("search", description: "Searches things.", schema: schema);
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("Done."));

        var ctx = MakeContext(tools: new ToolContext { Registry = new ToolRegistry([tool]) });
        var agent = new Agent(llm, "test-model");
        await RunToCompletion(agent, ctx, TestContext.Current.CancellationToken);

        ToolDefinition captured = Assert.Single(Captured(ctx).Tools);

        Assert.Equal("search", captured.Name);
        Assert.Equal("Searches things.", captured.Description);
        Assert.Equal(
            JsonSerializer.Serialize(JsonDocument.Parse(schema).RootElement),
            JsonSerializer.Serialize(captured.InputSchema));
    }

    // ── Which request gets captured ───────────────────────────────────────────

    /// <summary>
    /// A multi-iteration turn leaves the <em>last</em> iteration's request in the slot — the one
    /// that includes the tool result.
    /// </summary>
    [Fact]
    public async Task LastLlmRequest_AfterMultipleIterations_HoldsTheFinalRequest()
    {
        var tool = new FakeTool("calculator", _ => new ToolResult("42"));
        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse("use-1", "calculator"));
        llm.EnqueueResponse(TextResponse("The result is 42."));

        var ctx = MakeContext(tools: new ToolContext { Registry = new ToolRegistry([tool]) });
        var agent = new Agent(llm, "test-model");
        await RunToCompletion(agent, ctx, TestContext.Current.CancellationToken);

        LlmRequestSnapshot snapshot = Captured(ctx);

        Assert.Equal(2, snapshot.Iteration);
        Assert.Contains(snapshot.Messages, m => m.Role == ChatRole.Tool);
    }

    /// <summary>
    /// The capture sits outside the degenerate-response retry loop, so retrying one logical
    /// request does not advance the iteration or change the captured message list.
    /// </summary>
    [Fact]
    public async Task LastLlmRequest_DegenerateRetry_CapturesOneLogicalRequest()
    {
        var llm = new FakeChatClient();
        llm.EnqueueResponse(DegenerateResponse());
        llm.EnqueueResponse(TextResponse("Recovered."));

        var ctx = MakeContext();
        var agent = new Agent(llm, "test-model");
        await RunToCompletion(agent, ctx, TestContext.Current.CancellationToken);

        LlmRequestSnapshot snapshot = Captured(ctx);

        Assert.Equal(2, llm.GetResponseCallCount);
        Assert.Equal(1, snapshot.Iteration);
        Assert.Equal(llm.ReceivedMessages[0].Count, snapshot.Messages.Count);
    }

    /// <summary>Before any turn has run, nothing has been captured.</summary>
    [Fact]
    public void LastLlmRequest_BeforeFirstTurn_IsNull()
    {
        Assert.Null(MakeContext().LastLlmRequest);
    }
}
