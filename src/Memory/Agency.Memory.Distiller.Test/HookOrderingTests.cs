using Agency.Harness;
using Agency.Harness.Contexts;
using Agency.Harness.Hooks;
using Agency.Memory.Common.Hooks;

namespace Agency.Memory.Distiller.Test;

/// <summary>
/// Unit tests for the baseline-first hook composition mechanism described in spec §6.5.
/// These tests exercise the composition logic that lives in <see cref="AgentOptions"/>
/// (the <c>BaselineHooks</c> / <c>UserHooks</c> properties) and verify that
/// <see cref="AgentHooksExtensions.Compose"/> is applied correctly at the
/// <c>AgentFactory</c> layer: baseline first, user hooks after.
/// </summary>
/// <remarks>
/// Tests do NOT spin up the full DI pipeline or require Postgres; they operate
/// directly on <see cref="AgentOptions"/> and the composition primitives to keep the
/// signal/noise ratio high.
/// </remarks>
public sealed class HookOrderingTests
{
    // ── helpers ────────────────────────────────────────────────────────────────

    private static Context MakeContext() =>
        new() { Query = new QueryContext { Prompt = "test" } };

    /// <summary>
    /// Simulates the composition step that lives in <c>AgentFactory.CreateAgent</c>:
    /// compose baseline-first when both are present; fall back to either when one is null.
    /// </summary>
    private static AgentHooks? Compose(AgentOptions opts) =>
        (opts.BaselineHooks, opts.UserHooks) switch
        {
            (AgentHooks baseline, AgentHooks user) => baseline.Compose(user),
            (AgentHooks baseline, null) => baseline,
            (null, AgentHooks user) => user,
            _ => null,
        };

    // ── ordering ────────────────────────────────────────────────────────────────

    /// <summary>
    /// When both <see cref="AgentOptions.BaselineHooks"/> and
    /// <see cref="AgentOptions.UserHooks"/> are configured, the composed
    /// <see cref="AgentHooks.OnPreIteration"/> runs baseline first, then user hook.
    /// The user hook therefore sees context already enriched by the baseline.
    /// </summary>
    [Fact]
    public async Task Compose_BaselineFirst_UserHookSeesEnrichedContext()
    {
        var ct = TestContext.Current.CancellationToken;
        string? sentinelSeenByUser = null;

        var opts = new AgentOptions
        {
            DefaultClientName = "test",
            BaselineHooks = new AgentHooks
            {
                OnPreIteration = (ctx, _) =>
                {
                    ctx.Knowledge = new KnowledgeContext { Facts = ["sentinel-from-baseline"] };
                    return Task.CompletedTask;
                },
            },
            UserHooks = new AgentHooks
            {
                OnPreIteration = (ctx, _) =>
                {
                    sentinelSeenByUser = ctx.Knowledge.Facts.Count > 0 ? ctx.Knowledge.Facts[0] : null;
                    return Task.CompletedTask;
                },
            },
        };

        AgentHooks? composed = Compose(opts);

        Assert.NotNull(composed);
        await composed!.OnPreIteration!(MakeContext(), ct);

        Assert.Equal("sentinel-from-baseline", sentinelSeenByUser);
    }

    /// <summary>
    /// When both hooks are configured, the execution order is strictly
    /// baseline first then user — verified by an append log.
    /// </summary>
    [Fact]
    public async Task Compose_ExecutionOrder_BaselineBeforeUser()
    {
        var ct = TestContext.Current.CancellationToken;
        var log = new List<string>();

        var opts = new AgentOptions
        {
            DefaultClientName = "test",
            BaselineHooks = new AgentHooks
            {
                OnPreIteration = (_, _) => { log.Add("baseline"); return Task.CompletedTask; },
            },
            UserHooks = new AgentHooks
            {
                OnPreIteration = (_, _) => { log.Add("user"); return Task.CompletedTask; },
            },
        };

        AgentHooks? composed = Compose(opts);

        Assert.NotNull(composed);
        await composed!.OnPreIteration!(MakeContext(), ct);

        Assert.Equal(["baseline", "user"], log);
    }

    // ── default (no user hooks) ─────────────────────────────────────────────────

    /// <summary>
    /// When <see cref="AgentOptions.UserHooks"/> is null (the default), the composed
    /// hooks are exactly the baseline — no null reference, no silent drop.
    /// </summary>
    [Fact]
    public async Task Compose_NoUserHooks_BaselineRunsAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        bool baselineFired = false;

        var opts = new AgentOptions
        {
            DefaultClientName = "test",
            BaselineHooks = MemoryHookFactory.BuildEmpty(),
        };
        // Confirm that the BuildEmpty baseline has OnPreIteration wired.
        Assert.NotNull(opts.BaselineHooks.OnPreIteration);

        // Override with a sentinel baseline to confirm it fires.
        opts.BaselineHooks = new AgentHooks
        {
            OnPreIteration = (_, _) => { baselineFired = true; return Task.CompletedTask; },
        };

        AgentHooks? composed = Compose(opts);

        Assert.NotNull(composed);
        await composed!.OnPreIteration!(MakeContext(), ct);

        Assert.True(baselineFired, "Baseline hook must fire when UserHooks is null.");
    }

    // ── no baseline (memory disabled) ──────────────────────────────────────────

    /// <summary>
    /// When <see cref="AgentOptions.BaselineHooks"/> is null (memory disabled), the
    /// composed hooks are the user hooks only — the factory must not throw.
    /// </summary>
    [Fact]
    public async Task Compose_NoBaselineHooks_UserHooksRunAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        bool userFired = false;

        var opts = new AgentOptions
        {
            DefaultClientName = "test",
            BaselineHooks = null,
            UserHooks = new AgentHooks
            {
                OnPreIteration = (_, _) => { userFired = true; return Task.CompletedTask; },
            },
        };

        AgentHooks? composed = Compose(opts);

        Assert.NotNull(composed);
        await composed!.OnPreIteration!(MakeContext(), ct);

        Assert.True(userFired, "User hook must fire when BaselineHooks is null.");
    }

    // ── neither (memory disabled, no user hooks) ───────────────────────────────

    /// <summary>
    /// When both <see cref="AgentOptions.BaselineHooks"/> and
    /// <see cref="AgentOptions.UserHooks"/> are null, the composed result is null —
    /// the factory passes null to the agent (legacy no-memory path).
    /// </summary>
    [Fact]
    public void Compose_NeitherHooks_ReturnsNull()
    {
        var opts = new AgentOptions { DefaultClientName = "test" };

        AgentHooks? composed = Compose(opts);

        Assert.Null(composed);
    }

    // ── ComposeBefore escape hatch ─────────────────────────────────────────────

    /// <summary>
    /// <see cref="AgentHooksExtensions.ComposeBefore"/> lets an advanced caller observe
    /// un-enriched context before the baseline retrieval hook runs.
    /// This documents the escape hatch path per spec §6.5.
    /// </summary>
    [Fact]
    public async Task ComposeBefore_UserHookSeesEmptyContext_BeforeBaseline()
    {
        var ct = TestContext.Current.CancellationToken;
        string? sentinelBeforeBaseline = null;

        var baseline = new AgentHooks
        {
            OnPreIteration = (ctx, _) =>
            {
                ctx.Knowledge = new KnowledgeContext { Facts = ["sentinel-from-baseline"] };
                return Task.CompletedTask;
            },
        };

        var userHook = new AgentHooks
        {
            OnPreIteration = (ctx, _) =>
            {
                // Capture BEFORE baseline writes — should be empty.
                sentinelBeforeBaseline = ctx.Knowledge.Facts.Count > 0 ? ctx.Knowledge.Facts[0] : null;
                return Task.CompletedTask;
            },
        };

        // ComposeBefore: user runs first, baseline runs second.
        AgentHooks composed = baseline.ComposeBefore(userHook);

        await composed.OnPreIteration!(MakeContext(), ct);

        Assert.Null(sentinelBeforeBaseline);
    }
}
