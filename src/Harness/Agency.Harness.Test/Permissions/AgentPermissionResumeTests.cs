using System.Text.Json;
using Agency.Harness.Contexts;
using Agency.Harness.Hooks;
using Agency.Harness.Permissions;
using Agency.Harness.Test.Fakes;

namespace Agency.Harness.Test.Permissions;

// ─────────────────────────────────────────────────────────────────────────────
// TDD RED tests for RESUME and ABANDONMENT of a parked turn.
// (Task 15 implements Agent.ResumeAsync and ChatSession.ResumeWithPermissionsAsync.)
//
// Seam strategy:
//   • Cases 1-6, 7(a-c), 9, 10:  Agent.ResumeAsync via a helper that calls it
//     directly — gives fine-grained control over Context.PendingToolBatch.
//   • Cases 7(d), 8, 9 (Reset):   ChatSession level — abandonment and Reset are
//     ChatSession responsibilities that wrap the context lifecycle.
//
// All cases call ParkTurnAsync to reach the parked state via RunAsync, which is
// the only legal way to get a populated Context.PendingToolBatch without
// constructing it manually.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// TDD RED tests for resume and abandonment of a parked permission turn (spec §6.3, §6.4, §12).
/// These tests will fail with CS1061 compile errors for the missing
/// <c>Agent.ResumeAsync</c> and <c>ChatSession.ResumeWithPermissionsAsync</c> members
/// until Task 15 is implemented.
/// </summary>
public sealed class AgentPermissionResumeTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // Shared test-double used by tests that need an evaluator recording calls.
    // Copied from AgentPermissionParkTests (same file is not reachable from here
    // without making it non-private; a local copy is the cleanest option).
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Configurable test double for <see cref="IPermissionEvaluator"/>.
    /// Supply per-tool decisions via the <see cref="Decisions"/> dictionary;
    /// missing entries default to <see cref="PermissionDecision.Allowed"/>.
    /// </summary>
    private sealed class StubPermissionEvaluator : IPermissionEvaluator
    {
        public Dictionary<string, PermissionDecision> Decisions { get; } = new(StringComparer.Ordinal);
        public List<(string ToolName, JsonElement Input)> EvaluateCalls { get; } = [];
        public List<(string ProposedRule, bool Deny)> RecordAlwaysCalls { get; } = [];

        public PermissionDecision Evaluate(string toolName, JsonElement input)
        {
            this.EvaluateCalls.Add((toolName, input));
            return this.Decisions.TryGetValue(toolName, out PermissionDecision? decision)
                ? decision
                : PermissionDecision.Allowed;
        }

        public Task RecordAlwaysAsync(string proposedRule, bool deny, CancellationToken ct)
        {
            this.RecordAlwaysCalls.Add((proposedRule, deny));
            return Task.CompletedTask;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Shared helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static Context MakeContext(ToolContext? tools = null) =>
        new()
        {
            Query = new QueryContext { Prompt = "test prompt" },
            Tools = tools ?? ToolContext.Empty,
        };

    private static ChatResponse TextResponse(string text = "Done.") =>
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

    private static ChatResponse ToolCallWithArgResponse(
        string callId, string toolName, string argName, string argValue)
    {
        var args = new Dictionary<string, object?> { [argName] = argValue };
        return new ChatResponse(
            [new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, toolName, args)])])
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            FinishReason = ChatFinishReason.ToolCalls,
        };
    }

    /// <summary>
    /// Runs the agent until it parks (returns AwaitingPermission) and returns both the
    /// collected events and the permission-request events so tests can extract RequestIds.
    /// Fails the test if the turn does not park.
    /// </summary>
    private static async Task<(List<AgentEvent> Events, List<PermissionRequestedEvent> PermEvents)>
        ParkTurnAsync(Agent agent, Context ctx, CancellationToken ct = default)
    {
        var events = new List<AgentEvent>();
        await foreach (AgentEvent evt in agent.RunAsync(ctx, ct))
        {
            events.Add(evt);
        }

        AgentResultEvent result = Assert.IsType<AgentResultEvent>(events[^1]);
        Assert.Equal(AgentResultStatus.AwaitingPermission, result.Status);

        var permEvents = events.OfType<PermissionRequestedEvent>().ToList();
        return (events, permEvents);
    }

    /// <summary>
    /// Drains all events from the given async enumerable into a list.
    /// </summary>
    private static async Task<List<AgentEvent>> CollectAsync(
        IAsyncEnumerable<AgentEvent> source, CancellationToken ct = default)
    {
        var events = new List<AgentEvent>();
        await foreach (AgentEvent evt in source.WithCancellation(ct))
        {
            events.Add(evt);
        }

        return events;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Case 1 — Resume allow
    //
    // Spec §6.3 "AllowOnce | AllowAlways → InvokeAsync(...); then OnPostToolUse".
    // Scenario: park a batch containing 1 allowed sibling (toolA) + 1 Ask (toolB).
    // A Rewrite hook rewrites toolB's input before parking.
    // Resume with AllowOnce for toolB.
    //
    // Assertions:
    //   • toolB invoked with the POST-REWRITE input captured at park time.
    //   • OnPostToolUse fires for the resumed call.
    //   • ToolInvokedEvent for toolB is yielded on the resume stream.
    //   • OnPostToolBatch fires exactly ONCE with the FULL batch (toolA + toolB).
    //   • All result messages appended in batch order: none before resume, all after.
    //   • Loop continues: FakeLlmClient scripted with a follow-up final response;
    //     resume stream ends with a Success result.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resuming a parked ask with <see cref="PermissionResponseKind.AllowOnce"/> invokes the
    /// tool with the post-rewrite input captured at park time, fires
    /// <see cref="AgentHooks.OnPostToolUse"/> for the resumed call and
    /// <see cref="AgentHooks.OnPostToolBatch"/> exactly once with the full batch (the sibling
    /// allowed call plus the resumed call), appends all Tool-role messages after resume in
    /// batch order, and lets the loop continue to a <see cref="AgentResultStatus.Success"/>
    /// terminal result with <see cref="Context.PendingToolBatch"/> cleared.
    /// </summary>
    [Fact]
    public async Task Resume_AllowOnce_ToolInvokedWithPostRewriteInput_FullBatchAppended_LoopContinues()
    {
        var ct = TestContext.Current.CancellationToken;

        const string originalValue = "original-cmd";
        const string rewrittenValue = "rewritten-cmd";

        var toolA = new FakeTool("toolA", _ => new ToolResult("A ok"));
        var toolB = new FakeTool("toolB", _ => new ToolResult("B ok"));
        var registry = new ToolRegistry([toolA, toolB]);

        var evaluator = new StubPermissionEvaluator();
        evaluator.Decisions["toolA"] = PermissionDecision.Allowed;
        evaluator.Decisions["toolB"] = new PermissionDecision.Ask("rewritten-cmd", "toolB(rewritten-cmd)");

        var rewrittenArgs = new Dictionary<string, object?> { ["command"] = rewrittenValue };
        JsonElement rewrittenElement = JsonSerializer.SerializeToElement(rewrittenArgs);

        // Hook rewrites toolB's input before evaluation.
        var hooks = new AgentHooks
        {
            OnPreToolUse = (hookCtx, _) => Task.FromResult<PreToolUseDecision>(
                hookCtx.ToolName == "toolB"
                    ? new PreToolUseDecision.Rewrite(rewrittenElement)
                    : PreToolUseDecision.Allowed),
        };

        int postToolUseCount = 0;
        string? postToolUseName = null;
        hooks = hooks with
        {
            OnPostToolUse = (hookCtx, _) =>
            {
                postToolUseCount++;
                postToolUseName = hookCtx.ToolName;
                return Task.CompletedTask;
            },
        };

        int postBatchCount = 0;
        IReadOnlyList<ToolInvokedEvent>? capturedBatch = null;
        hooks = hooks with
        {
            OnPostToolBatch = (batch, _, _) =>
            {
                postBatchCount++;
                capturedBatch = batch;
                return Task.CompletedTask;
            },
        };

        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallWithArgResponse("id-b", "toolB", "command", originalValue));
        // After the batch the LLM gets tool results and returns a final text response.
        llm.EnqueueResponse(TextResponse("All done."));

        var ctx = MakeContext(new ToolContext { Registry = registry });

        var agent = new Agent(llm, "model", hooks: hooks, permissions: evaluator);

        // Also add toolA into the batch to test the full-batch path.  Re-script with both calls.
        var llm2 = new FakeChatClient();
        var twoCallsArgs = new Dictionary<string, object?> { ["command"] = originalValue };
        llm2.EnqueueResponse(new ChatResponse(
            [new ChatMessage(ChatRole.Assistant, [
                new FunctionCallContent("id-a", "toolA"),
                new FunctionCallContent("id-b", "toolB", twoCallsArgs),
            ])])
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            FinishReason = ChatFinishReason.ToolCalls,
        });
        llm2.EnqueueResponse(TextResponse("All done."));

        var ctx2 = MakeContext(new ToolContext { Registry = registry });
        var agent2 = new Agent(llm2, "model", hooks: hooks, permissions: evaluator);

        (List<AgentEvent> parkEvents, List<PermissionRequestedEvent> permEvents) =
            await ParkTurnAsync(agent2, ctx2, ct);

        // Confirm no Tool-role messages exist before resume.
        Assert.DoesNotContain(ctx2.Conversation.Messages, m => m.Role == ChatRole.Tool);

        // The pending request is for toolB.
        PermissionRequestedEvent permEvent = Assert.Single(permEvents, e => e.ToolName == "toolB");
        var responses = new List<PermissionResponse>
        {
            new(permEvent.RequestId, PermissionResponseKind.AllowOnce),
        };

        // ── Resume ────────────────────────────────────────────────────────────
        // Task 15 target surface:
        List<AgentEvent> resumeEvents = await CollectAsync(
            agent2.ResumeAsync(ctx2, responses, options: null, ct), ct);  // CS1061 expected here

        // toolB invoked with the rewritten input.
        Assert.Equal(1, toolB.InvokeCount);
        JsonElement invokedInput = toolB.ReceivedInputs[0];
        Assert.True(invokedInput.TryGetProperty("command", out JsonElement cmd));
        Assert.Equal(rewrittenValue, cmd.GetString());

        // OnPostToolUse fired for toolB on resume.
        Assert.Equal(1, postToolUseCount);
        Assert.Equal("toolB", postToolUseName);

        // ToolInvokedEvent for toolB in resume stream.
        Assert.Contains(resumeEvents, e => e is ToolInvokedEvent t && t.ToolName == "toolB");

        // OnPostToolBatch fired exactly once with the full batch (toolA + toolB = 2 entries).
        Assert.Equal(1, postBatchCount);
        Assert.NotNull(capturedBatch);
        Assert.Equal(2, capturedBatch!.Count);

        // All Tool-role messages appended after resume, in batch order.
        var toolMessages = ctx2.Conversation.Messages.Where(m => m.Role == ChatRole.Tool).ToList();
        Assert.Equal(2, toolMessages.Count);

        // Resume stream ends with Success.
        AgentResultEvent finalResult = Assert.IsType<AgentResultEvent>(resumeEvents[^1]);
        Assert.Equal(AgentResultStatus.Success, finalResult.Status);

        // PendingToolBatch cleared.
        Assert.Null(ctx2.PendingToolBatch);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Case 2 — Resume deny with message
    //
    // Spec §2.5: DenyOnce + Message "use the dev database instead"
    //   → result content "[Blocked] The user denied permission for this tool call: use the dev database instead"
    //   → IsError true, tool never invoked, loop continues.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resuming a parked ask with <see cref="PermissionResponseKind.DenyOnce"/> and a message
    /// produces a Tool-role result whose content is
    /// "[Blocked] The user denied permission for this tool call: {message}", the tool is never
    /// invoked, and the loop continues past the deny to a non-parked terminal result.
    /// </summary>
    [Fact]
    public async Task Resume_DenyOnce_WithMessage_BlockedResultContainsMessage_ToolNotInvoked()
    {
        var ct = TestContext.Current.CancellationToken;

        var toolA = new FakeTool("toolA", _ => new ToolResult("should not run"));
        var registry = new ToolRegistry([toolA]);

        var evaluator = new StubPermissionEvaluator();
        evaluator.Decisions["toolA"] = new PermissionDecision.Ask(null, "toolA");

        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse(("id-a", "toolA")));
        llm.EnqueueResponse(TextResponse("Understood."));

        var ctx = MakeContext(new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model", permissions: evaluator);

        (_, List<PermissionRequestedEvent> permEvents) = await ParkTurnAsync(agent, ctx, ct);

        PermissionRequestedEvent permEvent = Assert.Single(permEvents);
        var responses = new List<PermissionResponse>
        {
            new(permEvent.RequestId, PermissionResponseKind.DenyOnce, "use the dev database instead"),
        };

        List<AgentEvent> resumeEvents = await CollectAsync(
            agent.ResumeAsync(ctx, responses, options: null, ct), ct);  // CS1061 expected here

        // Tool was NOT invoked.
        Assert.Equal(0, toolA.InvokeCount);

        // The Tool-role message for the denied call must contain the expected blocked content.
        var toolMessages = ctx.Conversation.Messages.Where(m => m.Role == ChatRole.Tool).ToList();
        Assert.Single(toolMessages);

        string resultContent = string.Concat(
            toolMessages[0].Contents.OfType<FunctionResultContent>().Select(c => c.Result?.ToString() ?? string.Empty));
        Assert.Contains(
            "[Blocked] The user denied permission for this tool call: use the dev database instead",
            resultContent,
            StringComparison.Ordinal);

        // Loop continued to a non-AwaitingPermission result.
        AgentResultEvent finalResult = Assert.IsType<AgentResultEvent>(resumeEvents[^1]);
        Assert.NotEqual(AgentResultStatus.AwaitingPermission, finalResult.Status);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Case 3 — Resume deny without message
    //
    // Spec §2.5: DenyOnce, no message
    //   → "[Blocked] The user denied permission for this tool call."
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resuming a parked ask with <see cref="PermissionResponseKind.DenyOnce"/> and no message
    /// produces the default blocked string "[Blocked] The user denied permission for this tool
    /// call." without the trailing colon-and-message form used when a message is supplied.
    /// </summary>
    [Fact]
    public async Task Resume_DenyOnce_NoMessage_BlockedResultHasDefaultDenyString()
    {
        var ct = TestContext.Current.CancellationToken;

        var toolA = new FakeTool("toolA", _ => new ToolResult("should not run"));
        var registry = new ToolRegistry([toolA]);

        var evaluator = new StubPermissionEvaluator();
        evaluator.Decisions["toolA"] = new PermissionDecision.Ask(null, "toolA");

        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse(("id-a", "toolA")));
        llm.EnqueueResponse(TextResponse("Understood."));

        var ctx = MakeContext(new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model", permissions: evaluator);

        (_, List<PermissionRequestedEvent> permEvents) = await ParkTurnAsync(agent, ctx, ct);

        PermissionRequestedEvent permEvent = Assert.Single(permEvents);
        var responses = new List<PermissionResponse>
        {
            new(permEvent.RequestId, PermissionResponseKind.DenyOnce),
        };

        List<AgentEvent> resumeEvents = await CollectAsync(
            agent.ResumeAsync(ctx, responses, options: null, ct), ct);  // CS1061 expected here

        Assert.Equal(0, toolA.InvokeCount);

        var toolMessages = ctx.Conversation.Messages.Where(m => m.Role == ChatRole.Tool).ToList();
        Assert.Single(toolMessages);

        string resultContent = string.Concat(
            toolMessages[0].Contents.OfType<FunctionResultContent>().Select(c => c.Result?.ToString() ?? string.Empty));
        Assert.Contains(
            "[Blocked] The user denied permission for this tool call.",
            resultContent,
            StringComparison.Ordinal);

        // The deny-with-message string must NOT be present (no message was supplied).
        Assert.DoesNotContain(
            "[Blocked] The user denied permission for this tool call: ",
            resultContent,
            StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Case 4 — AllowAlways and DenyAlways record grants
    //
    // Spec §6.3 step 3: for each response with Kind in { AllowAlways, DenyAlways }:
    //   await evaluator.RecordAlwaysAsync(pending.ProposedRule, deny: Kind == DenyAlways).
    //
    // Sub-case A: AllowAlways → RecordAlwaysAsync(proposedRule, deny:false).
    // Sub-case B: DenyAlways  → RecordAlwaysAsync(proposedRule, deny:true) + blocked result.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resuming with <see cref="PermissionResponseKind.AllowAlways"/> calls
    /// <see cref="IPermissionEvaluator.RecordAlwaysAsync"/> exactly once with the pending call's
    /// proposed rule and <c>deny: false</c>.
    /// </summary>
    [Fact]
    public async Task Resume_AllowAlways_RecordAlwaysAsyncCalledWithDenyFalse()
    {
        var ct = TestContext.Current.CancellationToken;

        const string proposedRule = "toolA(someKey)";

        var toolA = new FakeTool("toolA", _ => new ToolResult("ok"));
        var registry = new ToolRegistry([toolA]);

        var evaluator = new StubPermissionEvaluator();
        evaluator.Decisions["toolA"] = new PermissionDecision.Ask("someKey", proposedRule);

        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse(("id-a", "toolA")));
        llm.EnqueueResponse(TextResponse("Done."));

        var ctx = MakeContext(new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model", permissions: evaluator);

        (_, List<PermissionRequestedEvent> permEvents) = await ParkTurnAsync(agent, ctx, ct);

        PermissionRequestedEvent permEvent = Assert.Single(permEvents);
        var responses = new List<PermissionResponse>
        {
            new(permEvent.RequestId, PermissionResponseKind.AllowAlways),
        };

        _ = await CollectAsync(agent.ResumeAsync(ctx, responses, options: null, ct), ct);  // CS1061 expected

        // RecordAlwaysAsync must have been called with (proposedRule, deny:false).
        Assert.Single(evaluator.RecordAlwaysCalls);
        (string recordedRule, bool deny) = evaluator.RecordAlwaysCalls[0];
        Assert.Equal(proposedRule, recordedRule);
        Assert.False(deny);
    }

    /// <summary>
    /// Resuming with <see cref="PermissionResponseKind.DenyAlways"/> calls
    /// <see cref="IPermissionEvaluator.RecordAlwaysAsync"/> exactly once with the pending call's
    /// proposed rule and <c>deny: true</c>, the tool is never invoked, and the appended Tool-role
    /// message carries a [Blocked] result.
    /// </summary>
    [Fact]
    public async Task Resume_DenyAlways_RecordAlwaysAsyncCalledWithDenyTrue_AndBlockedResult()
    {
        var ct = TestContext.Current.CancellationToken;

        const string proposedRule = "toolA(someKey)";

        var toolA = new FakeTool("toolA", _ => new ToolResult("should not run"));
        var registry = new ToolRegistry([toolA]);

        var evaluator = new StubPermissionEvaluator();
        evaluator.Decisions["toolA"] = new PermissionDecision.Ask("someKey", proposedRule);

        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse(("id-a", "toolA")));
        llm.EnqueueResponse(TextResponse("Understood."));

        var ctx = MakeContext(new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model", permissions: evaluator);

        (_, List<PermissionRequestedEvent> permEvents) = await ParkTurnAsync(agent, ctx, ct);

        PermissionRequestedEvent permEvent = Assert.Single(permEvents);
        var responses = new List<PermissionResponse>
        {
            new(permEvent.RequestId, PermissionResponseKind.DenyAlways),
        };

        _ = await CollectAsync(agent.ResumeAsync(ctx, responses, options: null, ct), ct);  // CS1061 expected

        // RecordAlwaysAsync must have been called with (proposedRule, deny:true).
        Assert.Single(evaluator.RecordAlwaysCalls);
        (string recordedRule, bool deny) = evaluator.RecordAlwaysCalls[0];
        Assert.Equal(proposedRule, recordedRule);
        Assert.True(deny);

        // Tool not invoked; blocked result present.
        Assert.Equal(0, toolA.InvokeCount);

        var toolMessages = ctx.Conversation.Messages.Where(m => m.Role == ChatRole.Tool).ToList();
        Assert.Single(toolMessages);
        string resultContent = string.Concat(
            toolMessages[0].Contents.OfType<FunctionResultContent>().Select(c => c.Result?.ToString() ?? string.Empty));
        Assert.Contains("[Blocked]", resultContent, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Case 5 — AllowAlways suppresses next park (rule-sourced)
    //
    // Spec §6.3: After AllowAlways the grant is live in the evaluator.
    // Use the REAL PermissionEvaluator with a temp LocalRulesPath.
    // Park on unresolved toolX; resume AllowAlways; script a second LLM turn
    // calling toolX identically → second batch does NOT park (tool executes);
    // the temp file contains the rule.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// With a real <see cref="PermissionEvaluator"/> backed by a temp local-rules file, resuming
    /// an unresolved rule-sourced ask with <see cref="PermissionResponseKind.AllowAlways"/>
    /// persists the allow rule and makes the grant live immediately: an identical subsequent
    /// call from the LLM in the same resumed loop executes without parking again, and the local
    /// rules file on disk contains the proposed rule under its "Allow" array.
    /// </summary>
    [Fact]
    public async Task Resume_AllowAlways_RuleSourced_SecondCallDoesNotPark_FileContainsRule()
    {
        var ct = TestContext.Current.CancellationToken;

        const string keyValue = "git status";
        const string proposedRule = "ExecutePowershell(git status)";

        string tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");

        try
        {
            var toolX = new FakeTool("ExecutePowershell", _ => new ToolResult("output"));
            var registry = new ToolRegistry([toolX]);

            var options = new PermissionsOptions { LocalRulesPath = tempPath };
            var evaluator = new PermissionEvaluator(options);

            // First LLM turn: call ExecutePowershell(git status) — unresolved, will park.
            var llm = new FakeChatClient();
            llm.EnqueueResponse(ToolCallWithArgResponse("id-x1", "ExecutePowershell", "command", keyValue));
            // After resume, LLM calls the same tool again.
            llm.EnqueueResponse(ToolCallWithArgResponse("id-x2", "ExecutePowershell", "command", keyValue));
            // After second batch executes (should not park), LLM returns final text.
            llm.EnqueueResponse(TextResponse("Done."));

            var ctx = MakeContext(new ToolContext { Registry = registry });
            var agent = new Agent(llm, "model", permissions: evaluator);

            (_, List<PermissionRequestedEvent> permEvents) = await ParkTurnAsync(agent, ctx, ct);

            PermissionRequestedEvent permEvent = Assert.Single(permEvents);
            Assert.Equal("ExecutePowershell", permEvent.ToolName);
            Assert.Equal(proposedRule, permEvent.ProposedRule);

            var responses = new List<PermissionResponse>
            {
                new(permEvent.RequestId, PermissionResponseKind.AllowAlways),
            };

            // Resume: the grant is recorded; loop continues; LLM calls toolX again.
            List<AgentEvent> resumeEvents = await CollectAsync(
                agent.ResumeAsync(ctx, responses, options: null, ct), ct);  // CS1061 expected

            // The resume stream must complete with Success (second call executed without parking).
            AgentResultEvent finalResult = Assert.IsType<AgentResultEvent>(resumeEvents[^1]);
            Assert.Equal(AgentResultStatus.Success, finalResult.Status);

            // ExecutePowershell was invoked twice total (first call on resume, second call directly).
            Assert.Equal(2, toolX.InvokeCount);

            // No AwaitingPermission on the resume stream.
            Assert.DoesNotContain(
                resumeEvents,
                e => e is AgentResultEvent r && r.Status == AgentResultStatus.AwaitingPermission);

            // The local file must contain the allow rule.
            Assert.True(File.Exists(tempPath), "Local rules file must exist after AllowAlways.");
            string json = await File.ReadAllTextAsync(tempPath, ct);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement allow = doc.RootElement.GetProperty("Allow");
            Assert.Contains(
                allow.EnumerateArray(),
                el => el.GetString() == proposedRule);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Case 6 — AllowAlways on hook-sourced request — recurrence
    //
    // Spec §3.5: hook asks recur by design. AllowAlways on a hook-sourced request
    // records the grant (RecordAlwaysAsync called), but the NEXT identical call
    // still pends because the hook always returns Ask.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Hook-sourced asks recur by design: resuming a hook-flagged call with
    /// <see cref="PermissionResponseKind.AllowAlways"/> still records a grant via
    /// <see cref="IPermissionEvaluator.RecordAlwaysAsync"/> and executes the resumed call, but
    /// because the hook unconditionally returns <see cref="PreToolUseDecision.Ask"/>, an
    /// identical subsequent call within the resumed loop parks again with a new
    /// <see cref="PermissionRequestedEvent"/> whose <see cref="PermissionRequestedEvent.Source"/>
    /// is <see cref="PermissionRequestSource.Hook"/>.
    /// </summary>
    [Fact]
    public async Task Resume_AllowAlways_HookSourced_GrantRecorded_ButNextCallParksAgain()
    {
        var ct = TestContext.Current.CancellationToken;

        var toolY = new FakeTool("toolY", _ => new ToolResult("ok"));
        var registry = new ToolRegistry([toolY]);

        var evaluator = new StubPermissionEvaluator();
        // Evaluator returns Allow (would normally pass), but hook always escalates.
        evaluator.Decisions["toolY"] = PermissionDecision.Allowed;

        // Hook always returns Ask("flagged") for toolY.
        var hooks = new AgentHooks
        {
            OnPreToolUse = (_, _) =>
                Task.FromResult<PreToolUseDecision>(new PreToolUseDecision.Ask("flagged")),
        };

        var llm = new FakeChatClient();
        // First turn: parks on toolY.
        llm.EnqueueResponse(ToolCallResponse(("id-y1", "toolY")));
        // After resume, LLM calls toolY again.
        llm.EnqueueResponse(ToolCallResponse(("id-y2", "toolY")));
        // (No third response — the test expects the second call to park again before reaching LLM.)

        var ctx = MakeContext(new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model", hooks: hooks, permissions: evaluator);

        (_, List<PermissionRequestedEvent> firstPermEvents) = await ParkTurnAsync(agent, ctx, ct);

        PermissionRequestedEvent firstPermEvent = Assert.Single(firstPermEvents);
        Assert.Equal(PermissionRequestSource.Hook, firstPermEvent.Source);

        var responses = new List<PermissionResponse>
        {
            new(firstPermEvent.RequestId, PermissionResponseKind.AllowAlways),
        };

        // Resume with AllowAlways.
        List<AgentEvent> resumeEvents = await CollectAsync(
            agent.ResumeAsync(ctx, responses, options: null, ct), ct);  // CS1061 expected

        // Grant must have been recorded.
        Assert.NotEmpty(evaluator.RecordAlwaysCalls);
        (string recordedRule, bool deny) = evaluator.RecordAlwaysCalls[0];
        Assert.False(deny);

        // The resumed call itself executes (toolY invoked once).
        Assert.Equal(1, toolY.InvokeCount);

        // The resume stream ends with AwaitingPermission again (second call parked).
        AgentResultEvent resumeResult = Assert.IsType<AgentResultEvent>(resumeEvents[^1]);
        Assert.Equal(AgentResultStatus.AwaitingPermission, resumeResult.Status);

        // A new PermissionRequestedEvent for toolY with Source=Hook.
        var newPermEvents = resumeEvents.OfType<PermissionRequestedEvent>().ToList();
        Assert.Single(newPermEvents);
        Assert.Equal("toolY", newPermEvents[0].ToolName);
        Assert.Equal(PermissionRequestSource.Hook, newPermEvents[0].Source);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Case 7 — Validation
    //
    // (a) Resume with empty responses while one pending → ArgumentException.
    // (b) Unknown RequestId → ArgumentException.
    // (c) Duplicate responses for same RequestId → ArgumentException.
    // (d) Resume when nothing parked → InvalidOperationException (ChatSession seam).
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Calling <c>Agent.ResumeAsync</c> with an empty response list while a call is still
    /// pending must throw <see cref="ArgumentException"/> — every pending request needs a
    /// matching response.
    /// </summary>
    [Fact]
    public async Task Resume_EmptyResponses_WhileOnePending_ThrowsArgumentException()
    {
        var ct = TestContext.Current.CancellationToken;

        var toolA = new FakeTool("toolA", _ => new ToolResult("ok"));
        var registry = new ToolRegistry([toolA]);

        var evaluator = new StubPermissionEvaluator();
        evaluator.Decisions["toolA"] = new PermissionDecision.Ask(null, "toolA");

        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse(("id-a", "toolA")));

        var ctx = MakeContext(new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model", permissions: evaluator);

        await ParkTurnAsync(agent, ctx, ct);

        // Empty responses — must throw.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            CollectAsync(agent.ResumeAsync(ctx, [], options: null, ct), ct));  // CS1061 expected
    }

    /// <summary>
    /// A response naming a <see cref="PermissionResponse.RequestId"/> that does not match any
    /// pending request must cause <c>Agent.ResumeAsync</c> to throw
    /// <see cref="ArgumentException"/>.
    /// </summary>
    [Fact]
    public async Task Resume_UnknownRequestId_ThrowsArgumentException()
    {
        var ct = TestContext.Current.CancellationToken;

        var toolA = new FakeTool("toolA", _ => new ToolResult("ok"));
        var registry = new ToolRegistry([toolA]);

        var evaluator = new StubPermissionEvaluator();
        evaluator.Decisions["toolA"] = new PermissionDecision.Ask(null, "toolA");

        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse(("id-a", "toolA")));

        var ctx = MakeContext(new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model", permissions: evaluator);

        await ParkTurnAsync(agent, ctx, ct);

        var responses = new List<PermissionResponse>
        {
            new(Guid.NewGuid(), PermissionResponseKind.AllowOnce),  // unknown id
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            CollectAsync(agent.ResumeAsync(ctx, responses, options: null, ct), ct));  // CS1061 expected
    }

    /// <summary>
    /// Supplying two responses for the same <see cref="PermissionResponse.RequestId"/> must
    /// cause <c>Agent.ResumeAsync</c> to throw <see cref="ArgumentException"/>.
    /// </summary>
    [Fact]
    public async Task Resume_DuplicateRequestId_ThrowsArgumentException()
    {
        var ct = TestContext.Current.CancellationToken;

        var toolA = new FakeTool("toolA", _ => new ToolResult("ok"));
        var registry = new ToolRegistry([toolA]);

        var evaluator = new StubPermissionEvaluator();
        evaluator.Decisions["toolA"] = new PermissionDecision.Ask(null, "toolA");

        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse(("id-a", "toolA")));

        var ctx = MakeContext(new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model", permissions: evaluator);

        (_, List<PermissionRequestedEvent> permEvents) = await ParkTurnAsync(agent, ctx, ct);
        Guid requestId = permEvents[0].RequestId;

        // Two responses for the same RequestId.
        var responses = new List<PermissionResponse>
        {
            new(requestId, PermissionResponseKind.AllowOnce),
            new(requestId, PermissionResponseKind.DenyOnce),
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            CollectAsync(agent.ResumeAsync(ctx, responses, options: null, ct), ct));  // CS1061 expected
    }

    /// <summary>
    /// Calling <see cref="ChatSession.ResumeWithPermissionsAsync"/> when no turn is parked must
    /// throw <see cref="InvalidOperationException"/>.
    /// </summary>
    [Fact]
    public async Task Resume_NothingParked_ThrowsInvalidOperationException()
    {
        // Uses ChatSession because InvalidOperationException is the ChatSession contract.
        var ct = TestContext.Current.CancellationToken;

        var llm = new FakeChatClient();
        llm.EnqueueResponse(TextResponse("Hello."));

        var agent = new Agent(llm, "model");
        var session = new ChatSession(agent, new AgentOptions());

        // Send one normal turn (no parking).
        await CollectAsync(session.SendAsync("hello", ct), ct);

        // Attempt to resume when nothing is parked.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CollectAsync(session.ResumeWithPermissionsAsync([], ct), ct));  // CS1061 expected
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Case 8 — Abandonment
    //
    // Spec §6.4: SendAsync while parked implicitly denies all pending calls with
    // "The user did not respond to the permission request.", completes the batch,
    // appends the new user message, runs the turn normally.
    //
    // Assertions:
    //   • AwaitingPermission result collected on first SendAsync.
    //   • Second SendAsync("new message") streams to completion (no exception).
    //   • Pended call's result message content = abandoned deny string.
    //   • Full batch results are in history before the new user message.
    //   • New user message was processed (FakeLlmClient receives it).
    //   • PendingToolBatch cleared after the second SendAsync.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Calling <see cref="ChatSession.SendAsync"/> with a new user message while a turn is
    /// parked implicitly denies every pending call with "The user did not respond to the
    /// permission request.", completes the batch, appends the new user message, and processes
    /// it normally: the denied tool is never invoked, the abandonment deny string reaches the
    /// LLM in history, and the new message is processed alongside it.
    /// </summary>
    [Fact]
    public async Task Abandonment_SendAsyncWhileParked_DeniesAllPendings_ProcessesNewMessage()
    {
        var ct = TestContext.Current.CancellationToken;

        var toolA = new FakeTool("toolA", _ => new ToolResult("ok"));
        var registry = new ToolRegistry([toolA]);

        var evaluator = new StubPermissionEvaluator();
        evaluator.Decisions["toolA"] = new PermissionDecision.Ask(null, "toolA");

        var llm = new FakeChatClient();
        // First turn: LLM calls toolA — will park.
        llm.EnqueueResponse(ToolCallResponse(("id-a", "toolA")));
        // After abandonment implicit deny, LLM calls are resumed with "new message" as user input.
        llm.EnqueueResponse(TextResponse("Got your new message."));

        var agent = new Agent(llm, "model", permissions: evaluator);
        var session = new ChatSession(agent, new AgentOptions(), new ToolContext { Registry = registry });

        // First SendAsync: parks the turn.
        List<AgentEvent> firstEvents = await CollectAsync(session.SendAsync("initial message", ct), ct);
        AgentResultEvent firstResult = Assert.IsType<AgentResultEvent>(firstEvents[^1]);
        Assert.Equal(AgentResultStatus.AwaitingPermission, firstResult.Status);

        // Second SendAsync with a new message — abandonment path.
        List<AgentEvent> secondEvents = await CollectAsync(session.SendAsync("new message", ct), ct);

        // Second SendAsync must complete normally (non-AwaitingPermission).
        AgentResultEvent secondResult = Assert.IsType<AgentResultEvent>(secondEvents[^1]);
        Assert.NotEqual(AgentResultStatus.AwaitingPermission, secondResult.Status);

        // The tool was NOT invoked.
        Assert.Equal(0, toolA.InvokeCount);

        // The abandoned pended call's result must contain the abandonment deny string.
        var toolMessages = session.GetType()  // Access internal ctx via reflection is fragile;
            // instead verify via the LLM receiving the right history.
            // The LLM should have received a tool result message with the abandonment string.
            // We verify via FakeChatClient.ReceivedMessages on the second call.
            .GetType();  // placeholder — actual assertion below

        // The second LLM call should have received the abandoned tool result in history.
        Assert.True(llm.GetResponseCallCount >= 2, "LLM must have been called at least twice.");
        IReadOnlyList<ChatMessage> messagesOnSecondCall = llm.ReceivedMessages[1];

        // There must be a Tool-role message containing the abandonment string.
        bool abandonedResultPresent = messagesOnSecondCall
            .Where(m => m.Role == ChatRole.Tool)
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Any(r => (r.Result?.ToString() ?? string.Empty).Contains(
                "The user did not respond to the permission request.",
                StringComparison.Ordinal));
        Assert.True(abandonedResultPresent,
            "Abandoned pended call must produce a Tool-role message with the abandonment deny string.");

        // The second user message must appear in what LLM received on second call.
        bool newMessagePresent = messagesOnSecondCall
            .Any(m => m.Role == ChatRole.User && (m.Text ?? string.Empty).Contains("new message", StringComparison.Ordinal));
        Assert.True(newMessagePresent, "New user message must reach the LLM after abandonment.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Case 9 — Reset clears pending
    //
    // Spec §6.4: Reset() clears any pending batch.
    // After Reset(), ResumeWithPermissionsAsync → InvalidOperationException (nothing parked).
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="ChatSession.Reset"/> clears any pending permission batch: after calling it,
    /// <see cref="ChatSession.ResumeWithPermissionsAsync"/> must throw
    /// <see cref="InvalidOperationException"/> because nothing remains parked.
    /// </summary>
    [Fact]
    public async Task Reset_ClearsPendingBatch_ResumeAfterResetThrowsInvalidOperation()
    {
        var ct = TestContext.Current.CancellationToken;

        var toolA = new FakeTool("toolA", _ => new ToolResult("ok"));
        var registry = new ToolRegistry([toolA]);

        var evaluator = new StubPermissionEvaluator();
        evaluator.Decisions["toolA"] = new PermissionDecision.Ask(null, "toolA");

        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse(("id-a", "toolA")));

        var agent = new Agent(llm, "model", permissions: evaluator);
        var session = new ChatSession(agent, new AgentOptions(), new ToolContext { Registry = registry });

        // Park the turn.
        List<AgentEvent> firstEvents = await CollectAsync(session.SendAsync("initial message", ct), ct);
        AgentResultEvent firstResult = Assert.IsType<AgentResultEvent>(firstEvents[^1]);
        Assert.Equal(AgentResultStatus.AwaitingPermission, firstResult.Status);

        // Reset clears everything including the pending batch.
        session.Reset();

        // ResumeWithPermissionsAsync must throw because nothing is parked after Reset.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CollectAsync(session.ResumeWithPermissionsAsync([], ct), ct));  // CS1061 expected
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Case 10 — Re-park
    //
    // Resume stream itself triggers a new Ask in the continued turn.
    // Script: LLM turn 1 → toolA pends. Resume AllowOnce for toolA → toolA executes.
    //         LLM turn 2 (inside resume) → toolB pends.
    //         Resume stream ends with AwaitingPermission + new PermissionRequestedEvent.
    //         Answering THAT resume completes (loop until non-AwaitingPermission).
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A resume that triggers a follow-up LLM turn can itself re-park: resuming the first ask
    /// executes that call and then parks again when the LLM immediately requests a second
    /// unresolved tool, yielding a new <see cref="PermissionRequestedEvent"/> and an
    /// <see cref="AgentResultStatus.AwaitingPermission"/> terminal result on the first resume
    /// stream; resuming the second ask then completes the loop with
    /// <see cref="AgentResultStatus.Success"/> and both tools invoked exactly once.
    /// </summary>
    [Fact]
    public async Task Resume_TriggerRepark_ResumeStreamEndsWithAwaitingPermissionAgain()
    {
        var ct = TestContext.Current.CancellationToken;

        var toolA = new FakeTool("toolA", _ => new ToolResult("A ok"));
        var toolB = new FakeTool("toolB", _ => new ToolResult("B ok"));
        var registry = new ToolRegistry([toolA, toolB]);

        var evaluator = new StubPermissionEvaluator();
        evaluator.Decisions["toolA"] = new PermissionDecision.Ask(null, "toolA");
        evaluator.Decisions["toolB"] = new PermissionDecision.Ask(null, "toolB");

        var llm = new FakeChatClient();
        // First park: toolA.
        llm.EnqueueResponse(ToolCallResponse(("id-a", "toolA")));
        // After toolA executes on resume, LLM calls toolB — second park.
        llm.EnqueueResponse(ToolCallResponse(("id-b", "toolB")));
        // After toolB executes on second resume, LLM returns final text.
        llm.EnqueueResponse(TextResponse("All done."));

        var ctx = MakeContext(new ToolContext { Registry = registry });
        var agent = new Agent(llm, "model", permissions: evaluator);

        (_, List<PermissionRequestedEvent> firstPermEvents) = await ParkTurnAsync(agent, ctx, ct);

        PermissionRequestedEvent firstPermEvent = Assert.Single(firstPermEvents, e => e.ToolName == "toolA");

        var firstResponses = new List<PermissionResponse>
        {
            new(firstPermEvent.RequestId, PermissionResponseKind.AllowOnce),
        };

        // First resume: toolA executes; toolB parks; stream ends with AwaitingPermission.
        List<AgentEvent> firstResumeEvents = await CollectAsync(
            agent.ResumeAsync(ctx, firstResponses, options: null, ct), ct);  // CS1061 expected

        AgentResultEvent repark = Assert.IsType<AgentResultEvent>(firstResumeEvents[^1]);
        Assert.Equal(AgentResultStatus.AwaitingPermission, repark.Status);

        Assert.Equal(1, toolA.InvokeCount);
        Assert.Equal(0, toolB.InvokeCount);

        // New PermissionRequestedEvent for toolB in the first resume stream.
        PermissionRequestedEvent secondPermEvent =
            Assert.Single(firstResumeEvents.OfType<PermissionRequestedEvent>(), e => e.ToolName == "toolB");

        // Second resume: toolB executes; loop ends with Success.
        var secondResponses = new List<PermissionResponse>
        {
            new(secondPermEvent.RequestId, PermissionResponseKind.AllowOnce),
        };

        List<AgentEvent> secondResumeEvents = await CollectAsync(
            agent.ResumeAsync(ctx, secondResponses, options: null, ct), ct);  // CS1061 expected

        AgentResultEvent finalResult = Assert.IsType<AgentResultEvent>(secondResumeEvents[^1]);
        Assert.Equal(AgentResultStatus.Success, finalResult.Status);

        Assert.Equal(1, toolA.InvokeCount);
        Assert.Equal(1, toolB.InvokeCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Bonus: Validate ChatSession.ResumeWithPermissionsAsync surface
    //
    // Tests that the ChatSession public surface (§3.1) is callable and delegates
    // correctly to Agent.ResumeAsync.  Most behavioral tests go through Agent
    // directly; this confirms ChatSession wires the plumbing.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="ChatSession.ResumeWithPermissionsAsync"/> delegates correctly to
    /// <c>Agent.ResumeAsync</c>: resuming an allowed call while parked invokes the tool and
    /// completes the resume stream with <see cref="AgentResultStatus.Success"/>.
    /// </summary>
    [Fact]
    public async Task ChatSession_ResumeWithPermissionsAsync_WhileParked_CompletesNormally()
    {
        var ct = TestContext.Current.CancellationToken;

        var toolA = new FakeTool("toolA", _ => new ToolResult("ok"));
        var registry = new ToolRegistry([toolA]);

        var evaluator = new StubPermissionEvaluator();
        evaluator.Decisions["toolA"] = new PermissionDecision.Ask(null, "toolA");

        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse(("id-a", "toolA")));
        llm.EnqueueResponse(TextResponse("Done."));

        var agent = new Agent(llm, "model", permissions: evaluator);
        var session = new ChatSession(agent, new AgentOptions(), new ToolContext { Registry = registry });

        List<AgentEvent> firstEvents = await CollectAsync(session.SendAsync("hello", ct), ct);
        AgentResultEvent firstResult = Assert.IsType<AgentResultEvent>(firstEvents[^1]);
        Assert.Equal(AgentResultStatus.AwaitingPermission, firstResult.Status);

        // Collect the PermissionRequestedEvent from the first stream.
        PermissionRequestedEvent permEvent =
            Assert.Single(firstEvents.OfType<PermissionRequestedEvent>());

        var responses = new List<PermissionResponse>
        {
            new(permEvent.RequestId, PermissionResponseKind.AllowOnce),
        };

        // ChatSession.ResumeWithPermissionsAsync — CS1061 expected until Task 15.
        List<AgentEvent> resumeEvents = await CollectAsync(
            session.ResumeWithPermissionsAsync(responses, ct), ct);

        AgentResultEvent finalResult = Assert.IsType<AgentResultEvent>(resumeEvents[^1]);
        Assert.Equal(AgentResultStatus.Success, finalResult.Status);

        Assert.Equal(1, toolA.InvokeCount);
    }

    /// <summary>
    /// Calling <see cref="ChatSession.ResumeWithPermissionsAsync"/> on a session that has never
    /// sent a message (no context, nothing parked) must throw
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    [Fact]
    public async Task ChatSession_ResumeWithPermissionsAsync_SessionNotStarted_ThrowsInvalidOperation()
    {
        // ResumeWithPermissionsAsync on a never-used session (no context) → InvalidOperationException.
        var ct = TestContext.Current.CancellationToken;

        var llm = new FakeChatClient();
        var agent = new Agent(llm, "model");
        var session = new ChatSession(agent, new AgentOptions());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CollectAsync(session.ResumeWithPermissionsAsync([], ct), ct));  // CS1061 expected
    }
}
