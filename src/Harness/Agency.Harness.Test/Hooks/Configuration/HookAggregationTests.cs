using System.Text.Json;
using Agency.Harness.Hooks;
using Agency.Harness.Hooks.Configuration;
using Agency.Harness.Hooks.Configuration.Handlers;

namespace Agency.Harness.Test.Hooks.Configuration;

/// <summary>
/// Verifies the AggregateDecision algorithm on <see cref="HookRegistry"/>:
/// deny-wins, first-declared-wins ordering, exit-2 dominates rewrite in same output,
/// and fail-open on non-blocking errors.
/// </summary>
public sealed class HookAggregationTests
{
    private static JsonElement EmptyInput => JsonDocument.Parse("{}").RootElement.Clone();

    private static JsonElement JsonWithRewrite() =>
        JsonDocument.Parse("""{"tool_input":{"key":"rewritten"}}""").RootElement.Clone();

    // ── Test 1 ───────────────────────────────────────────────────────────────

    [Fact]
    public void Aggregate_DenyWinsOverRewriteOverAllow()
    {
        var outputs = new List<HookHandlerOutput>
        {
            new(HookExitCodes.Ok, null, null, null),              // Allow
            new(HookExitCodes.Ok, JsonWithRewrite(), null, null), // Rewrite
            new(HookExitCodes.BlockingDeny, null, null, null),    // Deny
        };

        PreToolUseDecision result = HookRegistry.AggregateDecision(outputs, EmptyInput);

        Assert.IsType<PreToolUseDecision.Deny>(result);
    }

    // ── Test 2 ───────────────────────────────────────────────────────────────

    [Fact]
    public void Aggregate_OrderDeterministic_FirstDenyWins()
    {
        var outputs = new List<HookHandlerOutput>
        {
            new(HookExitCodes.BlockingDeny, null, "first", null),
            new(HookExitCodes.BlockingDeny, null, "second", null),
        };

        PreToolUseDecision result = HookRegistry.AggregateDecision(outputs, EmptyInput);

        var deny = Assert.IsType<PreToolUseDecision.Deny>(result);
        Assert.Contains("first", deny.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ── Test 3 ───────────────────────────────────────────────────────────────

    [Fact]
    public void Aggregate_Exit2_OverridesRewriteInSameOutput()
    {
        var outputs = new List<HookHandlerOutput>
        {
            new(HookExitCodes.BlockingDeny, JsonWithRewrite(), null, null),
        };

        PreToolUseDecision result = HookRegistry.AggregateDecision(outputs, EmptyInput);

        Assert.IsType<PreToolUseDecision.Deny>(result);
    }

    // ── Test 4 ───────────────────────────────────────────────────────────────

    [Fact]
    public void Aggregate_NonBlockingError_FailsOpenAllow()
    {
        var outputs = new List<HookHandlerOutput>
        {
            new(HookExitCodes.NonBlockingError, null, null, null),
        };

        PreToolUseDecision result = HookRegistry.AggregateDecision(outputs, EmptyInput);

        Assert.IsType<PreToolUseDecision.Allow>(result);
    }

    // ── Test 5 ───────────────────────────────────────────────────────────────

    [Fact]
    public void Map_RewriteToolInput_ProducesRewriteWithElement()
    {
        var outputs = new List<HookHandlerOutput>
        {
            new(HookExitCodes.Ok, JsonWithRewrite(), null, null),
        };

        PreToolUseDecision result = HookRegistry.AggregateDecision(outputs, EmptyInput);

        Assert.IsType<PreToolUseDecision.Rewrite>(result);
    }
}
