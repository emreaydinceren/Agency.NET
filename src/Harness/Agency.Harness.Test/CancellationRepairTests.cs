using System.Text.Json;
using Agency.Harness.Contexts;
using Agency.Harness.Test.Fakes;

namespace Agency.Harness.Test;

/// <summary>
/// Tests that a turn cancelled between the assistant message being appended and the tool
/// results being appended leaves a valid transcript: every <see cref="FunctionCallContent"/>
/// gets a matching <see cref="FunctionResultContent"/>, synthesized if necessary.
/// </summary>
public sealed class CancellationRepairTests
{
    /// <summary>
    /// A tool whose <see cref="InvokeAsync"/> blocks until the supplied token is cancelled.
    /// Signals <see cref="Entered"/> once invocation has actually started, so a test can wait for
    /// that signal before cancelling instead of racing against thread-pool scheduling.
    /// </summary>
    private sealed class BlockingTool : ITool
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ToolDefinition Definition { get; } = new(
            "blocking_tool", "Blocks until cancelled.", JsonDocument.Parse("{}").RootElement);

        /// <summary>Completes once <see cref="InvokeAsync"/> has started running.</summary>
        public Task Entered => _entered.Task;

        public async Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct)
        {
            _entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new ToolResult("unreachable");
        }
    }

    private static ChatResponse ToolCallResponse() =>
        new([new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "blocking_tool")])])
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            FinishReason = ChatFinishReason.ToolCalls,
        };

    private static ChatResponse TextResponse(string text) =>
        new([new ChatMessage(ChatRole.Assistant, text)])
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            FinishReason = ChatFinishReason.Stop,
        };

    /// <summary>
    /// Cancelling mid-tool must not leave a dangling <see cref="FunctionCallContent"/> without a
    /// matching <see cref="FunctionResultContent"/> — the next call would send an invalid transcript.
    /// </summary>
    [Fact]
    public async Task Cancel_MidTool_RepairsEveryFunctionCallWithAMatchingResult()
    {
        var client = new FakeChatClient();
        client.EnqueueResponse(ToolCallResponse());
        var agent = new Agent(client, "model-a");

        var blockingTool = new BlockingTool();
        var tools = new ToolContext { Registry = new ToolRegistry([blockingTool]) };
        var session = new ChatSession(agent, new AgentOptions(), tools);

        using var cts = new CancellationTokenSource();

        var runTask = Task.Run(async () =>
        {
            await foreach (var _ in session.SendAsync("go", cts.Token))
            {
            }
        });

        // Wait until the tool call has actually started, then cancel mid-flight — deterministic,
        // unlike racing a fixed delay against thread-pool scheduling.
        await blockingTool.Entered;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

        Context ctx = session.PreviewContext();
        var callIds = ctx.Conversation.Messages
            .Where(m => m.Role == ChatRole.Assistant)
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Select(c => c.CallId)
            .ToList();

        Assert.NotEmpty(callIds);

        var resultIds = ctx.Conversation.Messages
            .Where(m => m.Role == ChatRole.Tool)
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Select(r => r.CallId)
            .ToHashSet();

        foreach (string callId in callIds)
        {
            Assert.Contains(callId, resultIds);
        }
    }

    /// <summary>
    /// After a cancelled turn repairs the transcript, the next <see cref="ChatSession.SendAsync"/>
    /// call must complete normally — the repaired transcript is valid for the provider.
    /// </summary>
    [Fact]
    public async Task Cancel_MidTool_NextSendCompletesNormally()
    {
        var client = new FakeChatClient();
        client.EnqueueResponse(ToolCallResponse());
        var agent = new Agent(client, "model-a");

        var blockingTool = new BlockingTool();
        var tools = new ToolContext { Registry = new ToolRegistry([blockingTool]) };
        var session = new ChatSession(agent, new AgentOptions(), tools);

        using var cts = new CancellationTokenSource();

        var runTask = Task.Run(async () =>
        {
            await foreach (var _ in session.SendAsync("go", cts.Token))
            {
            }
        });

        await blockingTool.Entered;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

        client.EnqueueResponse(TextResponse("all good"));

        AgentResultEvent? result = null;
        await foreach (var evt in session.SendAsync("continue", TestContext.Current.CancellationToken))
        {
            if (evt is AgentResultEvent resultEvent)
            {
                result = resultEvent;
            }
        }

        Assert.NotNull(result);
        Assert.Equal(AgentResultStatus.Success, result!.Status);
    }
}
