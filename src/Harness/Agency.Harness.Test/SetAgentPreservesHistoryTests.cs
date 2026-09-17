using Agency.Harness.Test.Fakes;

namespace Agency.Harness.Test;

/// <summary>
/// Regression-anchor for D4 (per-session effort configuration): switching to a
/// differently-configured agent mid-session must not drop conversation history. This
/// guards <see cref="ChatSession.SetAgent"/> against future changes made while wiring
/// per-agent thinking/effort options — <see cref="ChatSession"/> itself is agnostic to
/// those provider-level settings, so this test exercises the same history-preservation
/// path via two agents that stand in for differently-configured ones.
/// </summary>
public sealed class SetAgentPreservesHistoryTests
{
    /// <summary>Builds a <see cref="ChatResponse"/> with a single text message.</summary>
    private static ChatResponse TextResponse(string text) =>
        new([new ChatMessage(ChatRole.Assistant, text)])
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            FinishReason = ChatFinishReason.Stop,
        };

    /// <summary>Drains all events from <see cref="ChatSession.SendAsync"/> into a list.</summary>
    private static async Task DrainAsync(ChatSession session, string message)
    {
        await foreach (var _ in session.SendAsync(message, TestContext.Current.CancellationToken))
        {
        }
    }

    /// <summary>
    /// Sends a first turn, switches to a differently-configured agent via
    /// <see cref="ChatSession.SetAgent"/>, sends a second turn, and asserts the second
    /// request still carries the first turn's messages.
    /// </summary>
    [Fact]
    public async Task SetAgent_ToDifferentlyConfiguredAgent_SecondRequestCarriesFirstTurnHistory()
    {
        var lowEffortClient = new FakeChatClient();
        lowEffortClient.EnqueueResponse(TextResponse("reply from low-effort agent"));
        var lowEffortAgent = new Agent(lowEffortClient, "model-low-effort");

        var highEffortClient = new FakeChatClient();
        highEffortClient.EnqueueResponse(TextResponse("reply from high-effort agent"));
        var highEffortAgent = new Agent(highEffortClient, "model-high-effort");

        var session = new ChatSession(lowEffortAgent, new AgentOptions());
        await DrainAsync(session, "first turn message");

        session.SetAgent(highEffortAgent);
        await DrainAsync(session, "second turn message");

        var messagesSeenByHighEffortAgent = highEffortClient.ReceivedMessages[0];
        Assert.Contains(
            messagesSeenByHighEffortAgent,
            m => m.Role == ChatRole.User && m.Text?.Contains("first turn message") == true);
        Assert.Contains(
            messagesSeenByHighEffortAgent,
            m => m.Role == ChatRole.User && m.Text?.Contains("second turn message") == true);
    }
}
