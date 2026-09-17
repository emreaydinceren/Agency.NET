using Agency.Acp.Errors;
using Agency.Acp.Test.Fakes;
using Agency.Harness;
using Agency.Harness.Tools;
using Agency.Acp.Turns;
using Agency.Llm.Common.Tools;
using dotacp.protocol;
using Microsoft.Extensions.AI;
using AcpTextContent = dotacp.protocol.TextContent;

namespace Agency.Acp.Test.Turns;

/// <summary>
/// Behavioral tests for <see cref="TurnDriver"/> (spec §6.4, §8.2): a Turn may park more than once,
/// and whatever the harness does in between, exactly one terminal response reaches the client.
/// </summary>
public sealed class TurnDriverTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ContentBlock[] TextPrompt(string text) => [new AcpTextContent { Text = text }];

    private static ChatResponse TextResponse(string text) =>
        new([new ChatMessage(ChatRole.Assistant, text)])
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            FinishReason = ChatFinishReason.Stop,
        };

    private static ChatResponse ToolCallResponse(params (string Id, string Name)[] calls)
    {
        var contents = calls.Select(c => (AIContent)new FunctionCallContent(c.Id, c.Name)).ToList();
        return new ChatResponse([new ChatMessage(ChatRole.Assistant, contents)])
        {
            Usage = new UsageDetails { InputTokenCount = 20, OutputTokenCount = 10 },
            FinishReason = ChatFinishReason.ToolCalls,
        };
    }

    private static ChatResponse TextThenToolCallResponse(string text, string callId, string toolName)
    {
        List<AIContent> contents = [new Microsoft.Extensions.AI.TextContent(text), new FunctionCallContent(callId, toolName)];
        return new ChatResponse([new ChatMessage(ChatRole.Assistant, contents)])
        {
            Usage = new UsageDetails { InputTokenCount = 20, OutputTokenCount = 10 },
            FinishReason = ChatFinishReason.ToolCalls,
        };
    }

    // ── Task 8.1: a two-park turn yields exactly one terminal response ─────────

    /// <summary>
    /// A permission evaluator that Asks for the first two distinct tools and Allows thereafter
    /// parks the turn twice. Asserts: (a) exactly one terminal <see cref="PromptResponse"/> reaches
    /// the caller; (b) two <c>session/request_permission</c> calls were made, one per tool; (c) the
    /// text streamed before the first park still reached the client.
    /// </summary>
    [Fact]
    public async Task RunTurnAsync_TwoDistinctToolsAskThenAllow_ParksTwice_YieldsExactlyOneTerminalResponse()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        var toolA = new FakeTool("toolA", _ => new ToolResult("A ok"));
        var toolB = new FakeTool("toolB", _ => new ToolResult("B ok"));
        var registry = new ToolRegistry([toolA, toolB]);

        var evaluator = new StubPermissionEvaluator();
        evaluator.Decisions["toolA"] = new Agency.Harness.Permissions.PermissionDecision.Ask("keyA", "toolA(keyA)");
        evaluator.Decisions["toolB"] = new Agency.Harness.Permissions.PermissionDecision.Ask("keyB", "toolB(keyB)");

        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextThenToolCallResponse("partial answer", "id-a", "toolA"));
        llm.EnqueueResponse(ToolCallResponse(("id-b", "toolB")));
        llm.EnqueueResponse(TextResponse("Done."));

        var agent = new Agent(llm, "model", permissions: evaluator);
        var chatSession = new ChatSession(agent, new AgentOptions(), toolContext: new Agency.Harness.Contexts.ToolContext { Registry = registry });
        var session = await TestSessions.BuildAsync(chatSession);

        var proxy = new FakeAcpClientProxy();
        proxy.EnqueuePermissionResponse(new SelectedPermissionOutcome { OptionId = "allow_once" });
        proxy.EnqueuePermissionResponse(new SelectedPermissionOutcome { OptionId = "allow_once" });

        var driver = new TurnDriver(proxy);

        PromptResponse response = await driver.RunTurnAsync(session, TextPrompt("go"), ct);

        // (a) exactly one terminal response — proven load-bearing in the task report by temporarily
        // collapsing the park loop to a single round trip and observing this assertion fail.
        Assert.Equal(StopReason.EndTurn, response.StopReason);

        // (b) two session/request_permission calls, one per tool, in order.
        Assert.Equal(2, proxy.PermissionRequests.Count);
        Assert.Equal("toolA", proxy.PermissionRequests[0].ToolCall.Title);
        Assert.Equal("toolB", proxy.PermissionRequests[1].ToolCall.Title);

        // (c) events emitted before the first park still reached the client.
        Assert.Contains(proxy.Updates, u =>
            u is SessionUpdateAgentMessageChunk chunk &&
            chunk.Content is AcpTextContent text &&
            text.Text == "partial answer");

        // AwaitingPermission itself never reaches the client as an update or a stop reason.
        Assert.DoesNotContain(proxy.Updates, u => u.SessionUpdateValue.Contains("permission", StringComparison.OrdinalIgnoreCase));

        // The guard is released and can be re-entered after completion.
        Assert.True(session.TryEnterTurn());
    }

    // ── Task 8.1: overlapping prompts are a client defect, not a queue ─────────

    /// <summary>
    /// A second <c>session/prompt</c> while one is in flight for the same session returns a
    /// JSON-RPC error rather than queueing (spec §6.4 Constraints, P6).
    /// </summary>
    [Fact]
    public async Task RunTurnAsync_SecondPromptWhileFirstInFlight_ThrowsInsteadOfQueueing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        var hangingClient = new HangingChatClient();
        var agent = new Agent(hangingClient, "model");
        var chatSession = new ChatSession(agent, new AgentOptions());
        var session = await TestSessions.BuildAsync(chatSession);

        var proxy = new FakeAcpClientProxy();
        var driver = new TurnDriver(proxy);

        Task<PromptResponse> firstTurn = driver.RunTurnAsync(session, TextPrompt("first"), ct);
        await hangingClient.Entered;

        AcpJsonRpcException ex = await Assert.ThrowsAsync<AcpJsonRpcException>(
            () => driver.RunTurnAsync(session, TextPrompt("second"), ct));
        Assert.Equal(ErrorCode.InvalidRequest, ex.Code);

        // Clean up the still-in-flight first turn so the test does not hang the process.
        session.TurnCts!.Cancel();
        PromptResponse firstResponse = await firstTurn;
        Assert.Equal(StopReason.Cancelled, firstResponse.StopReason);
    }
}
