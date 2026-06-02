using Agency.Harness.Contexts;
using Agency.Harness.Hooks;
using Agency.Harness.Test.Fakes;

namespace Agency.Harness.Test;

/// <summary>
/// Unit tests for <see cref="ChatSession"/>.
/// </summary>
public sealed class ChatSessionTests
{
    /// <summary>Builds a <see cref="ChatResponse"/> with a single text message.</summary>
    private static ChatResponse TextResponse(string text) =>
        new([new ChatMessage(ChatRole.Assistant, text)])
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            FinishReason = ChatFinishReason.Stop,
        };

    /// <summary>
    /// Drains all events from <see cref="ChatSession.SendAsync"/> into a list so
    /// callers can assert on the side-effects (e.g. which client was invoked).
    /// </summary>
    private static async Task DrainAsync(ChatSession session, string message)
    {
        await foreach (var _ in session.SendAsync(message, TestContext.Current.CancellationToken))
        {
        }
    }

    // ── SetAgent ──────────────────────────────────────────────────────────────

    /// <summary>
    /// After calling <see cref="ChatSession.SetAgent"/>, subsequent turns must be
    /// routed to the new agent, not the one the session was created with.
    /// </summary>
    [Fact]
    public async Task SetAgent_SubsequentTurns_UseNewAgent()
    {
        var clientA = new FakeChatClient();
        clientA.EnqueueResponse(TextResponse("response from A"));
        var agentA = new Agent(clientA, "model-a");

        var clientB = new FakeChatClient();
        clientB.EnqueueResponse(TextResponse("response from B"));
        var agentB = new Agent(clientB, "model-b");

        var session = new ChatSession(agentA, new AgentOptions());

        await DrainAsync(session, "first turn");

        Assert.Equal(1, clientA.GetResponseCallCount);
        Assert.Equal(0, clientB.GetResponseCallCount);

        session.SetAgent(agentB);

        await DrainAsync(session, "second turn");

        Assert.Equal(1, clientA.GetResponseCallCount); // unchanged — old agent not called again
        Assert.Equal(1, clientB.GetResponseCallCount); // new agent was used
    }

    /// <summary>
    /// Conversation history accumulated before <see cref="ChatSession.SetAgent"/> is
    /// preserved: the new agent receives all prior messages on its first turn.
    /// </summary>
    [Fact]
    public async Task SetAgent_ConversationHistory_IsPreserved()
    {
        var clientA = new FakeChatClient();
        clientA.EnqueueResponse(TextResponse("hello from A"));
        var agentA = new Agent(clientA, "model-a");

        var clientB = new FakeChatClient();
        clientB.EnqueueResponse(TextResponse("hello from B"));
        var agentB = new Agent(clientB, "model-b");

        var session = new ChatSession(agentA, new AgentOptions());
        await DrainAsync(session, "first turn");

        session.SetAgent(agentB);
        await DrainAsync(session, "second turn");

        // clientB must have received both the first and second user messages.
        var messagesSeenByB = clientB.ReceivedMessages[0];
        Assert.True(
            messagesSeenByB.Any(m => m.Role == ChatRole.User && m.Text?.Contains("first turn") == true),
            "Expected clientB to receive the conversation history from before the model switch.");
    }

    /// <summary>
    /// <see cref="ChatSession.SetAgent"/> with a null agent throws
    /// <see cref="ArgumentNullException"/>.
    /// </summary>
    [Fact]
    public void SetAgent_NullAgent_Throws()
    {
        var client = new FakeChatClient();
        var agent = new Agent(client, "model-a");
        var session = new ChatSession(agent, new AgentOptions());

        Assert.Throws<ArgumentNullException>(() => session.SetAgent(null!));
    }

    // ── User identity ─────────────────────────────────────────────────────────

    /// <summary>
    /// A <see cref="UserSpecificContext"/> passed to the <see cref="ChatSession"/> ctor must
    /// flow into <see cref="Context.User"/> by the time the first turn fires the
    /// <c>OnSessionStarted</c> hook.
    /// </summary>
    [Fact]
    public async Task Constructor_WithUser_UserIdReachesContext()
    {
        var client = new FakeChatClient();
        client.EnqueueResponse(TextResponse("hello"));

        Context? capturedCtx = null;
        var hooks = new AgentHooks
        {
            OnSessionStarted = (hc, _) =>
            {
                capturedCtx = hc.AgentContext;
                return Task.CompletedTask;
            },
        };

        var agent = new Agent(client, "model", hooks: hooks);
        var session = new ChatSession(agent, new AgentOptions(), user: new UserSpecificContext { Id = "user-42" });

        await DrainAsync(session, "hello");

        Assert.NotNull(capturedCtx);
        Assert.Equal("user-42", capturedCtx.User.Id);
    }
}
