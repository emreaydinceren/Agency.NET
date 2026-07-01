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

    /// <summary>When outputs mix allow, rewrite, and deny, the aggregated decision is deny — the most restrictive outcome wins.</summary>
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

    /// <summary>When multiple outputs deny, the aggregated deny reason is deterministically the first-declared handler's reason.</summary>
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

    /// <summary>When a single output carries both a blocking-deny exit code and rewrite JSON, the deny exit code takes precedence over the rewrite payload.</summary>
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

    /// <summary>An output with <c>HookExitCodes.NonBlockingError</c> fails open, aggregating to allow rather than blocking the tool call.</summary>
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

    /// <summary>An output whose JSON carries a <c>tool_input</c> element aggregates to <see cref="PreToolUseDecision.Rewrite"/> with that element attached.</summary>
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
