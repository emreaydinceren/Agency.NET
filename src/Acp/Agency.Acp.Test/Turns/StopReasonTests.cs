using System.Runtime.CompilerServices;
using Agency.Acp.Errors;
using Agency.Acp.Test.Fakes;
using Agency.Acp.Turns;
using Agency.Harness;
using Agency.Harness.Tools;
using Agency.Llm.Common.Tools;
using dotacp.protocol;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using AcpTextContent = dotacp.protocol.TextContent;

namespace Agency.Acp.Test.Turns;

/// <summary>
/// Behavioral tests for <see cref="StopReasonMapper"/> and, where a bare status mapping cannot
/// exercise the behavior, for <see cref="TurnDriver"/> driving the corresponding scenario
/// end-to-end (spec §8.3).
/// </summary>
public sealed class StopReasonTests
{
    private static ContentBlock[] TextPrompt(string text) => [new AcpTextContent { Text = text }];

    private static AgentResultEvent Result(AgentResultStatus status, string? finalText = null) =>
        new(status, finalText, new LlmTokenUsage(0, 0), 0m);

    // ── Pure status → StopReason mapping (spec §8.3 table) ──────────────────────

    /// <summary>Each non-error, non-park <see cref="AgentResultStatus"/> maps to its ACP <see cref="StopReason"/> per spec §8.3.</summary>
    [Theory]
    [InlineData(AgentResultStatus.Success, StopReason.EndTurn)]
    [InlineData(AgentResultStatus.MaxStepsReached, StopReason.MaxTurnRequests)]
    [InlineData(AgentResultStatus.Truncated, StopReason.MaxTokens)]
    public void MapTerminal_MapsEachStatusPerSpecTable(AgentResultStatus status, StopReason expected)
    {
        Assert.Equal(expected, StopReasonMapper.MapTerminal(Result(status, "text")));
    }

    /// <summary><see cref="AgentResultStatus.Error"/> is a JSON-RPC error, never a silent <c>end_turn</c> (spec P6).</summary>
    [Fact]
    public void MapTerminal_Error_ThrowsAcpJsonRpcException_CarryingTheMessage()
    {
        AcpJsonRpcException ex = Assert.Throws<AcpJsonRpcException>(
            () => StopReasonMapper.MapTerminal(Result(AgentResultStatus.Error, "boom")));

        Assert.Equal("boom", ex.Message);
    }

    /// <summary><see cref="AgentResultStatus.AwaitingPermission"/> must never reach this mapper.</summary>
    [Fact]
    public void MapTerminal_AwaitingPermission_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => StopReasonMapper.MapTerminal(Result(AgentResultStatus.AwaitingPermission)));
    }

    // ── TurnDriver-level: cancellation and timeout (spec §8.3, §6.9 D-5) ────────

    /// <summary>An <see cref="IChatClient"/> whose first call streams text then a tool call, and whose second call hangs.</summary>
    private sealed class TextThenHangChatClient : IChatClient
    {
        private int _calls;
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => this._entered.Task;
        public ChatClientMetadata Metadata { get; } = new("TextThenHang", null, null);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref this._calls);
            if (call == 1)
            {
                var response = new ChatResponse(
                    [new ChatMessage(ChatRole.Assistant, [new Microsoft.Extensions.AI.TextContent("partial answer"), new FunctionCallContent("id-1", "toolA")])])
                {
                    Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
                    FinishReason = ChatFinishReason.ToolCalls,
                };
                foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
                {
                    yield return update;
                }

                yield break;
            }

            this._entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    /// <summary>
    /// A user cancel mid-turn (spec §6.4, §8.3): no terminal <see cref="AgentResultEvent"/> is
    /// emitted, so <see cref="TurnDriver"/> synthesises <see cref="StopReason.Cancelled"/>, and
    /// whatever text had already streamed to the client via <c>session/update</c> before the cancel
    /// remains — nothing retracts it.
    /// </summary>
    [Fact]
    public async Task RunTurnAsync_CancelledMidTurn_ReturnsCancelled_WithPartialTextAlreadySent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        var toolA = new FakeTool("toolA", _ => new ToolResult("ok"));
        var registry = new ToolRegistry([toolA]);

        var client = new TextThenHangChatClient();
        var agent = new Agent(client, "model");
        var chatSession = new ChatSession(agent, new AgentOptions(), toolContext: new Agency.Harness.Contexts.ToolContext { Registry = registry });
        var session = await TestSessions.BuildAsync(chatSession);

        var proxy = new FakeAcpClientProxy();
        var driver = new TurnDriver(proxy);

        Task<PromptResponse> turnTask = driver.RunTurnAsync(session, TextPrompt("go"), ct);
        await client.Entered;
        session.TurnCts!.Cancel();

        PromptResponse response = await turnTask;

        Assert.Equal(StopReason.Cancelled, response.StopReason);
        Assert.Contains(proxy.Updates, u =>
            u is SessionUpdateAgentMessageChunk chunk &&
            chunk.Content is AcpTextContent text &&
            text.Text == "partial answer");
    }

    /// <summary>
    /// A turn timeout (spec §6.9 D-5) is distinguishable from a user cancel: it fails loudly as a
    /// JSON-RPC error rather than returning the plain <see cref="StopReason.Cancelled"/> response a
    /// user-initiated <c>session/cancel</c> produces.
    /// </summary>
    [Fact]
    public async Task RunTurnAsync_TurnTimeout_ThrowsJsonRpcError_DistinctFromCancel()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        var clock = new FakeTimeProvider();
        var client = new HangingChatClient();
        var agent = new Agent(client, "model", timeProvider: clock);
        var chatSession = new ChatSession(agent, new AgentOptions { TurnTimeoutSeconds = 30 });
        var session = await TestSessions.BuildAsync(chatSession);

        var proxy = new FakeAcpClientProxy();
        var driver = new TurnDriver(proxy);

        Task<PromptResponse> turnTask = driver.RunTurnAsync(session, TextPrompt("go"), ct);
        await client.Entered;
        clock.Advance(TimeSpan.FromSeconds(31));

        AcpJsonRpcException ex = await Assert.ThrowsAsync<AcpJsonRpcException>(() => turnTask);
        Assert.Equal(ErrorCode.InternalError, ex.Code);
    }

    // ── AwaitingPermission never reaches the client (spec §6.4, §8.3) ───────────

    /// <summary>
    /// While a turn is parked, <see cref="AgentResultStatus.AwaitingPermission"/>'s ACP analogue never
    /// appears as a <c>session/update</c>, and the terminal response is only sent once the turn
    /// truly ends — never for the intermediate park.
    /// </summary>
    [Fact]
    public async Task RunTurnAsync_Parks_AwaitingPermissionNeverReachesClient()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        var toolA = new FakeTool("toolA", _ => new ToolResult("ok"));
        var registry = new ToolRegistry([toolA]);

        var evaluator = new StubPermissionEvaluator();
        evaluator.Decisions["toolA"] = new Agency.Harness.Permissions.PermissionDecision.Ask("keyA", "toolA(keyA)");

        var llm = new FakeChatClient();
        llm.EnqueueResponse(new ChatResponse([new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("id-a", "toolA")])])
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            FinishReason = ChatFinishReason.ToolCalls,
        });
        llm.EnqueueResponse(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Done.")])
        {
            Usage = new UsageDetails { InputTokenCount = 5, OutputTokenCount = 5 },
            FinishReason = ChatFinishReason.Stop,
        });

        var agent = new Agent(llm, "model", permissions: evaluator);
        var chatSession = new ChatSession(agent, new AgentOptions(), toolContext: new Agency.Harness.Contexts.ToolContext { Registry = registry });
        var session = await TestSessions.BuildAsync(chatSession);

        var proxy = new FakeAcpClientProxy();
        proxy.EnqueuePermissionResponse(new SelectedPermissionOutcome { OptionId = "allow_once" });
        var driver = new TurnDriver(proxy);

        PromptResponse response = await driver.RunTurnAsync(session, TextPrompt("go"), ct);

        Assert.Equal(StopReason.EndTurn, response.StopReason);
        Assert.Single(proxy.PermissionRequests);
        Assert.DoesNotContain(proxy.Updates, u => u.SessionUpdateValue.Contains("permission", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(proxy.Updates, u => u.SessionUpdateValue.Contains("awaiting", StringComparison.OrdinalIgnoreCase));
    }
}
