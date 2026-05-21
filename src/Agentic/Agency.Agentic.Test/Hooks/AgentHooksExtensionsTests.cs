namespace Agency.Agentic.Hooks.Tests;

using System.Text.Json;
using Agency.Agentic.Contexts;
using Agency.Agentic.Hooks;

/// <summary>Verifies AgentHooks.Compose() merge semantics for all five delegate slots.</summary>
public sealed class AgentHooksExtensionsTests
{
    private static Context MakeContext() =>
        new() { Query = new QueryContext { Prompt = "test" } };

    private static PreToolUseHookContext MakePreCtx() =>
        new("tool", JsonSerializer.SerializeToElement(new Dictionary<string, object?>()), MakeContext());

    // ── Null-safety ────────────────────────────────────────────────────────────

    [Fact]
    public void Compose_FirstOnSessionStartedNull_UsesSecond()
    {
        Func<SessionStartedHookContext, CancellationToken, Task> h2 = (_, _) => Task.CompletedTask;
        AgentHooks result = AgentHooks.None.Compose(new AgentHooks { OnSessionStarted = h2 });
        Assert.Same(h2, result.OnSessionStarted);
    }

    [Fact]
    public void Compose_SecondOnSessionStartedNull_UsesFirst()
    {
        Func<SessionStartedHookContext, CancellationToken, Task> h1 = (_, _) => Task.CompletedTask;
        AgentHooks result = new AgentHooks { OnSessionStarted = h1 }.Compose(AgentHooks.None);
        Assert.Same(h1, result.OnSessionStarted);
    }

    [Fact]
    public void Compose_BothNull_AllDelegatesAreNull()
    {
        AgentHooks result = AgentHooks.None.Compose(AgentHooks.None);
        Assert.Null(result.OnSessionStarted);
        Assert.Null(result.OnPreToolUse);
        Assert.Null(result.OnPostToolUse);
        Assert.Null(result.OnAssistantTurn);
        Assert.Null(result.OnStop);
    }

    // ── Sequential execution ───────────────────────────────────────────────────

    [Fact]
    public async Task Compose_OnSessionStarted_BothRun_InOrder()
    {
        var log = new List<int>();
        var h1 = new AgentHooks { OnSessionStarted = (_, _) => { log.Add(1); return Task.CompletedTask; } };
        var h2 = new AgentHooks { OnSessionStarted = (_, _) => { log.Add(2); return Task.CompletedTask; } };

        AgentHooks composed = h1.Compose(h2);
        await composed.OnSessionStarted!(
            new SessionStartedHookContext("s", MakeContext()),
            CancellationToken.None);

        Assert.Equal([1, 2], log);
    }

    // ── PreToolUse: most-restrictive-wins ─────────────────────────────────────

    [Fact]
    public async Task Compose_PreToolUse_BothAllow_ReturnsAllow()
    {
        var h1 = new AgentHooks { OnPreToolUse = (_, _) => Task.FromResult(PreToolUseDecision.Allowed) };
        var h2 = new AgentHooks { OnPreToolUse = (_, _) => Task.FromResult(PreToolUseDecision.Allowed) };
        PreToolUseDecision result = await h1.Compose(h2).OnPreToolUse!(MakePreCtx(), CancellationToken.None);
        Assert.IsType<PreToolUseDecision.Allow>(result);
    }

    [Fact]
    public async Task Compose_PreToolUse_FirstDeny_ReturnsDeny()
    {
        var deny = new PreToolUseDecision.Deny("reason");
        var h1 = new AgentHooks { OnPreToolUse = (_, _) => Task.FromResult<PreToolUseDecision>(deny) };
        var h2 = new AgentHooks { OnPreToolUse = (_, _) => Task.FromResult(PreToolUseDecision.Allowed) };
        PreToolUseDecision result = await h1.Compose(h2).OnPreToolUse!(MakePreCtx(), CancellationToken.None);
        Assert.IsType<PreToolUseDecision.Deny>(result);
    }

    [Fact]
    public async Task Compose_PreToolUse_SecondDeny_ReturnsDeny()
    {
        var deny = new PreToolUseDecision.Deny("reason");
        var h1 = new AgentHooks { OnPreToolUse = (_, _) => Task.FromResult(PreToolUseDecision.Allowed) };
        var h2 = new AgentHooks { OnPreToolUse = (_, _) => Task.FromResult<PreToolUseDecision>(deny) };
        PreToolUseDecision result = await h1.Compose(h2).OnPreToolUse!(MakePreCtx(), CancellationToken.None);
        Assert.IsType<PreToolUseDecision.Deny>(result);
    }

    [Fact]
    public async Task Compose_PreToolUse_DenyBeatsRewrite()
    {
        var deny = new PreToolUseDecision.Deny("blocked");
        var rewrite = new PreToolUseDecision.Rewrite(JsonSerializer.SerializeToElement(1));
        var h1 = new AgentHooks { OnPreToolUse = (_, _) => Task.FromResult<PreToolUseDecision>(deny) };
        var h2 = new AgentHooks { OnPreToolUse = (_, _) => Task.FromResult<PreToolUseDecision>(rewrite) };
        PreToolUseDecision result = await h1.Compose(h2).OnPreToolUse!(MakePreCtx(), CancellationToken.None);
        Assert.IsType<PreToolUseDecision.Deny>(result);
    }

    [Fact]
    public async Task Compose_PreToolUse_RewriteBeatsAllow()
    {
        var rewrite = new PreToolUseDecision.Rewrite(JsonSerializer.SerializeToElement(42));
        var h1 = new AgentHooks { OnPreToolUse = (_, _) => Task.FromResult<PreToolUseDecision>(rewrite) };
        var h2 = new AgentHooks { OnPreToolUse = (_, _) => Task.FromResult(PreToolUseDecision.Allowed) };
        PreToolUseDecision result = await h1.Compose(h2).OnPreToolUse!(MakePreCtx(), CancellationToken.None);
        Assert.IsType<PreToolUseDecision.Rewrite>(result);
    }

    [Fact]
    public async Task Compose_PreToolUse_BothDeny_ReturnsFirstDeny()
    {
        var deny1 = new PreToolUseDecision.Deny("first");
        var deny2 = new PreToolUseDecision.Deny("second");
        var h1 = new AgentHooks { OnPreToolUse = (_, _) => Task.FromResult<PreToolUseDecision>(deny1) };
        var h2 = new AgentHooks { OnPreToolUse = (_, _) => Task.FromResult<PreToolUseDecision>(deny2) };
        PreToolUseDecision result = await h1.Compose(h2).OnPreToolUse!(MakePreCtx(), CancellationToken.None);
        PreToolUseDecision.Deny deny = Assert.IsType<PreToolUseDecision.Deny>(result);
        Assert.Equal("first", deny.Reason);
    }
}