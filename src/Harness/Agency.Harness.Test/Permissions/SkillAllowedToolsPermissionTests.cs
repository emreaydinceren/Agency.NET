using System.Text.Json;
using Agency.Harness.Contexts;
using Agency.Harness.Permissions;
using Agency.Harness.Skills;
using Agency.Harness.Test.Fakes;

namespace Agency.Harness.Test.Permissions;

/// <summary>
/// Verifies the Task-9 <c>allowed-tools</c> permission pre-approval feature:
/// while a skill is "active" (its rendered body is the most-recent skill message),
/// the skill's <c>allowed-tools</c> are pre-approved in the agent loop's permission
/// gate; the active-skill state is cleared on the next user message.
/// </summary>
public sealed class SkillAllowedToolsPermissionTests
{
    // ── Shared helpers ─────────────────────────────────────────────────────────

    /// <summary>Builds a minimal context backed by the given tool registry and skill catalog.</summary>
    private static Context MakeContext(ToolRegistry registry, ISkillCatalog? catalog = null)
    {
        SkillContext skillCtx = catalog is not null
            ? new SkillContext { Catalog = catalog }
            : SkillContext.Empty;

        return new Context
        {
            Query = new QueryContext { Prompt = "test" },
            Tools = new ToolContext { Registry = registry },
            Skills = skillCtx,
        };
    }

    /// <summary>
    /// Builds a <see cref="Skill"/> with the given name and <c>allowed-tools</c> list.
    /// </summary>
    private static Skill MakeSkill(string name, params string[] allowedTools) =>
        new()
        {
            Name = name,
            Description = $"Test skill: {name}",
            Body = $"You are now using the {name} skill.",
            SkillDir = $"/skills/{name}",
            AllowedTools = allowedTools,
        };

    /// <summary>Builds a ChatResponse carrying a plain text assistant turn (no tool calls).</summary>
    private static ChatResponse TextResponse(string text = "Done.") =>
        new([new ChatMessage(ChatRole.Assistant, text)])
        {
            Usage = new UsageDetails { InputTokenCount = 5, OutputTokenCount = 3 },
            FinishReason = ChatFinishReason.Stop,
        };

    /// <summary>Builds a ChatResponse that calls a list of tools (all with empty args).</summary>
    private static ChatResponse ToolCallResponse(params (string id, string name)[] calls)
    {
        List<AIContent> contents = calls
            .Select(c => (AIContent)new FunctionCallContent(c.id, c.name))
            .ToList();
        return new ChatResponse([new ChatMessage(ChatRole.Assistant, contents)])
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            FinishReason = ChatFinishReason.ToolCalls,
        };
    }

    /// <summary>Builds a ChatResponse that calls the "skill" tool with a given skill name.</summary>
    private static ChatResponse SkillCallResponse(string callId, string skillName) =>
        new([new ChatMessage(ChatRole.Assistant, [
            new FunctionCallContent(callId, "skill", new Dictionary<string, object?> { ["name"] = skillName })
        ])])
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            FinishReason = ChatFinishReason.ToolCalls,
        };

    /// <summary>Drains the full agent event stream into a list.</summary>
    private static async Task<List<AgentEvent>> RunToCompletion(
        Agent agent, Context ctx, CancellationToken ct = default)
    {
        List<AgentEvent> events = [];
        await foreach (AgentEvent evt in agent.RunAsync(ctx, ct))
        {
            events.Add(evt);
        }

        return events;
    }

    // ── Group 1: Active skill pre-approves listed tools ────────────────────────

    /// <summary>
    /// When the agent invokes a skill, then calls a tool listed in <c>allowed-tools</c>,
    /// the evaluator would return <see cref="PermissionDecision.Ask"/> but the tool is
    /// pre-approved by the active-skill state and executes without parking.
    /// </summary>
    [Fact]
    public async Task ActiveSkill_ToolInAllowedList_ExecutesWithoutParking()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Skill declares "ReadFile" as an allowed tool.
        Skill skill = MakeSkill("my-skill", "ReadFile");
        SkillCatalog catalog = new([skill]);

        // ReadFile is the target tool; it must execute (invoke count == 1).
        FakeTool readFile = new("ReadFile", _ => new ToolResult("file contents"));
        SkillTool skillTool = new(catalog);

        ToolRegistry registry = new([skillTool, readFile]);
        Context ctx = MakeContext(registry, catalog);

        // Evaluator always returns Ask for ReadFile — verifies pre-approval overrides it.
        StubPermissionEvaluator evaluator = new();
        evaluator.Decisions["ReadFile"] = new PermissionDecision.Ask(null, "ReadFile");

        FakeChatClient llm = new();
        // Turn 1: invoke "skill" with "my-skill".
        llm.EnqueueResponse(SkillCallResponse("call-1", "my-skill"));
        // Turn 2: now call ReadFile (should be pre-approved).
        llm.EnqueueResponse(ToolCallResponse(("call-2", "ReadFile")));
        // Turn 3: text reply to finish.
        llm.EnqueueResponse(TextResponse("All done."));

        Agent agent = new(llm, "model", permissions: evaluator);
        List<AgentEvent> events = await RunToCompletion(agent, ctx, ct);

        // Must finish with Success (not AwaitingPermission).
        AgentResultEvent result = Assert.IsType<AgentResultEvent>(events[^1]);
        Assert.Equal(AgentResultStatus.Success, result.Status);

        // ReadFile must have been invoked (not parked).
        Assert.Equal(1, readFile.InvokeCount);
    }

    // ── Group 2: Tool NOT in allowed list still requires ask/deny ─────────────

    /// <summary>
    /// When the agent invokes a skill, then calls a tool that is NOT in <c>allowed-tools</c>,
    /// the evaluator's Ask decision is respected and the turn is parked.
    /// </summary>
    [Fact]
    public async Task ActiveSkill_ToolNotInAllowedList_StillParks()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Skill only declares "ReadFile" — WriteFile is NOT in the list.
        Skill skill = MakeSkill("my-skill", "ReadFile");
        SkillCatalog catalog = new([skill]);

        FakeTool writeFile = new("WriteFile", _ => new ToolResult("written"));
        SkillTool skillTool = new(catalog);

        ToolRegistry registry = new([skillTool, writeFile]);
        Context ctx = MakeContext(registry, catalog);

        StubPermissionEvaluator evaluator = new();
        evaluator.Decisions["WriteFile"] = new PermissionDecision.Ask(null, "WriteFile");

        FakeChatClient llm = new();
        llm.EnqueueResponse(SkillCallResponse("call-1", "my-skill"));
        // After skill, try to call WriteFile (NOT in allowed-tools → must park).
        llm.EnqueueResponse(ToolCallResponse(("call-2", "WriteFile")));

        Agent agent = new(llm, "model", permissions: evaluator);
        List<AgentEvent> events = await RunToCompletion(agent, ctx, ct);

        // Must park, not succeed.
        AgentResultEvent result = Assert.IsType<AgentResultEvent>(events[^1]);
        Assert.Equal(AgentResultStatus.AwaitingPermission, result.Status);

        // WriteFile must NOT have been invoked.
        Assert.Equal(0, writeFile.InvokeCount);
    }

    // ── Group 3: Active state is cleared on next user message ─────────────────

    /// <summary>
    /// After the active skill's turn ends and the next user message is sent via
    /// <see cref="ChatSession.SendAsync"/>, the active-skill state is cleared: the same
    /// tool that was pre-approved in the previous turn is no longer pre-approved.
    /// </summary>
    [Fact]
    public async Task ActiveSkillState_ClearedOnNextUserMessage_ToolNoLongerPreApproved()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        Skill skill = MakeSkill("my-skill", "ReadFile");
        SkillCatalog catalog = new([skill]);

        FakeTool readFile = new("ReadFile", _ => new ToolResult("file contents"));
        SkillTool skillTool = new(catalog);

        ToolRegistry registry = new([skillTool, readFile]);
        Context ctx = MakeContext(registry, catalog);

        StubPermissionEvaluator evaluator = new();
        // Evaluator always returns Ask for ReadFile.
        evaluator.Decisions["ReadFile"] = new PermissionDecision.Ask(null, "ReadFile");

        FakeChatClient llm = new();
        // Turn 1 (user message 1): invoke skill then ReadFile (pre-approved → executes).
        llm.EnqueueResponse(SkillCallResponse("call-1", "my-skill"));
        llm.EnqueueResponse(ToolCallResponse(("call-2", "ReadFile")));
        llm.EnqueueResponse(TextResponse("Done turn 1."));

        Agent agent = new(llm, "model", permissions: evaluator);
        AgentOptions options = new();
        ChatSession session = new(agent, options, new ToolContext { Registry = registry }, skills: new SkillContext { Catalog = catalog });

        // First user turn — skill is invoked, ReadFile is pre-approved.
        List<AgentEvent> turn1Events = [];
        await foreach (AgentEvent evt in session.SendAsync("do the skill", ct))
        {
            turn1Events.Add(evt);
        }

        AgentResultEvent turn1Result = Assert.IsType<AgentResultEvent>(turn1Events[^1]);
        Assert.Equal(AgentResultStatus.Success, turn1Result.Status);
        Assert.Equal(1, readFile.InvokeCount);

        // Second user turn: now call ReadFile directly (no skill invoked this turn).
        // The active state should be cleared, so ReadFile parks again.
        llm.EnqueueResponse(ToolCallResponse(("call-3", "ReadFile")));

        List<AgentEvent> turn2Events = [];
        await foreach (AgentEvent evt in session.SendAsync("just read the file", ct))
        {
            turn2Events.Add(evt);
        }

        AgentResultEvent turn2Result = Assert.IsType<AgentResultEvent>(turn2Events[^1]);
        // Must park, not succeed — the active skill is cleared.
        Assert.Equal(AgentResultStatus.AwaitingPermission, turn2Result.Status);
        // ReadFile was NOT invoked a second time (still at 1).
        Assert.Equal(1, readFile.InvokeCount);
    }

    // ── Group 4: Deny rule still beats active-skill pre-approval ──────────────

    /// <summary>
    /// A hard deny rule from the evaluator blocks a tool even if the active skill
    /// declares it in <c>allowed-tools</c>. Deny always wins.
    /// </summary>
    [Fact]
    public async Task ActiveSkill_DenyRuleBeatsPreApproval_ToolIsBlocked()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Skill declares "DangerousTool" as an allowed tool.
        Skill skill = MakeSkill("risky-skill", "DangerousTool");
        SkillCatalog catalog = new([skill]);

        FakeTool dangerous = new("DangerousTool", _ => new ToolResult("dangerous output"));
        SkillTool skillTool = new(catalog);

        ToolRegistry registry = new([skillTool, dangerous]);
        Context ctx = MakeContext(registry, catalog);

        // Evaluator returns a hard Deny for DangerousTool.
        StubPermissionEvaluator evaluator = new();
        evaluator.Decisions["DangerousTool"] = new PermissionDecision.Deny("Config deny rule fires.");

        FakeChatClient llm = new();
        llm.EnqueueResponse(SkillCallResponse("call-1", "risky-skill"));
        llm.EnqueueResponse(ToolCallResponse(("call-2", "DangerousTool")));
        llm.EnqueueResponse(TextResponse("Done."));

        Agent agent = new(llm, "model", permissions: evaluator);
        List<AgentEvent> events = await RunToCompletion(agent, ctx, ct);

        // Loop must finish (deny is non-parking).
        AgentResultEvent result = Assert.IsType<AgentResultEvent>(events[^1]);
        Assert.Equal(AgentResultStatus.Success, result.Status);

        // DangerousTool must NOT have been invoked — deny blocked it.
        Assert.Equal(0, dangerous.InvokeCount);

        // Verify the tool result is a [Blocked] message (via conversation — check ToolInvokedEvent).
        ToolInvokedEvent? toolEvt = events.OfType<ToolInvokedEvent>()
            .FirstOrDefault(e => e.ToolName == "DangerousTool");
        Assert.NotNull(toolEvt);
        Assert.True(toolEvt.Result.IsError);
        Assert.Contains("[Blocked]", toolEvt.Result.Content);
    }

    // ── Group 5: No active skill → evaluator Ask still parks ──────────────────

    /// <summary>
    /// When no skill is active, the standard permission evaluation applies:
    /// an Ask from the evaluator still parks the turn.
    /// </summary>
    [Fact]
    public async Task NoActiveSkill_EvaluatorAsk_ParksNormally()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        FakeTool readFile = new("ReadFile", _ => new ToolResult("file contents"));
        ToolRegistry registry = new([readFile]);
        Context ctx = MakeContext(registry);

        StubPermissionEvaluator evaluator = new();
        evaluator.Decisions["ReadFile"] = new PermissionDecision.Ask(null, "ReadFile");

        FakeChatClient llm = new();
        llm.EnqueueResponse(ToolCallResponse(("call-1", "ReadFile")));

        Agent agent = new(llm, "model", permissions: evaluator);
        List<AgentEvent> events = await RunToCompletion(agent, ctx, ct);

        AgentResultEvent result = Assert.IsType<AgentResultEvent>(events[^1]);
        Assert.Equal(AgentResultStatus.AwaitingPermission, result.Status);
        Assert.Equal(0, readFile.InvokeCount);
    }

    // ── Group 6: Skill with empty allowed-tools has no effect ─────────────────

    /// <summary>
    /// When a skill declares no <c>allowed-tools</c>, invoking it does not pre-approve
    /// any tools; the evaluator's Ask decision still parks the turn.
    /// </summary>
    [Fact]
    public async Task ActiveSkill_EmptyAllowedTools_NoPpreApprovalEffect()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Skill with empty allowed-tools.
        Skill skill = MakeSkill("plain-skill"); // no allowed tools
        SkillCatalog catalog = new([skill]);

        FakeTool readFile = new("ReadFile", _ => new ToolResult("file contents"));
        SkillTool skillTool = new(catalog);

        ToolRegistry registry = new([skillTool, readFile]);
        Context ctx = MakeContext(registry, catalog);

        StubPermissionEvaluator evaluator = new();
        evaluator.Decisions["ReadFile"] = new PermissionDecision.Ask(null, "ReadFile");

        FakeChatClient llm = new();
        llm.EnqueueResponse(SkillCallResponse("call-1", "plain-skill"));
        llm.EnqueueResponse(ToolCallResponse(("call-2", "ReadFile")));

        Agent agent = new(llm, "model", permissions: evaluator);
        List<AgentEvent> events = await RunToCompletion(agent, ctx, ct);

        AgentResultEvent result = Assert.IsType<AgentResultEvent>(events[^1]);
        Assert.Equal(AgentResultStatus.AwaitingPermission, result.Status);
        Assert.Equal(0, readFile.InvokeCount);
    }
}
