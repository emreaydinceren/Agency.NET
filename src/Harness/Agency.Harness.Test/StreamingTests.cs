using System.Text.Json;

using Agency.Harness.Contexts;
using Agency.Harness.Test.Fakes;

namespace Agency.Harness.Test;

/// <summary>
/// Tests for D2 (streaming): the agent loop calls <c>IChatClient.GetStreamingResponseAsync</c>
/// instead of <c>GetResponseAsync</c> and emits per-chunk delta events as the response streams in.
/// All tests use <see cref="FakeChatClient"/> — no real LLM is needed.
/// </summary>
public sealed class StreamingTests
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

    // ── 2.1(a): a delta precedes the terminal AgentResultEvent ─────────────────

    /// <summary>
    /// 2.1(a): a streamed text response emits at least one <see cref="AssistantTextDeltaEvent"/>
    /// before the terminal <see cref="AgentResultEvent"/>.
    /// </summary>
    [Fact]
    public async Task RunAsync_StreamedTextResponse_DeltaPrecedesAgentResultEvent()
    {
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("Paris is the capital of France."));

        var agent = new Agent(llm, "model");
        var events = await RunToCompletion(agent, MakeContext(), ct: TestContext.Current.CancellationToken);

        int deltaIndex = events.FindIndex(static e => e is AssistantTextDeltaEvent);
        int resultIndex = events.FindIndex(static e => e is AgentResultEvent);

        Assert.True(deltaIndex >= 0, "Expected at least one AssistantTextDeltaEvent.");
        Assert.True(resultIndex >= 0, "Expected a terminal AgentResultEvent.");
        Assert.True(deltaIndex < resultIndex, "The text delta must precede the terminal AgentResultEvent.");
    }

    // ── 2.1(b): concatenated deltas equal the AssistantTurnEvent text ──────────

    /// <summary>
    /// 2.1(b): concatenating every <see cref="AssistantTextDeltaEvent.Text"/> emitted for a turn
    /// yields the same text as the corresponding <see cref="AssistantTurnEvent"/>'s message text.
    /// </summary>
    [Fact]
    public async Task RunAsync_StreamedMultiDeltaResponse_ConcatenatedDeltasEqualAssistantTurnText()
    {
        const string messageId = "msg-1";
        var llm = new FakeChatClient();
        llm.EnqueueUpdates(
            new ChatResponseUpdate(ChatRole.Assistant, "Paris ") { MessageId = messageId },
            new ChatResponseUpdate(ChatRole.Assistant, "is the ") { MessageId = messageId },
            new ChatResponseUpdate(ChatRole.Assistant, "capital of France.")
            {
                MessageId = messageId,
                FinishReason = ChatFinishReason.Stop,
            },
            new ChatResponseUpdate(ChatRole.Assistant, [])
            {
                MessageId = messageId,
                Contents = [new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 })],
            });

        var agent = new Agent(llm, "model");
        var events = await RunToCompletion(agent, MakeContext(), ct: TestContext.Current.CancellationToken);

        string concatenatedDeltas = string.Concat(
            events.OfType<AssistantTextDeltaEvent>().Select(static e => e.Text));
        string turnText = Assert.IsType<AssistantTurnEvent>(
            events.Single(static e => e is AssistantTurnEvent)).Message.Text;

        Assert.Equal("Paris is the capital of France.", concatenatedDeltas);
        Assert.Equal(concatenatedDeltas, turnText);
    }

    // ── 2.1(c): usage is accumulated after the stream drains ───────────────────

    /// <summary>
    /// 2.1(c): after a streamed turn completes, <see cref="Context.TotalUsage"/> reflects the
    /// usage-only update that arrives last in the decomposed stream (Finding 3).
    /// </summary>
    [Fact]
    public async Task RunAsync_StreamedResponse_AccumulatesTotalUsage()
    {
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("Done.", inputTokens: 7, outputTokens: 3));

        var agent = new Agent(llm, "model");
        var ctx = MakeContext();
        await RunToCompletion(agent, ctx, ct: TestContext.Current.CancellationToken);

        Assert.True(ctx.TotalUsage.TotalTokens > 0);
        Assert.Equal(10, ctx.TotalUsage.TotalTokens);
    }

    // ── 2.1(d): a streamed FunctionCallContent still drives the tool loop ──────

    /// <summary>
    /// 2.1(d): a <see cref="FunctionCallContent"/> that arrives via the streaming path still
    /// drives the tool loop — the same tool is invoked with the same arguments as it would be
    /// from a non-streamed response.
    /// </summary>
    [Fact]
    public async Task RunAsync_StreamedFunctionCall_StillDrivesToolLoop_WithSameToolAndArgs()
    {
        var tool = new FakeTool("search", _ => new ToolResult("42"));
        var registry = new ToolRegistry([tool]);
        var llm = new FakeChatClient();

        var arguments = new Dictionary<string, object?> { ["query"] = "capital of France" };
        var toolCallResponse = new ChatResponse(
        [
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "search", arguments)]),
        ])
        {
            Usage = new UsageDetails { InputTokenCount = 20, OutputTokenCount = 10 },
            FinishReason = ChatFinishReason.ToolCalls,
        };
        llm.EnqueueResponse(toolCallResponse);
        llm.EnqueueResponse(TextResponse("The capital of France is Paris."));

        var ctx = MakeContext(tools: new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model");
        await RunToCompletion(agent, ctx, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, tool.InvokeCount);
        JsonElement receivedInput = Assert.Single(tool.ReceivedInputs);
        Assert.Equal("capital of France", receivedInput.GetProperty("query").GetString());
    }

    // ── 2.2: reasoning never appears in AssistantTextDeltaEvent.Text ───────────

    /// <summary>
    /// 2.2: <see cref="TextReasoningContent"/> in the stream is surfaced only as an
    /// <see cref="AssistantThoughtDeltaEvent"/> — it never leaks into an
    /// <see cref="AssistantTextDeltaEvent"/>.
    /// </summary>
    [Fact]
    public async Task RunAsync_StreamedReasoningAndText_ReasoningNeverAppearsInTextDelta()
    {
        const string messageId = "msg-1";
        var llm = new FakeChatClient();
        llm.EnqueueUpdates(
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("Let me think about this...")])
            {
                MessageId = messageId,
            },
            new ChatResponseUpdate(ChatRole.Assistant, "The answer is Paris.")
            {
                MessageId = messageId,
                FinishReason = ChatFinishReason.Stop,
            },
            new ChatResponseUpdate(ChatRole.Assistant, [])
            {
                MessageId = messageId,
                Contents = [new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 })],
            });

        var agent = new Agent(llm, "model");
        var events = await RunToCompletion(agent, MakeContext(), ct: TestContext.Current.CancellationToken);

        var thoughtEvent = Assert.Single(events.OfType<AssistantThoughtDeltaEvent>());
        Assert.Equal("Let me think about this...", thoughtEvent.Text);

        foreach (AssistantTextDeltaEvent textDelta in events.OfType<AssistantTextDeltaEvent>())
        {
            Assert.DoesNotContain("Let me think about this...", textDelta.Text, StringComparison.Ordinal);
        }
    }
}
