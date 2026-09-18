using Agency.Harness.Contexts;
using Agency.Harness.Test.Fakes;

namespace Agency.Harness.Test;

/// <summary>
/// Tests for the <see cref="ChatSession"/> constructor overload that accepts an
/// <c>identityPrompt</c> (spec §6.4): identity supplied at construction must reach the submitted
/// system prompt on a live turn, and <see cref="ChatSession.PreviewContext"/> must agree with it
/// so preview hosts never silently diverge from live turns.
/// </summary>
public sealed class ChatSessionIdentityTests
{
    private const string DefaultIdentityLine = "You are an autonomous agent operating inside the Agency runtime.";
    private const string ReActText = "When solving a task, always explain your reasoning";

    /// <summary>Builds a <see cref="ChatResponse"/> with a single text message.</summary>
    private static ChatResponse TextResponse(string text) =>
        new([new ChatMessage(ChatRole.Assistant, text)])
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            FinishReason = ChatFinishReason.Stop,
        };

    /// <summary>Drives one turn through <paramref name="session"/> and discards the events.</summary>
    private static async Task DrainAsync(ChatSession session, string message)
    {
        await foreach (var _ in session.SendAsync(message, TestContext.Current.CancellationToken))
        {
        }
    }

    /// <summary>Constructs a <see cref="ChatSession"/> via the new 8-arg overload, with all trailing arguments defaulted except <paramref name="identityPrompt"/>.</summary>
    private static ChatSession MakeSession(FakeChatClient client, string? identityPrompt) =>
        new(new Agent(client, "model"), new AgentOptions(), null, null, null, null, null, identityPrompt);

    /// <summary>(a) The first line of the submitted system prompt is the supplied identity text.</summary>
    [Fact]
    public async Task SendAsync_WithIdentityPrompt_FirstLineOfSubmittedPromptIsIdentity()
    {
        var client = new FakeChatClient();
        client.EnqueueResponse(TextResponse("hello"));
        var session = MakeSession(client, "You are Ana, who routes.");

        await DrainAsync(session, "hi");

        string submittedPrompt = Assert.Single(client.ReceivedSystemPrompts);
        string firstLine = submittedPrompt.Split('\n', 2)[0].TrimEnd('\r');
        Assert.Equal("You are Ana, who routes.", firstLine);
    }

    /// <summary>(b) The default identity line must not appear when a custom identity is supplied.</summary>
    [Fact]
    public async Task SendAsync_WithIdentityPrompt_DefaultIdentityLineDoesNotAppear()
    {
        var client = new FakeChatClient();
        client.EnqueueResponse(TextResponse("hello"));
        var session = MakeSession(client, "You are Ana, who routes.");

        await DrainAsync(session, "hi");

        string submittedPrompt = Assert.Single(client.ReceivedSystemPrompts);
        Assert.DoesNotContain(DefaultIdentityLine, submittedPrompt, StringComparison.Ordinal);
    }

    /// <summary>(c) The ReAct instruction survives a custom identity — identity replaces a line, not the whole prompt (spec O-2).</summary>
    [Fact]
    public async Task SendAsync_WithIdentityPrompt_ReActTextSurvives()
    {
        var client = new FakeChatClient();
        client.EnqueueResponse(TextResponse("hello"));
        var session = MakeSession(client, "You are Ana, who routes.");

        await DrainAsync(session, "hi");

        string submittedPrompt = Assert.Single(client.ReceivedSystemPrompts);
        Assert.Contains(ReActText, submittedPrompt, StringComparison.Ordinal);
    }

    /// <summary>(d) With a <see langword="null"/> identity, the default identity line appears verbatim.</summary>
    [Fact]
    public async Task SendAsync_WithNullIdentityPrompt_DefaultIdentityLineAppears()
    {
        var client = new FakeChatClient();
        client.EnqueueResponse(TextResponse("hello"));
        var session = MakeSession(client, identityPrompt: null);

        await DrainAsync(session, "hi");

        string submittedPrompt = Assert.Single(client.ReceivedSystemPrompts);
        Assert.Contains(DefaultIdentityLine, submittedPrompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// (e) <see cref="ChatSession.PreviewContext"/> reports the same identity on
    /// <see cref="QueryContext.IdentityPrompt"/> as a live turn would — spec §6.4 warns that
    /// missing this second call site makes preview hosts silently disagree with live turns.
    /// </summary>
    [Fact]
    public void PreviewContext_WithIdentityPrompt_ReportsSameIdentity()
    {
        var client = new FakeChatClient();
        var session = MakeSession(client, "You are Ana, who routes.");

        Context preview = session.PreviewContext();

        Assert.Equal("You are Ana, who routes.", preview.Query.IdentityPrompt);
    }

    /// <summary>
    /// Spec O-1: identity must reach the model on every iteration, not just the first. Scripts a
    /// two-iteration turn — the first response requests a tool call, forcing the agent loop
    /// around again for a second LLM call, the second is plain text and terminates the turn —
    /// and asserts both submitted system prompts carry the identity. This has no paired
    /// implementation: it asserts the property that falls out of <see cref="SystemPromptBuilder"/>
    /// rebuilding the prompt every iteration (spec §9); a property believed to hold for
    /// structural reasons and never asserted is exactly how the original defect shipped.
    /// </summary>
    [Fact]
    public async Task SendAsync_TwoIterationTurn_IdentityPresentOnBothIterations()
    {
        var tool = new FakeTool("alpha", _ => new ToolResult("alpha result"));
        var registry = new ToolRegistry([tool]);
        var client = new FakeChatClient();

        var toolCallResponse = new ChatResponse(
        [
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "alpha")]),
        ])
        {
            Usage = new UsageDetails { InputTokenCount = 20, OutputTokenCount = 10 },
            FinishReason = ChatFinishReason.ToolCalls,
        };
        client.EnqueueResponse(toolCallResponse);
        client.EnqueueResponse(TextResponse("done"));

        var session = new ChatSession(
            new Agent(client, "model"),
            new AgentOptions(),
            new ToolContext { Registry = registry },
            null, null, null, null,
            "You are Ana, who routes.");

        await DrainAsync(session, "hi");

        Assert.Equal(2, client.ReceivedSystemPrompts.Count);
        Assert.All(
            client.ReceivedSystemPrompts,
            prompt => Assert.Contains("You are Ana, who routes.", prompt, StringComparison.Ordinal));
    }
}
