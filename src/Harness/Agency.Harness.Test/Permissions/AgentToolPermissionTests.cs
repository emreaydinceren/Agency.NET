using System.Text.Json;
using Agency.Harness.Contexts;
using Agency.Harness.Permissions;
using Agency.Harness.Test.Fakes;
using Agency.Harness.Tools;

namespace Agency.Harness.Test.Permissions;

// ─────────────────────────────────────────────────────────────────────────────
// TDD RED tests for AgentTool's sub-agent auto-deny policy (spec §9.5, §12).
//
// Design: AgentTool receives a child Agent via the existing agentFactory delegate
// (Func<string?, string?, (AgentOptions, Agent, IToolRegistry)>).  Tests construct
// that factory so it returns an Agent whose IPermissionEvaluator is a
// StubPermissionEvaluator returning Ask for the target child tool — this makes the
// child park when that tool is requested.
//
// No new production seam is needed for these tests to compile today:
//   • AgentTool.ctor already accepts the factory.
//   • The child Agent already accepts the permissions parameter (Task 13).
//   • ChatSession.ResumeWithPermissionsAsync already exists (Task 15).
//
// RED shape: These tests will FAIL at runtime because AgentTool.InvokeAsync
// today handles AgentResultEvent(AwaitingPermission) with:
//   return new ToolResult(finalText, IsError: (status ?? Error) != Success)
// meaning it returns an IsError:true result instead of calling
// ResumeWithPermissionsAsync to auto-deny and continue.
// Task 17 must add the PermissionRequestedEvent + AwaitingPermission handling.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// TDD RED tests for <see cref="AgentTool"/> sub-agent auto-deny policy (spec §9.5).
/// These tests fail today because <see cref="AgentTool"/> does not yet call
/// <see cref="ChatSession.ResumeWithPermissionsAsync"/> when the child parks.
/// Task 17 implements the auto-deny loop.
/// </summary>
public sealed class AgentToolPermissionTests
{
    // ── Constants ─────────────────────────────────────────────────────────────

    /// <summary>The deny message prescribed by §9.5.</summary>
    private const string SubAgentDenyMessage =
        "Sub-agents cannot request permission; grant a rule to the parent session instead.";

    // ── Shared helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a StubPermissionEvaluator with Ask decisions for the given child tool names.
    /// All other tools default to Allow.
    /// </summary>
    private static StubPermissionEvaluator ChildEvaluatorThatAsksFor(params string[] toolNames)
    {
        var evaluator = new StubPermissionEvaluator();
        foreach (string name in toolNames)
        {
            evaluator.Decisions[name] = new PermissionDecision.Ask(null, name);
        }

        return evaluator;
    }

    /// <summary>
    /// Builds an AgentTool backed by a child scripted with the given LLM responses and
    /// a StubPermissionEvaluator whose Ask decisions are set for <paramref name="childUnresolvedTools"/>.
    /// Returns the AgentTool and the child tool registry so callers can assert InvokeCount.
    /// </summary>
    private static (AgentTool Tool, FakeChatClient ChildLlm)
        BuildAgentToolWithUnresolvedChildCalls(
            FakeTool[] childTools,
            string[] childUnresolvedTools,
            Action<FakeChatClient> scriptChildLlm)
    {
        var childLlm = new FakeChatClient();
        scriptChildLlm(childLlm);

        StubPermissionEvaluator childEvaluator = ChildEvaluatorThatAsksFor(childUnresolvedTools);
        var childRegistry = new ToolRegistry(childTools);

        AgentTool agentTool = new AgentTool(
            (_, _) =>
            {
                // Construct the child Agent with the stub evaluator so the child will park
                // when it attempts the unresolved tool.
                var childAgent = new Agent(childLlm, "child-model", permissions: childEvaluator);
                return (new AgentOptions(), childAgent, childRegistry);
            });

        return (agentTool, childLlm);
    }

    private static ChatResponse TextResponse(string text) =>
        new([new ChatMessage(ChatRole.Assistant, text)])
        {
            Usage = new UsageDetails { InputTokenCount = 5, OutputTokenCount = 3 },
            FinishReason = ChatFinishReason.Stop,
        };

    private static ChatResponse ToolCallResponse(params (string id, string name)[] calls)
    {
        var contents = calls
            .Select(c => (AIContent)new FunctionCallContent(c.id, c.name))
            .ToList();
        return new ChatResponse([new ChatMessage(ChatRole.Assistant, contents)])
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            FinishReason = ChatFinishReason.ToolCalls,
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 1 — Child ask auto-denied; parent settles
    //
    // Spec §9.5 / §12 "AgentTool sub-agent test":
    //   Child agent attempts a tool (childUnresolved) whose evaluator returns Ask.
    //   AgentTool MUST:
    //     (a) detect PermissionRequestedEvent + AwaitingPermission from the child stream,
    //     (b) call ResumeWithPermissionsAsync with DenyOnce + SubAgentDenyMessage for EVERY
    //         pending request,
    //     (c) allow the child to continue after the deny.
    //   Observables:
    //     • The denied child tool is NEVER invoked.
    //     • The child's conversation receives a [Blocked] result.
    //     • The child continues and emits a follow-up final answer.
    //     • AgentTool returns a ToolResult with IsError:false (parent turn settles).
    //     • No PermissionRequestedEvent or AwaitingPermission escapes into the PARENT stream.
    //
    // RED: today AgentTool returns IsError:true for AwaitingPermission and never resumes.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChildAsk_AutoDenied_ParentSettles_DeniedToolNeverInvoked()
    {
        var ct = TestContext.Current.CancellationToken;

        var childUnresolved = new FakeTool("childUnresolved", _ => new ToolResult("should not run"));

        // Script the child LLM:
        //   Turn 1: requests childUnresolved → will park.
        //   Turn 2 (after auto-deny): child sees [Blocked]; LLM returns final answer.
        (AgentTool agentTool, _) = BuildAgentToolWithUnresolvedChildCalls(
            childTools: [childUnresolved],
            childUnresolvedTools: ["childUnresolved"],
            scriptChildLlm: llm =>
            {
                llm.EnqueueResponse(ToolCallResponse(("cid-1", "childUnresolved")));
                llm.EnqueueResponse(TextResponse("Child says: task complete after deny."));
            });

        // Invoke AgentTool directly (simulates the parent agent running it).
        var input = JsonSerializer.SerializeToElement(new { prompt = "do the child task" });
        ToolResult result = await agentTool.InvokeAsync(input, ct);

        // ── (a) Denied child tool is NEVER invoked ────────────────────────────
        Assert.Equal(0, childUnresolved.InvokeCount);

        // ── (b) Parent receives a non-error ToolResult (parent batch completes) ─
        // Task 17 contract: AgentTool auto-denies and resumes; child completes with Success;
        // ToolResult is not an error.
        Assert.False(result.IsError,
            $"AgentTool must return IsError:false after auto-deny. " +
            $"Got content: {result.Content}");

        // ── (c) The child's final output (after denied call) is visible ────────
        // The child LLM was scripted to say "Child says: task complete after deny."
        // AgentTool surfaces finalText as the ToolResult content.
        Assert.Contains("task complete after deny", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 2 — Child's [Blocked] history contains the sub-agent deny message
    //
    // Spec §9.5: auto-deny is DenyOnce with message SubAgentDenyMessage.
    // The [Blocked] result fed back to the child LLM must contain SubAgentDenyMessage.
    //
    // We verify via the child LLM's received messages on its second call
    // (after auto-deny the child loop continues and the LLM is called again;
    // that second call's message history contains the tool result with the deny message).
    //
    // RED: today AgentTool never resumes; child LLM is called at most once.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChildAsk_AutoDenied_ChildHistoryContainsBlockedDenyMessage()
    {
        var ct = TestContext.Current.CancellationToken;

        var childUnresolved = new FakeTool("childUnresolved", _ => new ToolResult("nope"));
        var childLlm = new FakeChatClient();
        childLlm.EnqueueResponse(ToolCallResponse(("cid-1", "childUnresolved")));
        childLlm.EnqueueResponse(TextResponse("Child final."));

        StubPermissionEvaluator childEvaluator = ChildEvaluatorThatAsksFor("childUnresolved");
        var childRegistry = new ToolRegistry([childUnresolved]);

        var agentTool = new AgentTool(
            (_, _) =>
            {
                var childAgent = new Agent(childLlm, "child-model", permissions: childEvaluator);
                return (new AgentOptions(), childAgent, childRegistry);
            });

        var input = JsonSerializer.SerializeToElement(new { prompt = "run child" });
        await agentTool.InvokeAsync(input, ct);

        // After auto-deny the child LLM must be called a second time.
        // Its message history on the second call must contain a Tool-role message
        // with the [Blocked] + SubAgentDenyMessage string.
        Assert.True(
            childLlm.GetResponseCallCount >= 2,
            "Child LLM must be called at least twice: once before park, once after auto-deny.");

        IReadOnlyList<ChatMessage> secondCallMessages = childLlm.ReceivedMessages[1];

        bool blockedMessagePresent = secondCallMessages
            .Where(m => m.Role == ChatRole.Tool)
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Any(r =>
            {
                string content = r.Result?.ToString() ?? string.Empty;
                return content.Contains("[Blocked]", StringComparison.Ordinal)
                    && content.Contains(SubAgentDenyMessage, StringComparison.Ordinal);
            });

        Assert.True(
            blockedMessagePresent,
            $"Child LLM's second call must include a [Blocked] Tool-role message " +
            $"containing '{SubAgentDenyMessage}'.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 3 — Parent turn does NOT park; no permission events escape
    //
    // Spec §9.5: "Child agents therefore operate on rules only".
    // PermissionRequestedEvent and AwaitingPermission must NOT be visible in the
    // parent agent's event stream.  We verify by running a parent agent whose
    // only tool is the AgentTool; the parent event stream must contain no
    // PermissionRequestedEvent and the terminal status must not be AwaitingPermission.
    //
    // RED: today AgentTool returns IsError:true for AwaitingPermission, which may
    // cause the parent loop to see an error result, but NOT because of a re-park.
    // The test also verifies that even with today's behavior the parent never parks
    // (it currently returns an error result instead).
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ParentTurn_DoesNotPark_NoPermissionEventsEscape()
    {
        var ct = TestContext.Current.CancellationToken;

        var childUnresolved = new FakeTool("childUnresolved", _ => new ToolResult("nope"));
        var childLlm = new FakeChatClient();
        childLlm.EnqueueResponse(ToolCallResponse(("cid-1", "childUnresolved")));
        childLlm.EnqueueResponse(TextResponse("Child after deny."));

        StubPermissionEvaluator childEvaluator = ChildEvaluatorThatAsksFor("childUnresolved");
        var childRegistry = new ToolRegistry([childUnresolved]);

        var agentTool = new AgentTool(
            (_, _) =>
            {
                var childAgent = new Agent(childLlm, "child-model", permissions: childEvaluator);
                return (new AgentOptions(), childAgent, childRegistry);
            });

        // Parent: LLM calls subagent_tool once, then returns final text.
        var parentLlm = new FakeChatClient();
        parentLlm.EnqueueResponse(ToolCallResponse(("pid-1", "subagent_tool")));
        parentLlm.EnqueueResponse(TextResponse("Parent done."));

        var parentRegistry = new ToolRegistry([agentTool]);
        var parentAgent = new Agent(parentLlm, "parent-model");
        var parentCtx = new Context
        {
            Query = new QueryContext { Prompt = "do something" },
            Tools = new ToolContext { Registry = parentRegistry },
        };

        var parentEvents = new List<AgentEvent>();
        await foreach (AgentEvent evt in parentAgent.RunAsync(parentCtx, ct))
        {
            parentEvents.Add(evt);
        }

        // No PermissionRequestedEvent must appear in the parent stream.
        Assert.Empty(parentEvents.OfType<PermissionRequestedEvent>());

        // Parent turn must NOT be AwaitingPermission.
        AgentResultEvent parentResult = Assert.IsType<AgentResultEvent>(parentEvents[^1]);
        Assert.NotEqual(AgentResultStatus.AwaitingPermission, parentResult.Status);

        // Task 17 contract: parent batch completes with Success.
        // Today this assertion is RED — today AgentTool returns IsError:true, causing the
        // parent loop to see a tool error. After Task 17 the child completes normally and
        // the parent ToolResult is non-error, so the parent loop settles with Success.
        Assert.Equal(AgentResultStatus.Success, parentResult.Status);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 4 — Multiple child asks: both auto-denied with the same message
    //
    // Spec §9.5: "immediately calls ResumeWithPermissionsAsync answering every
    //   request DenyOnce with message SubAgentDenyMessage."
    // When the child batch has 2 unresolved calls → both auto-denied.
    // Child stream completes; parent settles.
    //
    // RED: today AgentTool never calls ResumeWithPermissionsAsync.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TwoChildAsks_BothAutoDenied_ChildStreamCompletes_ParentSettles()
    {
        var ct = TestContext.Current.CancellationToken;

        var childToolA = new FakeTool("childToolA", _ => new ToolResult("A result"));
        var childToolB = new FakeTool("childToolB", _ => new ToolResult("B result"));

        var childLlm = new FakeChatClient();
        // Child requests both tools in one batch → both park.
        childLlm.EnqueueResponse(ToolCallResponse(("cid-a", "childToolA"), ("cid-b", "childToolB")));
        // After both are auto-denied, child LLM sees two [Blocked] results and returns final text.
        childLlm.EnqueueResponse(TextResponse("Child done after both denied."));

        StubPermissionEvaluator childEvaluator = ChildEvaluatorThatAsksFor("childToolA", "childToolB");
        var childRegistry = new ToolRegistry([childToolA, childToolB]);

        var agentTool = new AgentTool(
            (_, _) =>
            {
                var childAgent = new Agent(childLlm, "child-model", permissions: childEvaluator);
                return (new AgentOptions(), childAgent, childRegistry);
            });

        var input = JsonSerializer.SerializeToElement(new { prompt = "do both" });
        ToolResult result = await agentTool.InvokeAsync(input, ct);

        // Neither child tool was invoked.
        Assert.Equal(0, childToolA.InvokeCount);
        Assert.Equal(0, childToolB.InvokeCount);

        // AgentTool returns a non-error result (child completed after auto-deny).
        Assert.False(result.IsError,
            $"AgentTool must return IsError:false after auto-denying both child asks. " +
            $"Got content: {result.Content}");

        // Child LLM was called at least twice: once before park, once after auto-deny.
        Assert.True(
            childLlm.GetResponseCallCount >= 2,
            "Child LLM must be called at least twice.");

        // Both auto-deny messages must appear in the child LLM's second call history.
        IReadOnlyList<ChatMessage> secondCallMessages = childLlm.ReceivedMessages[1];

        int blockedCount = secondCallMessages
            .Where(m => m.Role == ChatRole.Tool)
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Count(r =>
            {
                string content = r.Result?.ToString() ?? string.Empty;
                return content.Contains("[Blocked]", StringComparison.Ordinal)
                    && content.Contains(SubAgentDenyMessage, StringComparison.Ordinal);
            });

        Assert.Equal(2, blockedCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 5 — Child with mixed batch: one allowed, one auto-denied
    //
    // The child LLM calls two tools: one that the evaluator Allows, and one it Asks.
    // Only the Ask is auto-denied; the allowed tool runs normally.
    // The child then completes.
    //
    // RED: today AgentTool never resumes after park.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChildMixedBatch_AllowedToolRuns_UnresolvedAutoDenied_ParentSettles()
    {
        var ct = TestContext.Current.CancellationToken;

        var childAllowed = new FakeTool("childAllowed", _ => new ToolResult("allowed result"));
        var childUnresolved = new FakeTool("childUnresolved", _ => new ToolResult("should not run"));

        var childLlm = new FakeChatClient();
        // Child requests both in one batch.
        childLlm.EnqueueResponse(
            ToolCallResponse(("cid-ok", "childAllowed"), ("cid-deny", "childUnresolved")));
        // After auto-deny of the unresolved call, child continues.
        childLlm.EnqueueResponse(TextResponse("Child complete, used allowed tool."));

        var childEvaluator = new StubPermissionEvaluator();
        childEvaluator.Decisions["childAllowed"] = PermissionDecision.Allowed;
        childEvaluator.Decisions["childUnresolved"] = new PermissionDecision.Ask(null, "childUnresolved");

        var childRegistry = new ToolRegistry([childAllowed, childUnresolved]);

        var agentTool = new AgentTool(
            (_, _) =>
            {
                var childAgent = new Agent(childLlm, "child-model", permissions: childEvaluator);
                return (new AgentOptions(), childAgent, childRegistry);
            });

        var input = JsonSerializer.SerializeToElement(new { prompt = "mixed batch task" });
        ToolResult result = await agentTool.InvokeAsync(input, ct);

        // Allowed tool was invoked; unresolved tool was not.
        Assert.Equal(1, childAllowed.InvokeCount);
        Assert.Equal(0, childUnresolved.InvokeCount);

        // AgentTool returns a non-error result.
        Assert.False(result.IsError,
            $"AgentTool must return IsError:false. Got: {result.Content}");
    }
}
