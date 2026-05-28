using Agency.Agentic.Contexts;
using Agency.Agentic.Hooks;
using Agency.Memory.Common.Hooks;
using Agency.Memory.Common.Storage;

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
}
