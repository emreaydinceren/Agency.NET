using System.Text.Json;
using Agency.Harness.Contexts;
using Agency.Harness.Hooks;

namespace Agency.Harness.Test.Agents;

/// <summary>
/// Unit tests for <see cref="AgentHooksExtensions.Fold"/>, which combines baseline, configured,
/// and user-supplied <see cref="AgentHooks"/> into a single set that runs each source's handlers
/// in order and lets deny decisions from any source win.
/// </summary>
public sealed class AgentFactoryHookFoldTests
{
    private static Context MakeContext() =>
        new() { Query = new QueryContext { Prompt = "test" } };

    private static StopHookContext MakeStopCtx() =>
        new(new AgentResultEvent(AgentResultStatus.Success, null, new LlmTokenUsage(0, 0), 0m), MakeContext());

    private static PreToolUseHookContext MakePreToolUseCtx() =>
        new("tool", JsonSerializer.SerializeToElement(new Dictionary<string, object?>()), MakeContext());

    /// <summary>
    /// Folding three <see langword="null"/> hook sources returns <see langword="null"/> rather
    /// than an empty <see cref="AgentHooks"/> instance.
    /// </summary>
    [Fact]
    public void Fold_AllNull_ReturnsNull()
    {
        AgentHooks? result = AgentHooksExtensions.Fold(null, null, null);
        Assert.Null(result);
    }

    /// <summary>
    /// When all three sources define an <see cref="AgentHooks.OnStop"/> handler, the folded
    /// handler runs them in baseline, then configured, then user order.
    /// </summary>
    [Fact]
    public async Task Fold_OrderBaselineConfiguredUser()
    {
        var order = new List<string>();
        var baseline = new AgentHooks { OnStop = (ctx, ct) => { order.Add("baseline"); return Task.CompletedTask; } };
        var configured = new AgentHooks { OnStop = (ctx, ct) => { order.Add("configured"); return Task.CompletedTask; } };
        var user = new AgentHooks { OnStop = (ctx, ct) => { order.Add("user"); return Task.CompletedTask; } };

        AgentHooks? folded = AgentHooksExtensions.Fold(baseline, configured, user);
        await folded!.OnStop!(MakeStopCtx(), CancellationToken.None);

        Assert.Equal(["baseline", "configured", "user"], order);
    }

    /// <summary>
    /// If any source's <see cref="AgentHooks.OnPreToolUse"/> handler denies, the folded decision
    /// is a deny even when a later source in the fold order would allow.
    /// </summary>
    [Fact]
    public async Task Fold_DenyWinsAcrossSources()
    {
        var configuredHooks = new AgentHooks
        {
            OnPreToolUse = (ctx, ct) => Task.FromResult<PreToolUseDecision>(new PreToolUseDecision.Deny("config-deny"))
        };
        var userHooks = new AgentHooks
        {
            OnPreToolUse = (ctx, ct) => Task.FromResult(PreToolUseDecision.Allowed)
        };

        AgentHooks? folded = AgentHooksExtensions.Fold(null, configuredHooks, userHooks);
        PreToolUseDecision result = await folded!.OnPreToolUse!(MakePreToolUseCtx(), CancellationToken.None);

        Assert.IsType<PreToolUseDecision.Deny>(result);
    }
}
