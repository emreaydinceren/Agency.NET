using Agency.Harness.Contexts;
using Agency.Harness.Test.Fakes;

namespace Agency.Harness.Test;

/// <summary>
/// Regression tests for the empty-<c>choices</c> retry in <see cref="Agent"/>'s LLM call
/// (see logs/app-2026-07-18-090039.log): some OpenAI-compatible backends (e.g. LM Studio) occasionally
/// return a 200 response with an empty <c>choices</c> array, which surfaces as an
/// <see cref="ArgumentOutOfRangeException"/> (ParamName "index") deep inside the OpenAI SDK.
/// </summary>
public sealed class AgentEmptyChoicesRetryTests
{
    private static Context MakeContext(string prompt = "hi there") =>
        new()
        {
            Query = new QueryContext { Prompt = prompt },
            Tools = ToolContext.Empty,
        };

    private static ChatResponse TextResponse(string text) =>
        new([new ChatMessage(ChatRole.Assistant, text)])
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            FinishReason = ChatFinishReason.Stop,
        };

    // S3928: "index" isn't a parameter of this method — it deliberately replicates the ParamName
    // the OpenAI SDK throws with (see class remarks above), not a real validated argument here.
#pragma warning disable S3928
    private static ArgumentOutOfRangeException EmptyChoicesException() =>
        new("index", "Specified argument was out of the range of valid values.");
#pragma warning restore S3928

    private static async Task<List<AgentEvent>> RunToCompletion(
        Agent agent, Context ctx, CancellationToken ct)
    {
        var events = new List<AgentEvent>();
        await foreach (var evt in agent.RunAsync(ctx, ct))
        {
            events.Add(evt);
        }

        return events;
    }

    /// <summary>
    /// A single empty-choices failure is retried transparently and the turn still succeeds.
    /// </summary>
    [Fact]
    public async Task RunAsync_RetriesOnceOnEmptyChoicesThenSucceeds()
    {
        var llm = new FakeChatClient();
        llm.EnqueueException(EmptyChoicesException());
        llm.EnqueueResponse(TextResponse("hello!"));

        var agent = new Agent(llm, "google/gemma-4-e2b");
        var events = await RunToCompletion(agent, MakeContext(), TestContext.Current.CancellationToken);

        Assert.Equal(2, llm.GetResponseCallCount);
        var result = Assert.IsType<AgentResultEvent>(events[^1]);
        Assert.Equal(AgentResultStatus.Success, result.Status);
    }

    /// <summary>
    /// The bug reproduced in logs/app-2026-07-18-090039.log: two consecutive empty-choices
    /// failures used to propagate uncaught past the single retry and crash the whole console
    /// session. With a bounded retry loop, a second consecutive failure is retried too.
    /// </summary>
    [Fact]
    public async Task RunAsync_RetriesTwiceOnConsecutiveEmptyChoicesThenSucceeds()
    {
        var llm = new FakeChatClient();
        llm.EnqueueException(EmptyChoicesException());
        llm.EnqueueException(EmptyChoicesException());
        llm.EnqueueResponse(TextResponse("hello!"));

        var agent = new Agent(llm, "google/gemma-4-e2b");
        var events = await RunToCompletion(agent, MakeContext(), TestContext.Current.CancellationToken);

        Assert.Equal(3, llm.GetResponseCallCount);
        var result = Assert.IsType<AgentResultEvent>(events[^1]);
        Assert.Equal(AgentResultStatus.Success, result.Status);
    }

    /// <summary>
    /// Once the backend has failed on every attempt within the retry budget, a descriptive
    /// <see cref="InvalidOperationException"/> propagates instead of the raw SDK exception —
    /// so a console user sees an actionable explanation instead of "Specified argument was
    /// out of the range of valid values. (Parameter 'index')".
    /// </summary>
    [Fact]
    public async Task RunAsync_GivesUpAfterMaxAttemptsAndThrowsDescriptiveException()
    {
        var llm = new FakeChatClient();
        llm.EnqueueException(EmptyChoicesException());
        llm.EnqueueException(EmptyChoicesException());
        llm.EnqueueException(EmptyChoicesException());

        var agent = new Agent(llm, "google/gemma-4-e2b");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await RunToCompletion(agent, MakeContext(), TestContext.Current.CancellationToken));

        Assert.Equal(3, llm.GetResponseCallCount);
        Assert.Contains("google/gemma-4-e2b", ex.Message);
        Assert.Contains("3 consecutive malformed responses", ex.Message);
        Assert.IsType<ArgumentOutOfRangeException>(ex.InnerException);
    }
}
