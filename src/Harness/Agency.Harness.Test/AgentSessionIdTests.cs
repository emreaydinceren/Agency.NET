using Agency.Harness.Contexts;
using Agency.Harness.Test.Fakes;

namespace Agency.Harness.Test;

/// <summary>
/// Tests that verify stable session-id behaviour across <see cref="ChatSession"/> turns
/// and that a pre-set <see cref="SessionContext.Id"/> is honoured by the agent loop.
/// </summary>
public sealed class AgentSessionIdTests
{
    private static ChatResponse TextResponse(string text) =>
        new([new ChatMessage(ChatRole.Assistant, text)])
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            FinishReason = ChatFinishReason.Stop,
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

    // ── Stable session id across turns ────────────────────────────────────────

    /// <summary>
    /// A <see cref="ChatSession"/> preserves the same session id across successive
    /// <c>SendAsync</c> turns rather than generating a new one each time.
    /// </summary>
    [Fact]
    public async Task ChatAsync_SessionId_IsStableAcrossTwoTurns()
    {
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("turn 1"));
        llm.EnqueueResponse(TextResponse("turn 2"));

        var agent = new Agent(llm, "model");
        var session = new ChatSession(agent, new AgentOptions());

        string? idFromTurn1 = null;
        string? idFromTurn2 = null;

        await foreach (AgentEvent evt in session.SendAsync("first", TestContext.Current.CancellationToken))
        {
            if (evt is SessionStartedEvent started)
            {
                idFromTurn1 = started.SessionId;
            }
        }

        await foreach (AgentEvent evt in session.SendAsync("second", TestContext.Current.CancellationToken))
        {
            if (evt is SessionStartedEvent started)
            {
                idFromTurn2 = started.SessionId;
            }
        }

        Assert.NotNull(idFromTurn1);
        Assert.NotEmpty(idFromTurn1);
        Assert.Equal(idFromTurn1, idFromTurn2);
    }

    // ── Pre-set session id is honoured ────────────────────────────────────────

    /// <summary>
    /// When the caller sets <see cref="SessionContext.Id"/> on the context before running, the
    /// agent loop honours it instead of generating a fresh session id.
    /// </summary>
    [Fact]
    public async Task RunAsync_PresetSessionId_IsNotOverwritten()
    {
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("done"));

        var agent = new Agent(llm, "model");
        var ctx = Agent.CreateContext("Hello");
        ctx.Session = ctx.Session with { Id = "preset-123" };

        var events = await RunToCompletion(agent, ctx);

        var sessionEvent = Assert.IsType<SessionStartedEvent>(events[0]);
        Assert.Equal("preset-123", sessionEvent.SessionId);
    }
}
