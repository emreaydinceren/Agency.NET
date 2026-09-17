using Agency.Harness.Contexts;
using Agency.Harness.Test.Fakes;

namespace Agency.Harness.Test;

/// <summary>
/// Tests that a truncated LLM response (<c>FinishReason.Length</c>) is reported as its own
/// terminal status rather than being conflated with a genuine, unrecoverable <c>Error</c>.
/// </summary>
public sealed class AgentResultStatusTests
{
    private static Context MakeContext(string prompt = "Hello") =>
        new()
        {
            Query = new QueryContext { Prompt = prompt },
            Tools = ToolContext.Empty,
        };

    private static async Task<List<AgentEvent>> RunToCompletion(Agent agent, Context ctx)
    {
        var events = new List<AgentEvent>();
        await foreach (AgentEvent evt in agent.RunAsync(ctx, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }
        return events;
    }

    /// <summary>Builds a <see cref="ChatResponse"/> with only reasoning content — no tool call, no text.</summary>
    private static ChatResponse DegenerateResponse() =>
        new([new ChatMessage(ChatRole.Assistant, [new TextReasoningContent("thinking about it...")])])
        {
            Usage = new UsageDetails { InputTokenCount = 50, OutputTokenCount = 20 },
            FinishReason = ChatFinishReason.Stop,
        };

    /// <summary>
    /// A response that hits its token limit mid-generation is a recoverable, known condition — not
    /// the same as a genuinely broken/degenerate response — so it must be reported as its own
    /// <see cref="AgentResultStatus.Truncated"/> status, not <see cref="AgentResultStatus.Error"/>.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenResponseTruncated_EmitsTruncatedStatus()
    {
        var llm = new FakeChatClient();
        llm.EnqueueResponse(new ChatResponse([new ChatMessage(ChatRole.Assistant, "...cut")])
        {
            Usage = new UsageDetails { InputTokenCount = 3350, OutputTokenCount = 746 },
            FinishReason = ChatFinishReason.Length,
        });

        var agent = new Agent(llm, "model");
        var events = await RunToCompletion(agent, MakeContext());

        var result = Assert.IsType<AgentResultEvent>(events[^1]);
        Assert.Equal(AgentResultStatus.Truncated, result.Status);
    }

    /// <summary>
    /// A genuinely degenerate response (neither tool call nor text, retries exhausted) is still a
    /// real error — truncation getting its own status must not weaken this case.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenResponseIsDegenerate_StillEmitsErrorStatus()
    {
        var llm = new FakeChatClient();
        llm.EnqueueResponse(DegenerateResponse());
        llm.EnqueueResponse(DegenerateResponse());
        llm.EnqueueResponse(DegenerateResponse());

        var agent = new Agent(llm, "model");
        var events = await RunToCompletion(agent, MakeContext());

        var result = Assert.IsType<AgentResultEvent>(events[^1]);
        Assert.Equal(AgentResultStatus.Error, result.Status);
    }
}
