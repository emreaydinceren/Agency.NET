using Agency.Harness.Contexts;
using Agency.Harness.Hooks;
using Agency.Memory.Common.Hooks;

namespace Agency.Memory.Common.Test;

/// <summary>Tests for <see cref="MemoryHookFactory"/> baseline composition and hook ordering.</summary>
public sealed class MemoryHookFactoryTests
{
    /// <summary>
    /// Verifies that the baseline hooks include the expected hook delegates
    /// (retrieval on OnPreIteration, timer on OnAssistantTurn).
    /// </summary>
    [Fact]
    public void Build_ProducesBaselineWithRetrieval_DistillerTimer_AuditHooks()
    {
        var baseline = MemoryHookFactory.BuildEmpty();

        // The baseline AgentHooks record should be non-null and have hooks wired.
        Assert.NotNull(baseline);
        // OnPreIteration (retrieval) and OnAssistantTurn (timer) are the core hooks.
        Assert.NotNull(baseline.OnPreIteration);
        Assert.NotNull(baseline.OnAssistantTurn);
    }

    /// <summary>
    /// Verifies that when using Compose (baseline first), the user hook sees Context
    /// after the baseline has already run.
    /// </summary>
    [Fact]
    public async Task Compose_BaselineFirst_UserHookSeesEnrichedContext()
    {
        var ct = TestContext.Current.CancellationToken;

        // Build a baseline that writes a sentinel value to context.
        string? sentinelSeenByUser = null;
        var baseline = new AgentHooks
        {
            OnPreIteration = (ctx, _) =>
            {
                ctx.Knowledge = new KnowledgeContext { Facts = ["sentinel-fact"] };
                return Task.CompletedTask;
            },
        };

        var userHooks = new AgentHooks
        {
            OnPreIteration = (ctx, _) =>
            {
                sentinelSeenByUser = ctx.Knowledge.Facts.FirstOrDefault();
                return Task.CompletedTask;
            },
        };

        var composed = baseline.Compose(userHooks);

        var context = new Context { Query = new QueryContext { Prompt = "test" } };
        await composed.OnPreIteration!(context, ct);

        Assert.Equal("sentinel-fact", sentinelSeenByUser);
    }

    /// <summary>
    /// Verifies that ComposeBefore (user hook first) executes the user hook
    /// before the baseline, so the user hook sees the un-enriched context.
    /// </summary>
    [Fact]
    public async Task ComposeBefore_UserHookFirst_SeesEmptyContext()
    {
        var ct = TestContext.Current.CancellationToken;

        bool userSawEmptyFacts = false;
        var baseline = new AgentHooks
        {
            OnPreIteration = (ctx, _) =>
            {
                ctx.Knowledge = new KnowledgeContext { Facts = ["sentinel-fact"] };
                return Task.CompletedTask;
            },
        };

        var userHooks = new AgentHooks
        {
            OnPreIteration = (ctx, _) =>
            {
                userSawEmptyFacts = ctx.Knowledge.Facts.Count == 0;
                return Task.CompletedTask;
            },
        };

        // ComposeBefore: userHooks runs FIRST, baseline runs second.
        var composed = baseline.ComposeBefore(userHooks);
        var context = new Context { Query = new QueryContext { Prompt = "test" } };
        await composed.OnPreIteration!(context, ct);

        Assert.True(userSawEmptyFacts, "User hook should see empty context when composed before baseline");
    }

    /// <summary>
    /// Verifies that the removed <c>OnPostToolUseFailure</c> hook is NOT present in <see cref="AgentHooks"/>.
    /// </summary>
    [Fact]
    public void OldOnPostToolUseFailure_NotRegistered()
    {
        var type = typeof(AgentHooks);

        // The property should not exist on the type at all (compile-time removal).
        var property = type.GetProperty("OnPostToolUseFailure");
        Assert.Null(property);
    }

    /// <summary>
    /// The 2-argument overload of <see cref="MemoryHookFactory.Build"/> must leave
    /// <see cref="AgentHooks.OnSessionStarted"/> null so existing callers are unaffected.
    /// </summary>
    [Fact]
    public void Build_TwoArgs_LeavesOnSessionStartedNull()
    {
        AgentHooks hooks = MemoryHookFactory.Build(
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask);

        Assert.Null(hooks.OnSessionStarted);
    }

    /// <summary>
    /// The 3-argument overload of <see cref="MemoryHookFactory.Build"/> must wire
    /// <see cref="AgentHooks.OnSessionStarted"/> to the supplied callback, and invoking
    /// the hook must call the callback.
    /// </summary>
    [Fact]
    public async Task Build_ThreeArgs_WiresOnSessionStarted_AndInvokesCallback()
    {
        bool callbackInvoked = false;
        Func<SessionStartedHookContext, CancellationToken, Task> sessionStarted =
            (_, _) =>
            {
                callbackInvoked = true;
                return Task.CompletedTask;
            };

        AgentHooks hooks = MemoryHookFactory.Build(
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            sessionStarted);

        Assert.NotNull(hooks.OnSessionStarted);

        var ctx = new Context { Query = new QueryContext { Prompt = "test" } };
        await hooks.OnSessionStarted!(new SessionStartedHookContext("s1", ctx), CancellationToken.None);

        Assert.True(callbackInvoked);
    }

    /// <summary>
    /// The 4-argument overload of <see cref="MemoryHookFactory.Build"/> must wire
    /// <see cref="AgentHooks.OnSessionEnd"/> to the supplied callback.
    /// </summary>
    [Fact]
    public async Task Build_FourArgs_WiresOnSessionEnd_AndInvokesCallback()
    {
        bool endCallbackInvoked = false;
        Func<SessionEndedHookContext, CancellationToken, Task> sessionEnd =
            (_, _) =>
            {
                endCallbackInvoked = true;
                return Task.CompletedTask;
            };

        AgentHooks hooks = MemoryHookFactory.Build(
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            sessionEndCallback: sessionEnd);

        Assert.NotNull(hooks.OnSessionEnd);

        var ctx = new Context { Query = new QueryContext { Prompt = "test" } };
        await hooks.OnSessionEnd!(new SessionEndedHookContext("s1", ctx), CancellationToken.None);

        Assert.True(endCallbackInvoked);
    }

    /// <summary>
    /// When no <c>sessionEndCallback</c> is supplied, <see cref="AgentHooks.OnSessionEnd"/>
    /// must remain null so existing callers are unaffected.
    /// </summary>
    [Fact]
    public void Build_WithoutSessionEndCallback_LeavesOnSessionEndNull()
    {
        AgentHooks hooks = MemoryHookFactory.Build(
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask);

        Assert.Null(hooks.OnSessionEnd);
    }
}
