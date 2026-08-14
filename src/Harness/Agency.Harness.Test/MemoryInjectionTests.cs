using Agency.Harness.Contexts;
using Agency.Harness.Hooks;
using Agency.Harness.Test.Fakes;

namespace Agency.Harness.Test;

/// <summary>
/// Tests that the <c>&lt;memory&gt;</c> block reaches the chat client as its own user message,
/// adjacent to the current question, and never enters the persisted conversation.
/// </summary>
public sealed class MemoryInjectionTests
{
    private static ChatResponse TextResponse(string text) =>
        new([new ChatMessage(ChatRole.Assistant, text)])
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            FinishReason = ChatFinishReason.Stop,
        };

    /// <summary>A hook that populates recall the way the retrieval engine does.</summary>
    private static AgentHooks RecallHook(string title = "Shaved ice", string value = "Likes fruity shaved ice.") =>
        new()
        {
            OnPreIteration = (c, _) =>
            {
                c.Knowledge = c.Knowledge with
                {
                    MemoryPolicy = "Treat these as already known.",
                    Records = [new MemoryRecord(title, value, DateTimeOffset.UtcNow.AddMinutes(-5))],
                };
                return Task.CompletedTask;
            },
        };

    private static async Task RunToCompletion(Agent agent, Context ctx, CancellationToken ct)
    {
        await foreach (var _ in agent.RunAsync(ctx, ct))
        {
        }
    }

    /// <summary>
    /// The block arrives as a user message positioned immediately before the question, so the
    /// recalled records are the last thing the model reads before the request it must answer.
    /// </summary>
    [Fact]
    public async Task MemoryBlock_IsInjectedAsUserMessage_DirectlyBeforeTheQuestion()
    {
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("Shaved ice."));

        var ctx = new Context { Query = new QueryContext { Prompt = "What should I eat?" } };
        var agent = new Agent(llm, "test-model", hooks: RecallHook());
        await RunToCompletion(agent, ctx, TestContext.Current.CancellationToken);

        IReadOnlyList<ChatMessage> sent = llm.ReceivedMessages[^1];

        Assert.Equal(2, sent.Count);
        Assert.Equal(ChatRole.User, sent[0].Role);
        Assert.Contains("<memory>", sent[0].Text, StringComparison.Ordinal);
        Assert.Contains("Likes fruity shaved ice.", sent[0].Text, StringComparison.Ordinal);
        Assert.Equal("What should I eat?", sent[1].Text);
    }

    /// <summary>
    /// The injected message is transient. Recall is a vector search over the current message, so a
    /// copy left in the transcript would be stale on the next turn and would accumulate.
    /// </summary>
    [Fact]
    public async Task MemoryBlock_DoesNotEnterThePersistedConversation()
    {
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("Shaved ice."));

        var ctx = new Context { Query = new QueryContext { Prompt = "What should I eat?" } };
        var agent = new Agent(llm, "test-model", hooks: RecallHook());
        await RunToCompletion(agent, ctx, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            ctx.Conversation.Messages,
            m => m.Text?.Contains("<memory>", StringComparison.Ordinal) ?? false);
    }

    /// <summary>
    /// The captured request must show the block, otherwise <c>/dump-context</c> would report a
    /// request that was never sent — the failure the capture-at-the-call-site design exists to
    /// prevent.
    /// </summary>
    [Fact]
    public async Task MemoryBlock_AppearsInTheCapturedRequest()
    {
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("Shaved ice."));

        var ctx = new Context { Query = new QueryContext { Prompt = "What should I eat?" } };
        var agent = new Agent(llm, "test-model", hooks: RecallHook());
        await RunToCompletion(agent, ctx, TestContext.Current.CancellationToken);

        byte[] bytes = Assert.IsType<byte[]>(ctx.LastLlmRequest);
        LlmRequestSnapshot snapshot = Assert.IsType<LlmRequestSnapshot>(
            LlmRequestSnapshotCodec.Deserialize(bytes));

        Assert.Contains(
            snapshot.Messages,
            m => m.Text?.Contains("<memory>", StringComparison.Ordinal) ?? false);
    }

    /// <summary>
    /// With nothing recalled and no policy, no message is injected at all — the request is exactly
    /// the conversation.
    /// </summary>
    [Fact]
    public async Task NoRecall_InjectsNoMessage()
    {
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("Hello."));

        var ctx = new Context { Query = new QueryContext { Prompt = "Hi" } };
        var agent = new Agent(llm, "test-model");
        await RunToCompletion(agent, ctx, TestContext.Current.CancellationToken);

        IReadOnlyList<ChatMessage> sent = llm.ReceivedMessages[^1];

        Assert.Single(sent);
        Assert.Equal("Hi", sent[0].Text);
    }

    /// <summary>
    /// With a project-instructions block present, the memory block sits between it and the
    /// question: instructions first, then recall, then what the user asked.
    /// </summary>
    [Fact]
    public async Task MemoryBlock_SitsBetweenInstructionsAndTheQuestion()
    {
        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("Shaved ice."));

        Context ctx = Agent.CreateContext(
            "What should I eat?",
            instructionsBlock: "<project-instructions>be terse</project-instructions>");
        var agent = new Agent(llm, "test-model", hooks: RecallHook());
        await RunToCompletion(agent, ctx, TestContext.Current.CancellationToken);

        IReadOnlyList<ChatMessage> sent = llm.ReceivedMessages[^1];

        Assert.Equal(3, sent.Count);
        Assert.Contains("<project-instructions>", sent[0].Text, StringComparison.Ordinal);
        Assert.Contains("<memory>", sent[1].Text, StringComparison.Ordinal);
        Assert.Equal("What should I eat?", sent[2].Text);
    }
}
