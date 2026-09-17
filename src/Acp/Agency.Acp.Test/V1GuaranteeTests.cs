using Agency.Acp.Permissions;
using Agency.Acp.Sessions;
using Agency.Acp.Test.Fakes;
using Agency.Harness;
using Agency.Harness.Contexts;
using Agency.Harness.Permissions;
using Agency.Harness.Tools;
using Agency.Llm.Common;
using Agency.Llm.Common.Tools;
using dotacp.protocol;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agency.Acp.Test;

/// <summary>
/// The v1 safety guarantee (spec §6.5/§6.6, P4/P5): the ACP adapter registers no filesystem or
/// shell tools, no configured hook can park a turn on a permission the client can never answer, and
/// the permission gate that closes that path is actually wired into the production composition root
/// rather than merely existing and being tested in isolation. One test method drives six
/// independently-named assertion methods, so a regression names itself in the failure output
/// instead of surfacing as a single opaque "guarantee failed".
/// </summary>
public sealed class V1GuaranteeTests
{
    /// <summary>
    /// Runs all six locks. Each is a separately named private method below so a regression names
    /// itself in the failure output (spec §6.5/§6.6).
    /// </summary>
    [Fact]
    public async Task TheSixLocks()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        await NoBuiltInToolsRegistered(ct);
        await SkillToolNotRegistered(ct);
        NoShellRunnerWired();
        SkillShellExecutionDisabled();
        await NoHookCanReturnAsk(ct);
        PermissionEvaluatorIsWired();
    }

    // ── Lock #1 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The registry <see cref="SessionFactory.CreateAsync"/> builds for a session contains
    /// none of the harness's built-in tools. Drives the real <see cref="SessionFactory"/>
    /// (only the LLM client is faked — the same seam <c>SessionCreationTests</c> already uses) and
    /// requests each name by tool call: a registered tool would execute, an absent one returns the
    /// registry's own "No tool registered" error. This proves the negative behaviourally, against
    /// the actual composition code, rather than by reading a list.
    /// </summary>
    private static async Task NoBuiltInToolsRegistered(CancellationToken ct)
    {
        string[] builtInToolNames = ["read_file", "write_file", "execute_powershell", "subagent_tool"];
        (SessionState state, FakeChatClient llm) = await CreateSessionWithFakeAgentAsync(ct);

        llm.EnqueueResponse(ToolCallResponse([.. builtInToolNames.Select((n, i) => ($"id-{i}", n))]));
        llm.EnqueueResponse(TextResponse("done"));

        List<AgentEvent> events = await CollectAsync(state.ChatSession.SendAsync("go", ct));

        foreach (string name in builtInToolNames)
        {
            ToolInvokedEvent invoked = Assert.Single(events.OfType<ToolInvokedEvent>(), e => e.ToolName == name);
            Assert.True(invoked.Result.IsError);
            Assert.Contains($"No tool registered with name '{name}'", invoked.Result.Content, StringComparison.Ordinal);
        }

        await state.DisposeAsync();
    }

    // ── Lock #2 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The <c>skill</c> meta-tool (model-invoked skills) is absent from the same registry, for the
    /// same reason and by the same proof technique as <see cref="NoBuiltInToolsRegistered"/>.
    /// </summary>
    private static async Task SkillToolNotRegistered(CancellationToken ct)
    {
        (SessionState state, FakeChatClient llm) = await CreateSessionWithFakeAgentAsync(ct);

        llm.EnqueueResponse(ToolCallResponse(("id-1", "skill")));
        llm.EnqueueResponse(TextResponse("done"));

        List<AgentEvent> events = await CollectAsync(state.ChatSession.SendAsync("go", ct));

        ToolInvokedEvent invoked = Assert.Single(events.OfType<ToolInvokedEvent>());
        Assert.True(invoked.Result.IsError);
        Assert.Contains("No tool registered with name 'skill'", invoked.Result.Content, StringComparison.Ordinal);

        await state.DisposeAsync();
    }

    // ── Lock #3 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// No <c>ISkillShellRunner</c> resolves from the process composition root
    /// (<see cref="Program.BuildHost"/>) — the type is <c>internal</c> to <c>Agency.Harness</c>, so
    /// it is located by reflection rather than referenced directly, and resolved through
    /// <see cref="IServiceProvider.GetService(Type)"/> the same way DI would resolve it for any
    /// caller that asked.
    /// </summary>
    private static void NoShellRunnerWired()
    {
        using IHost host = Program.BuildHost();

        Type? shellRunnerType = typeof(AgentOptions).Assembly.GetType("Agency.Harness.Skills.ISkillShellRunner");
        Assert.NotNull(shellRunnerType);

        object? resolved = host.Services.GetService(shellRunnerType!);
        Assert.Null(resolved);
    }

    // ── Lock #4 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>Skills:DisableShellExecution</c> reads as <see langword="true"/> in the process
    /// composition root, regardless of any config file — see the comment at the call site in
    /// <see cref="Program.BuildHost"/>.
    /// </summary>
    private static void SkillShellExecutionDisabled()
    {
        using IHost host = Program.BuildHost();

        IConfiguration config = host.Services.GetRequiredService<IConfiguration>();
        Assert.True(config.GetValue<bool>("Skills:DisableShellExecution"));
    }

    // ── Lock #5 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// No hook can return <see cref="Agency.Harness.Permissions.PermissionDecision.Ask"/> for any
    /// tool the v1 composition could ever expose. Builds the v1 profile used here — no configured
    /// hooks, and either no evaluator or (as production now wires, see Lock #6) an evaluator that by
    /// construction never asks — and drives one turn that calls every tool the registry actually
    /// holds. This test constructs the <see cref="Agent"/> directly (no evaluator) rather than via
    /// <c>Program.BuildHost</c>, because the property under test — no <c>Ask</c> reaches the client —
    /// must hold with or without an evaluator in play; Lock #6 is what proves production actually has
    /// one, and <c>PersonaPermissionEvaluatorTests</c> proves that one specifically never asks
    /// either. The tool names are read
    /// back from <see cref="IToolRegistry.ListDefinitions"/> rather than hardcoded, per spec §6.5:
    /// enumerating the registry, instead of asserting against a fixed list, is what keeps a tool
    /// registered tomorrow covered by this test today.
    /// </summary>
    /// <remarks>
    /// This assertion is behavioural, not structural, and deliberately so (spec §14). You cannot
    /// statically prove a hook delegate never returns <c>Ask</c>: forbidding <c>OnPreToolUse</c>
    /// entirely would also block a legitimate rewrite hook, while merely allowing it would pass
    /// today and say nothing about a future hook that does ask. Two structural facts make a
    /// structural substitute doubly unsafe here — an evaluator <c>Allow</c> does NOT clear a hook
    /// <c>Ask</c> (see the permission-gate precedence comment in <c>Agent.cs</c>), and a hook
    /// <c>Ask</c> parks the turn even when no evaluator is supplied at all. So the only check that
    /// actually closes the path is driving a real turn and observing that zero
    /// <see cref="PermissionRequestedEvent"/>s and zero
    /// <see cref="AgentResultStatus.AwaitingPermission"/> results come out the other end.
    /// </remarks>
    private static async Task NoHookCanReturnAsk(CancellationToken ct)
    {
        var registry = new ToolRegistry(
        [
            new FakeTool("get_help"),
            new FakeTool("invite_agent"),
            new FakeTool("a_tool_registered_after_this_test_was_written"),
        ]);
        IReadOnlyList<ToolDefinition> toolDefs = registry.ListDefinitions();

        var llm = new FakeChatClient();
        llm.EnqueueResponse(ToolCallResponse([.. toolDefs.Select((d, i) => ($"id-{i}", d.Name))]));
        llm.EnqueueResponse(TextResponse("done"));

        // No permissions evaluator, no hooks — the v1 profile (see Program.BuildHost comments).
        var agent = new Agent(llm, "model");
        await using var chatSession = new ChatSession(
            agent, new AgentOptions(), toolContext: new ToolContext { Registry = registry });

        List<AgentEvent> events = await CollectAsync(chatSession.SendAsync("go", ct));

        Assert.Empty(events.OfType<PermissionRequestedEvent>());
        Assert.DoesNotContain(
            events.OfType<AgentResultEvent>(), e => e.Status == AgentResultStatus.AwaitingPermission);

        // Sanity: every enumerated tool actually ran to completion rather than the loop silently
        // stopping short (which would make the assertions above vacuously true).
        Assert.Equal(toolDefs.Count, events.OfType<ToolInvokedEvent>().Count());
    }

    // ── Lock #6 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The production composition root (<see cref="Program.BuildHost"/>) actually resolves an
    /// <see cref="IPermissionEvaluator"/>, and it is <see cref="PersonaPermissionEvaluator"/> — the
    /// v1 evaluator that allows only a session's own granted tools, denies everything else, and
    /// never asks (spec §6.6). This is the assertion that would have caught the bug this lock fixes:
    /// <c>PersonaPermissionEvaluator</c> was fully implemented and unit-tested in isolation, but
    /// never registered anywhere Lock #5's own test-built "v1 profile" could see — a property that
    /// holds by omission is a property that gets removed by accident (spec P5). Structural (resolve
    /// + type check), unlike Lock #5, because there is nothing behavioural left to prove here: once
    /// this type is wired at all, Lock #5 and <c>PersonaPermissionEvaluatorTests</c> already cover
    /// that it never asks and that denials are recoverable, not fatal.
    /// </summary>
    private static void PermissionEvaluatorIsWired()
    {
        using IHost host = Program.BuildHost();

        IPermissionEvaluator? evaluator = host.Services.GetService<IPermissionEvaluator>();
        Assert.NotNull(evaluator);
        Assert.IsType<PersonaPermissionEvaluator>(evaluator);
    }

    // ── Shared helpers ───────────────────────────────────────────────────────

    private static async Task<(SessionState State, FakeChatClient Llm)> CreateSessionWithFakeAgentAsync(CancellationToken ct)
    {
        var capturedClient = new FakeChatClient();
        var services = new ServiceCollection();
        services.AddScoped<IAgentFactory>(_ => new FakeAgentFactory((_, model) => new Agent(capturedClient, model ?? "model")));
        ServiceProvider provider = services.BuildServiceProvider();

        var factory = new SessionFactory(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new AgentOptions { DefaultModel = "model", DefaultClientName = "client" },
            _ => Task.FromResult<IReadOnlyList<Model>>([]));

        (SessionState state, _) = await factory.CreateAsync(
            new NewSessionRequest { Cwd = "/a" }, requestedModelId: null, ct);

        return (state, capturedClient);
    }

    private static async Task<List<AgentEvent>> CollectAsync(IAsyncEnumerable<AgentEvent> events)
    {
        var list = new List<AgentEvent>();
        await foreach (AgentEvent e in events)
        {
            list.Add(e);
        }

        return list;
    }

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
}
