using System.Text.Json;
using Agency.Harness.Contexts;
using Agency.Harness.Hooks;
using Agency.Harness.Permissions;
using Agency.Harness.Test.Fakes;

namespace Agency.Harness.Test.Permissions;

// ─────────────────────────────────────────────────────────────────────────────
// Stub evaluator — configurable per-tool decisions, records Evaluate calls.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Configurable test double for <see cref="IPermissionEvaluator"/>.
/// Supply per-tool decisions via the <see cref="Decisions"/> dictionary;
/// missing entries default to <see cref="PermissionDecision.Allowed"/>.
/// </summary>
internal sealed class StubPermissionEvaluator : IPermissionEvaluator
{
    /// <summary>Maps tool name → decision returned by <see cref="Evaluate"/>.</summary>
    public Dictionary<string, PermissionDecision> Decisions { get; } = new(StringComparer.Ordinal);

    /// <summary>Records each (toolName, input) pair passed to <see cref="Evaluate"/>, in call order.</summary>
    public List<(string ToolName, JsonElement Input)> EvaluateCalls { get; } = [];

    /// <summary>Records each (proposedRule, deny) pair passed to <see cref="RecordAlwaysAsync"/>.</summary>
    public List<(string ProposedRule, bool Deny)> RecordAlwaysCalls { get; } = [];

    /// <inheritdoc/>
    public PermissionDecision Evaluate(string toolName, JsonElement input)
    {
        this.EvaluateCalls.Add((toolName, input));
        return this.Decisions.TryGetValue(toolName, out PermissionDecision? decision)
            ? decision
            : PermissionDecision.Allowed;
    }

    /// <inheritdoc/>
    public Task RecordAlwaysAsync(string proposedRule, bool deny, CancellationToken ct)
    {
        this.RecordAlwaysCalls.Add((proposedRule, deny));
        return Task.CompletedTask;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Test helpers shared across all tests in this file
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// TDD RED tests for the Agent mid-batch PARK path (§6.1, §12).
/// These tests reference the new <c>IPermissionEvaluator? permissions</c> Agent constructor
/// parameter (Task 13 implements it) and <c>Context.PendingToolBatch</c> (also Task 13).
/// Expected build/runtime state: compile errors on the new ctor parameter references,
/// or runtime failures on tests that exercise today's fail-closed hook-Ask behavior.
/// </summary>
public sealed class AgentPermissionParkTests
{
    // ── Shared helpers ─────────────────────────────────────────────────────────

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

    /// <summary>
    /// Builds a ChatResponse with the given tool calls, each with empty arguments.
    /// </summary>
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

    /// <summary>
    /// Builds a ChatResponse with a single tool call carrying a named argument.
    /// </summary>
    private static ChatResponse ToolCallWithArgResponse(
        string callId,
        string toolName,
        string argName,
        string argValue)
    {
        var args = new Dictionary<string, object?> { [argName] = argValue };
        var contents = new List<AIContent> { new FunctionCallContent(callId, toolName, args) };
        return new ChatResponse([new ChatMessage(ChatRole.Assistant, contents)])
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            FinishReason = ChatFinishReason.ToolCalls,
        };
    }

    private static async Task<List<AgentEvent>> RunToCompletion(
        Agent agent, Context ctx, CancellationToken ct = default)
    {
        var events = new List<AgentEvent>();
        await foreach (AgentEvent evt in agent.RunAsync(ctx, ct))
        {
            events.Add(evt);
        }

        return events;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 1 — Park: 3 tool calls, 1 Allow, 1 Deny, 1 Ask
    //
    // Spec §6.1 / §12 "Park" row.
    // Expected events: 2 ToolInvokedEvents (toolA allowed, toolB denied),
    //   1 PermissionRequestedEvent (toolC), terminal AgentResultEvent(AwaitingPermission).
    // No Tool-role messages appended; OnPostToolBatch NOT fired; toolC never invoked.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Park_OneBatchWith3Calls_AllowDenyAsk_YieldsCorrectEvents()
    {
        var ct = TestContext.Current.CancellationToken;

        var toolA = new FakeTool("toolA", _ => new ToolResult("A ok"));
        var toolB = new FakeTool("toolB", _ => new ToolResult("B ok"));
        var toolC = new FakeTool("toolC", _ => new ToolResult("C ok"));
        var registry = new ToolRegistry([toolA, toolB, toolC]);

        var evaluator = new StubPermissionEvaluator();
        evaluator.Decisions["toolA"] = PermissionDecision.Allowed;
        evaluator.Decisions["toolB"] = new PermissionDecision.Deny("Permission rule 'X' denies this call.");
        evaluator.Decisions["toolC"] = new PermissionDecision.Ask(null, "toolC");

        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse(("id-a", "toolA"), ("id-b", "toolB"), ("id-c", "toolC")));
        // No second response queued — the turn must park before calling the LLM again.

        var ctx = MakeContext(new ToolContext { Registry = registry });

        // Task 13 adds the permissions parameter after hooks.
        var agent = new Agent(llm, "model", permissions: evaluator);

        List<AgentEvent> events = await RunToCompletion(agent, ctx, ct);

        // ── Event count and terminal status ───────────────────────────────────
        var result = Assert.IsType<AgentResultEvent>(events[^1]);
        Assert.Equal(AgentResultStatus.AwaitingPermission, result.Status);

        // ── Exactly 2 ToolInvokedEvents (toolA + toolB, NOT toolC) ───────────
        var toolEvents = events.OfType<ToolInvokedEvent>().ToList();
        Assert.Equal(2, toolEvents.Count);
        Assert.Contains(toolEvents, e => e.ToolName == "toolA");
        Assert.Contains(toolEvents, e => e.ToolName == "toolB");
        Assert.DoesNotContain(toolEvents, e => e.ToolName == "toolC");

        // ── toolB's event carries a [Blocked] result ──────────────────────────
        ToolInvokedEvent toolBEvent = toolEvents.Single(e => e.ToolName == "toolB");
        Assert.True(toolBEvent.Result.IsError);
        Assert.Contains("[Blocked]", toolBEvent.Result.Content, StringComparison.Ordinal);

        // ── Exactly 1 PermissionRequestedEvent for toolC ─────────────────────
        var permEvents = events.OfType<PermissionRequestedEvent>().ToList();
        Assert.Single(permEvents);
        PermissionRequestedEvent permEvent = permEvents[0];
        Assert.Equal("toolC", permEvent.ToolName);
        Assert.Equal(PermissionRequestSource.UnresolvedRule, permEvent.Source);
        Assert.NotEqual(Guid.Empty, permEvent.RequestId);

        // ── toolC was NEVER invoked ────────────────────────────────────────────
        Assert.Equal(0, toolC.InvokeCount);

        // ── No Tool-role messages appended to conversation ────────────────────
        Assert.DoesNotContain(ctx.Conversation.Messages, m => m.Role == ChatRole.Tool);
    }

    [Fact]
    public async Task Park_OnPostToolBatch_NotFired_WhenBatchParks()
    {
        var ct = TestContext.Current.CancellationToken;

        var toolA = new FakeTool("toolA", _ => new ToolResult("ok"));
        var toolC = new FakeTool("toolC", _ => new ToolResult("ok"));
        var registry = new ToolRegistry([toolA, toolC]);

        var evaluator = new StubPermissionEvaluator();
        evaluator.Decisions["toolA"] = PermissionDecision.Allowed;
        evaluator.Decisions["toolC"] = new PermissionDecision.Ask(null, "toolC");

        int postBatchCount = 0;
        var hooks = new AgentHooks
        {
            OnPostToolBatch = (_, _, _) => { postBatchCount++; return Task.CompletedTask; },
        };

        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse(("id-a", "toolA"), ("id-c", "toolC")));

        var ctx = MakeContext(new ToolContext { Registry = registry });

        // Task 13 adds the permissions parameter after hooks.
        var agent = new Agent(llm, "model", hooks: hooks, permissions: evaluator);

        await RunToCompletion(agent, ctx, ct);

        Assert.Equal(0, postBatchCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 2 — Multiple asks: 2 unresolved calls → 2 PermissionRequestedEvents
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Park_TwoUnresolvedCalls_YieldsTwoPermissionRequestedEventsWithDistinctIds()
    {
        var ct = TestContext.Current.CancellationToken;

        var toolA = new FakeTool("toolA", _ => new ToolResult("ok"));
        var toolB = new FakeTool("toolB", _ => new ToolResult("ok"));
        var registry = new ToolRegistry([toolA, toolB]);

        var evaluator = new StubPermissionEvaluator();
        evaluator.Decisions["toolA"] = new PermissionDecision.Ask("keyA", "toolA(keyA)");
        evaluator.Decisions["toolB"] = new PermissionDecision.Ask("keyB", "toolB(keyB)");

        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse(("id-a", "toolA"), ("id-b", "toolB")));

        var ctx = MakeContext(new ToolContext { Registry = registry });

        var agent = new Agent(llm, "model", permissions: evaluator);

        List<AgentEvent> events = await RunToCompletion(agent, ctx, ct);

        // Two PermissionRequestedEvents before the terminal AwaitingPermission.
        var permEvents = events.OfType<PermissionRequestedEvent>().ToList();
        Assert.Equal(2, permEvents.Count);

        // Distinct, non-empty RequestIds.
        Assert.NotEqual(permEvents[0].RequestId, permEvents[1].RequestId);
        Assert.NotEqual(Guid.Empty, permEvents[0].RequestId);
        Assert.NotEqual(Guid.Empty, permEvents[1].RequestId);

        // Terminal AwaitingPermission.
        var result = Assert.IsType<AgentResultEvent>(events[^1]);
        Assert.Equal(AgentResultStatus.AwaitingPermission, result.Status);

        // Neither tool was invoked.
        Assert.Equal(0, toolA.InvokeCount);
        Assert.Equal(0, toolB.InvokeCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 3 — Hook Ask + Allow rule: call pends with Source=Hook
    //
    // Spec §2.3 step 3: a hook Ask flags the call for user confirmation,
    // even when an allow rule matches. Allow rules cannot clear a hook Ask.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Park_HookAsk_WithAllowRule_CallStillPends_SourceIsHook()
    {
        var ct = TestContext.Current.CancellationToken;

        var toolA = new FakeTool("toolA", _ => new ToolResult("ok"));
        var registry = new ToolRegistry([toolA]);

        // Evaluator returns Allow for toolA — but the hook returns Ask, which must win.
        var evaluator = new StubPermissionEvaluator();
        evaluator.Decisions["toolA"] = PermissionDecision.Allowed;

        var hooks = new AgentHooks
        {
            OnPreToolUse = (ctx, _) =>
                Task.FromResult<PreToolUseDecision>(new PreToolUseDecision.Ask("flagged")),
        };

        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse(("id-a", "toolA")));

        var ctx = MakeContext(new ToolContext { Registry = registry });

        var agent = new Agent(llm, "model", hooks: hooks, permissions: evaluator);

        List<AgentEvent> events = await RunToCompletion(agent, ctx, ct);

        // The call must have parked.
        var result = Assert.IsType<AgentResultEvent>(events[^1]);
        Assert.Equal(AgentResultStatus.AwaitingPermission, result.Status);

        // Exactly one PermissionRequestedEvent with Source=Hook and Reason="flagged".
        PermissionRequestedEvent permEvent = Assert.Single(events.OfType<PermissionRequestedEvent>());
        Assert.Equal("toolA", permEvent.ToolName);
        Assert.Equal(PermissionRequestSource.Hook, permEvent.Source);
        Assert.Equal("flagged", permEvent.Reason);

        // Tool was NOT invoked.
        Assert.Equal(0, toolA.InvokeCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 4 — Hook Ask + Deny rule: denied WITHOUT parking
    //
    // Spec §2.3 step 2: a deny rule still denies a hook-Ask-flagged call
    // (deny always wins). Turn completes normally — no AwaitingPermission.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Park_HookAsk_WithDenyRule_DeniedWithoutParking()
    {
        var ct = TestContext.Current.CancellationToken;

        var toolA = new FakeTool("toolA", _ => new ToolResult("ok"));
        var registry = new ToolRegistry([toolA]);

        // Evaluator returns Deny for toolA.
        var evaluator = new StubPermissionEvaluator();
        evaluator.Decisions["toolA"] = new PermissionDecision.Deny("Permission rule 'X' denies this call.");

        var hooks = new AgentHooks
        {
            OnPreToolUse = (_, _) =>
                Task.FromResult<PreToolUseDecision>(new PreToolUseDecision.Ask("flagged")),
        };

        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse(("id-a", "toolA")));
        // Turn continues after denial — LLM needs a follow-up response.
        llm.EnqueueResponse(TextResponse("Understood."));

        var ctx = MakeContext(new ToolContext { Registry = registry });

        var agent = new Agent(llm, "model", hooks: hooks, permissions: evaluator);

        List<AgentEvent> events = await RunToCompletion(agent, ctx, ct);

        // NO AwaitingPermission — the turn completed normally.
        var result = Assert.IsType<AgentResultEvent>(events[^1]);
        Assert.NotEqual(AgentResultStatus.AwaitingPermission, result.Status);

        // No PermissionRequestedEvent — deny short-circuits before park.
        Assert.Empty(events.OfType<PermissionRequestedEvent>());

        // ToolInvokedEvent exists but has a [Blocked] result.
        ToolInvokedEvent toolEvent = Assert.Single(events.OfType<ToolInvokedEvent>());
        Assert.Equal("toolA", toolEvent.ToolName);
        Assert.True(toolEvent.Result.IsError);
        Assert.Contains("[Blocked]", toolEvent.Result.Content, StringComparison.Ordinal);

        // Tool was NOT invoked.
        Assert.Equal(0, toolA.InvokeCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 5 — Hook Ask + OnUnresolved=Deny semantics (headless)
    //
    // Spec §2.3 step 3 + §2.5 row 5:
    //   When OnUnresolved=Deny, a hook-Ask call fails closed instead of parking.
    //   Deny reason: "A hook required user confirmation, but this session cannot ask: {hookReason}"
    //   Observable: no AwaitingPermission, [Blocked] result containing the hook reason.
    //
    // Implementation assumption pinned for Task 13:
    //   The stub evaluator can model this by returning Deny with the §2.5 reason string
    //   when given a hook-Asked call name — the evaluator is configured with OnUnresolved=Deny
    //   (via the deny decision in the stub for the hook-asked tool name).
    //   Alternatively, Task 13 may integrate OnUnresolved=Deny directly into the agent's
    //   hook-Ask handling path.  Either way the OBSERVABLE contract is identical:
    //   no park, no PermissionRequestedEvent, [Blocked] result whose content contains
    //   the hook reason substring.
    //
    // NOTE: The test pins observable output only; mechanism freedom is preserved for Task 13.
    //   If Task 13 routes this through the evaluator's OnUnresolved=Deny path, the stub must
    //   return Deny with the §2.5 string.  If the agent handles it directly (recognising hook-Ask
    //   + evaluator.OnUnresolved == Deny), the stub approach below works as-is.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Park_HookAsk_OnUnresolvedDeny_FailsClosedWithHookReason()
    {
        var ct = TestContext.Current.CancellationToken;

        const string hookReason = "sensitive path detected";
        const string expectedDenyReason =
            $"A hook required user confirmation, but this session cannot ask: {hookReason}";

        var toolA = new FakeTool("toolA", _ => new ToolResult("ok"));
        var registry = new ToolRegistry([toolA]);

        // Stub evaluator configured to return the headless-deny reason for toolA.
        // This models what the system does when OnUnresolved=Deny is set and a hook-Asked
        // call arrives.  Task 13 may derive this from PermissionsOptions.OnUnresolved
        // rather than having the evaluator return this specific string, in which case the
        // test fixture may need adjustment — see the method comment above.
        var evaluator = new StubPermissionEvaluator();
        evaluator.Decisions["toolA"] = new PermissionDecision.Deny(expectedDenyReason);

        var hooks = new AgentHooks
        {
            OnPreToolUse = (_, _) =>
                Task.FromResult<PreToolUseDecision>(new PreToolUseDecision.Ask(hookReason)),
        };

        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse(("id-a", "toolA")));
        llm.EnqueueResponse(TextResponse("Understood."));

        var ctx = MakeContext(new ToolContext { Registry = registry });

        var agent = new Agent(llm, "model", hooks: hooks, permissions: evaluator);

        List<AgentEvent> events = await RunToCompletion(agent, ctx, ct);

        // No park — turn completed normally.
        var result = Assert.IsType<AgentResultEvent>(events[^1]);
        Assert.NotEqual(AgentResultStatus.AwaitingPermission, result.Status);

        // No PermissionRequestedEvent.
        Assert.Empty(events.OfType<PermissionRequestedEvent>());

        // [Blocked] result containing the hook reason.
        ToolInvokedEvent toolEvent = Assert.Single(events.OfType<ToolInvokedEvent>());
        Assert.True(toolEvent.Result.IsError);
        Assert.Contains("[Blocked]", toolEvent.Result.Content, StringComparison.Ordinal);
        Assert.Contains(hookReason, toolEvent.Result.Content, StringComparison.Ordinal);

        // Tool was NOT invoked.
        Assert.Equal(0, toolA.InvokeCount);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 6 — No evaluator: regression guard
    //
    // Spec §2.4: "When null the rules layer is absent and behavior is unchanged."
    // Agent constructed WITHOUT permissions param → existing behavior byte-for-byte.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoEvaluator_AllToolsInvoke_NormalCompletion()
    {
        var ct = TestContext.Current.CancellationToken;

        var toolA = new FakeTool("toolA", _ => new ToolResult("A"));
        var toolB = new FakeTool("toolB", _ => new ToolResult("B"));
        var registry = new ToolRegistry([toolA, toolB]);

        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse(("id-a", "toolA"), ("id-b", "toolB")));
        llm.EnqueueResponse(TextResponse("Done."));

        var ctx = MakeContext(new ToolContext { Registry = registry });

        // Existing ctor shape — no permissions param.
        var agent = new Agent(llm, "model");

        List<AgentEvent> events = await RunToCompletion(agent, ctx, ct);

        // Both tools invoked once.
        Assert.Equal(1, toolA.InvokeCount);
        Assert.Equal(1, toolB.InvokeCount);

        // Terminal success.
        var result = Assert.IsType<AgentResultEvent>(events[^1]);
        Assert.Equal(AgentResultStatus.Success, result.Status);

        // Two ToolInvokedEvents.
        Assert.Equal(2, events.OfType<ToolInvokedEvent>().Count());

        // No permission events.
        Assert.Empty(events.OfType<PermissionRequestedEvent>());

        // Two Tool-role messages appended.
        Assert.Equal(2, ctx.Conversation.Messages.Count(m => m.Role == ChatRole.Tool));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 7 — No evaluator + hook Ask: parks with Source=Hook
    //
    // Spec §3.5 "Without an evaluator": hook asks pend even without an evaluator.
    // KeyValue=null, ProposedRule=bare tool name, AwaitingPermission terminal.
    //
    // This REPLACES Task 11's interim fail-closed behavior — this test pins the
    // FINAL contract and is currently RED because today hook-Ask fail-closes.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoEvaluator_HookAsk_ParksWithSourceHook()
    {
        var ct = TestContext.Current.CancellationToken;

        var toolA = new FakeTool("toolA", _ => new ToolResult("ok"));
        var registry = new ToolRegistry([toolA]);

        var hooks = new AgentHooks
        {
            OnPreToolUse = (_, _) =>
                Task.FromResult<PreToolUseDecision>(new PreToolUseDecision.Ask("flagged")),
        };

        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse(("id-a", "toolA")));

        var ctx = MakeContext(new ToolContext { Registry = registry });

        // No permissions param — park/resume is Agent machinery, not evaluator machinery.
        var agent = new Agent(llm, "model", hooks: hooks);

        List<AgentEvent> events = await RunToCompletion(agent, ctx, ct);

        // Turn parks.
        var result = Assert.IsType<AgentResultEvent>(events[^1]);
        Assert.Equal(AgentResultStatus.AwaitingPermission, result.Status);

        // PermissionRequestedEvent with Source=Hook, Reason="flagged",
        // KeyValue=null (no evaluator), ProposedRule=bare tool name "toolA".
        PermissionRequestedEvent permEvent = Assert.Single(events.OfType<PermissionRequestedEvent>());
        Assert.Equal("toolA", permEvent.ToolName);
        Assert.Equal(PermissionRequestSource.Hook, permEvent.Source);
        Assert.Equal("flagged", permEvent.Reason);
        Assert.Null(permEvent.KeyValue);
        Assert.Equal("toolA", permEvent.ProposedRule);

        // Tool NOT invoked.
        Assert.Equal(0, toolA.InvokeCount);

        // No Tool-role messages appended.
        Assert.DoesNotContain(ctx.Conversation.Messages, m => m.Role == ChatRole.Tool);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 8 — Post-Rewrite capture
    //
    // Spec §2.4: the gate evaluates post-Rewrite input.
    // Hook returns Rewrite(newInput) for a tool the evaluator Asks on.
    // PermissionRequestedEvent.Input must equal the REWRITTEN input, not the original.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Park_HookRewrite_ThenEvaluatorAsk_PermissionRequestedEventHasRewrittenInput()
    {
        var ct = TestContext.Current.CancellationToken;

        var toolA = new FakeTool("toolA", _ => new ToolResult("ok"));
        var registry = new ToolRegistry([toolA]);

        var evaluator = new StubPermissionEvaluator();
        evaluator.Decisions["toolA"] = new PermissionDecision.Ask("rewritten-value", "toolA(rewritten-value)");

        // Original arg: "original-value"; hook rewrites to "rewritten-value"
        const string originalValue = "original-value";
        const string rewrittenValue = "rewritten-value";

        var rewrittenArgs = new Dictionary<string, object?> { ["command"] = rewrittenValue };
        JsonElement rewrittenElement = JsonSerializer.SerializeToElement(rewrittenArgs);

        var hooks = new AgentHooks
        {
            OnPreToolUse = (_, _) =>
                Task.FromResult<PreToolUseDecision>(new PreToolUseDecision.Rewrite(rewrittenElement)),
        };

        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallWithArgResponse("id-a", "toolA", "command", originalValue));

        var ctx = MakeContext(new ToolContext { Registry = registry });

        var agent = new Agent(llm, "model", hooks: hooks, permissions: evaluator);

        List<AgentEvent> events = await RunToCompletion(agent, ctx, ct);

        // Turn parks.
        var result = Assert.IsType<AgentResultEvent>(events[^1]);
        Assert.Equal(AgentResultStatus.AwaitingPermission, result.Status);

        // The PermissionRequestedEvent.Input must be the REWRITTEN input.
        PermissionRequestedEvent permEvent = Assert.Single(events.OfType<PermissionRequestedEvent>());
        Assert.Equal("toolA", permEvent.ToolName);
        Assert.True(
            permEvent.Input.TryGetProperty("command", out JsonElement cmdProp),
            "PermissionRequestedEvent.Input must have a 'command' property (the rewritten input).");
        Assert.Equal(rewrittenValue, cmdProp.GetString());

        // The original value must NOT be present on the event's input.
        Assert.NotEqual(originalValue, cmdProp.GetString());

        // Tool NOT invoked.
        Assert.Equal(0, toolA.InvokeCount);
    }
}
