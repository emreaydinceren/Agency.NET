using Agency.Acp.Permissions;
using Agency.Acp.Test.Fakes;
using Agency.Acp.Turns;
using Agency.Harness;
using Agency.Harness.Permissions;
using Agency.Harness.Tools;
using Agency.Llm.Common.Tools;
using Microsoft.Extensions.AI;

namespace Agency.Acp.Test.Permissions;

/// <summary>
/// Behavioral tests for <see cref="PersonaPermissionEvaluator"/> (spec §6.6): a denied tool is
/// recoverable — the model sees a <c>[Blocked]</c> tool result and the turn still reaches
/// <c>end_turn</c> — never a fatal, parked turn.
/// </summary>
public sealed class PersonaPermissionEvaluatorTests
{
    /// <summary>(a) A granted tool is allowed.</summary>
    [Fact]
    public void Evaluate_GrantedTool_Allows()
    {
        var evaluator = new PersonaPermissionEvaluator(["get_help"]);

        PermissionDecision decision = evaluator.Evaluate("get_help", default);

        Assert.IsType<PermissionDecision.Allow>(decision);
    }

    /// <summary>
    /// (b) Any tool outside the granted set is denied with a <c>[Blocked]</c> tool result
    /// (<see cref="ToolResult.IsError"/> true) rather than parking the turn, and the turn still
    /// reaches <see cref="dotacp.protocol.StopReason.EndTurn"/> — a denial is recoverable, a park is not.
    /// </summary>
    [Fact]
    public async Task Evaluate_UngrantedTool_YieldsBlockedResult_AndTurnStillReachesEndTurn()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        var evaluator = new PersonaPermissionEvaluator(["get_help"]);
        var ungrantedTool = new FakeTool("shell_exec", _ => new ToolResult("should never run"));
        var registry = new ToolRegistry([ungrantedTool]);

        var llm = new FakeChatClient();
        llm.EnqueueResponse(new ChatResponse([new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("id-1", "shell_exec")])])
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            FinishReason = ChatFinishReason.ToolCalls,
        });
        llm.EnqueueResponse(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Understood.")])
        {
            Usage = new UsageDetails { InputTokenCount = 5, OutputTokenCount = 5 },
            FinishReason = ChatFinishReason.Stop,
        });

        var agent = new Agent(llm, "model", permissions: evaluator);
        var chatSession = new ChatSession(agent, new AgentOptions(), toolContext: new Agency.Harness.Contexts.ToolContext { Registry = registry });

        var events = new List<AgentEvent>();
        await foreach (AgentEvent evt in chatSession.SendAsync("do it", ct))
        {
            events.Add(evt);
        }

        ToolInvokedEvent toolEvent = Assert.Single(events.OfType<ToolInvokedEvent>());
        Assert.True(toolEvent.Result.IsError);
        Assert.Contains("[Blocked]", toolEvent.Result.Content, StringComparison.Ordinal);
        Assert.Equal(0, ungrantedTool.InvokeCount);

        // Never parks.
        Assert.DoesNotContain(events, e => e is PermissionRequestedEvent);

        var result = Assert.IsType<AgentResultEvent>(events[^1]);
        Assert.Equal(AgentResultStatus.Success, result.Status);
        Assert.Equal(dotacp.protocol.StopReason.EndTurn, StopReasonMapper.MapTerminal(result));
    }

    /// <summary>(c) <see cref="PersonaPermissionEvaluator.Evaluate"/> never returns Ask, over an exhaustive sweep of tool names.</summary>
    [Fact]
    public void Evaluate_NeverReturnsAsk_OverExhaustiveToolNameSweep()
    {
        var evaluator = new PersonaPermissionEvaluator(["get_help", "invite_agent"]);

        string[] candidates =
        [
            "get_help", "invite_agent", // granted
            "shell_exec", "read_file", "write_file", "execute_powershell", "delete_everything",
            string.Empty, " ", "a", new string('x', 500),
            "tool with spaces", "tool\twith\ttabs", "tool\nwith\nnewlines",
            "unicode_🎉_tool", "get_help ", " get_help", "GET_HELP", "Get_Help",
        ];

        foreach (string name in candidates)
        {
            PermissionDecision decision = evaluator.Evaluate(name, default);
            Assert.IsNotType<PermissionDecision.Ask>(decision);
        }

        // A broad randomized sweep on top of the pinned edge cases above.
        var random = new Random(Seed: 42);
        for (int i = 0; i < 500; i++)
        {
            int length = random.Next(0, 40);
            var chars = new char[length];
            for (int c = 0; c < length; c++)
            {
                chars[c] = (char)random.Next('!', '~');
            }

            PermissionDecision decision = evaluator.Evaluate(new string(chars), default);
            Assert.IsNotType<PermissionDecision.Ask>(decision);
        }
    }

    /// <summary>
    /// (e) <see cref="PersonaPermissionEvaluator.SetGrantedTools"/> replaces the granted set —
    /// this is how <c>SessionFactory.CreateAsync</c> turns the empty set DI necessarily constructs
    /// this evaluator with (spec §6.6, Lock #6) into the session's real, explicit granted set once
    /// its <c>McpClientPool</c> has connected.
    /// </summary>
    [Fact]
    public void SetGrantedTools_ReplacesGrantedSet()
    {
        var evaluator = new PersonaPermissionEvaluator([]);
        Assert.IsType<PermissionDecision.Deny>(evaluator.Evaluate("get_help", default));

        evaluator.SetGrantedTools(["get_help"]);

        Assert.IsType<PermissionDecision.Allow>(evaluator.Evaluate("get_help", default));
        Assert.IsType<PermissionDecision.Deny>(evaluator.Evaluate("shell_exec", default));
    }

    /// <summary>(d) No file is written to <c>%LocalAppData%</c> during construction, evaluation, or <see cref="PersonaPermissionEvaluator.RecordAlwaysAsync"/>.</summary>
    [Fact]
    public async Task RecordAlwaysAsync_NeverWritesToLocalAppData()
    {
        string permissionsFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Agency", "permissions.local.json");

        bool existedBefore = File.Exists(permissionsFile);
        DateTime? mtimeBefore = existedBefore ? File.GetLastWriteTimeUtc(permissionsFile) : null;

        var evaluator = new PersonaPermissionEvaluator(["get_help"]);
        evaluator.Evaluate("get_help", default);
        evaluator.Evaluate("anything_else", default);
        await evaluator.RecordAlwaysAsync("anything_else", deny: false, TestContext.Current.CancellationToken);
        await evaluator.RecordAlwaysAsync("get_help", deny: true, TestContext.Current.CancellationToken);

        Assert.Equal(existedBefore, File.Exists(permissionsFile));
        if (existedBefore)
        {
            Assert.Equal(mtimeBefore, File.GetLastWriteTimeUtc(permissionsFile));
        }
    }
}
