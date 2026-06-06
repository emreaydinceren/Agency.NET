using System.Text.Json;
using Agency.Harness.Hooks;
using Agency.Harness.Hooks.Configuration;
using Agency.Harness.Hooks.Configuration.Handlers;

namespace Agency.Harness.Test.Hooks.Configuration;

/// <summary>
/// RED tests for the <c>PreToolUseDecision.Ask</c> variant (Task 10).
/// All tests are expected to fail until Task 11 adds the Ask variant and wires
/// <c>"ask"</c> into <see cref="HookRegistry.MapToDecision"/> /
/// <see cref="HookRegistry.AggregateDecision"/>.
/// </summary>
public sealed class PreToolUseAskTests
{
    private static JsonElement EmptyInput => JsonDocument.Parse("{}").RootElement.Clone();

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="HookHandlerOutput"/> that carries the Claude-Code-style
    /// <c>hookSpecificOutput.permissionDecision = "ask"</c> JSON, exactly mirroring
    /// how existing deny tests exercise this path.
    /// </summary>
    private static HookHandlerOutput AskOutput(string? reason)
    {
        string json = reason is not null
            ? $"{{\"hookSpecificOutput\":{{\"permissionDecision\":\"ask\",\"permissionDecisionReason\":\"{reason}\"}}}}"
            : "{\"hookSpecificOutput\":{\"permissionDecision\":\"ask\"}}";
        JsonElement element = JsonDocument.Parse(json).RootElement.Clone();
        return new HookHandlerOutput(HookExitCodes.Ok, element, null, null);
    }

    private static HookHandlerOutput DenyOutput(string? reason = null)
    {
        string json = reason is not null
            ? $"{{\"hookSpecificOutput\":{{\"permissionDecision\":\"deny\",\"permissionDecisionReason\":\"{reason}\"}}}}"
            : "{\"hookSpecificOutput\":{\"permissionDecision\":\"deny\"}}";
        JsonElement element = JsonDocument.Parse(json).RootElement.Clone();
        return new HookHandlerOutput(HookExitCodes.Ok, element, null, null);
    }

    private static HookHandlerOutput RewriteOutput() =>
        new(HookExitCodes.Ok, JsonDocument.Parse("""{"tool_input":{"key":"rewritten"}}""").RootElement.Clone(), null, null);

    private static HookHandlerOutput AllowOutput() =>
        new(HookExitCodes.Ok, null, null, null);

    // ── Mapping tests (through HookRegistry.MapToDecision via AggregateDecision) ──

    // ── Test 1 ───────────────────────────────────────────────────────────────

    [Fact]
    public void Map_AskDecisionWithReason_ProducesAskWithReason()
    {
        // JSON: { "hookSpecificOutput": { "permissionDecision": "ask", "permissionDecisionReason": "needs human eyes" } }
        // Mirrors the deny-mapping path; AggregateDecision is the established seam.
        var outputs = new List<HookHandlerOutput> { AskOutput("needs human eyes") };

        PreToolUseDecision result = HookRegistry.AggregateDecision(outputs, EmptyInput);

        var ask = Assert.IsType<PreToolUseDecision.Ask>(result);
        Assert.Equal("needs human eyes", ask.Reason);
    }

    // ── Test 2 ───────────────────────────────────────────────────────────────

    [Fact]
    public void Map_AskDecisionWithoutReason_ProducesAskWithNullReason()
    {
        // JSON: { "hookSpecificOutput": { "permissionDecision": "ask" } } — no reason field
        var outputs = new List<HookHandlerOutput> { AskOutput(null) };

        PreToolUseDecision result = HookRegistry.AggregateDecision(outputs, EmptyInput);

        var ask = Assert.IsType<PreToolUseDecision.Ask>(result);
        Assert.Null(ask.Reason);
    }

    // ── Aggregation-precedence tests (Deny > Ask > Rewrite > Allow) ──────────

    // ── Test 3 ───────────────────────────────────────────────────────────────

    [Fact]
    public void Aggregate_DenyAndAsk_DenyWins()
    {
        // Deny > Ask — deny must still dominate when an Ask is also present.
        var outputs = new List<HookHandlerOutput>
        {
            DenyOutput("deny reason"),
            AskOutput("ask reason"),
        };

        PreToolUseDecision result = HookRegistry.AggregateDecision(outputs, EmptyInput);

        Assert.IsType<PreToolUseDecision.Deny>(result);
    }

    // ── Test 4 ───────────────────────────────────────────────────────────────

    [Fact]
    public void Aggregate_AskAndRewrite_AskWins()
    {
        // Ask > Rewrite — ask must outrank a rewrite.
        var outputs = new List<HookHandlerOutput>
        {
            AskOutput("needs human eyes"),
            RewriteOutput(),
        };

        PreToolUseDecision result = HookRegistry.AggregateDecision(outputs, EmptyInput);

        Assert.IsType<PreToolUseDecision.Ask>(result);
    }

    // ── Test 5 ───────────────────────────────────────────────────────────────

    [Fact]
    public void Aggregate_AskAndAllow_AskWins()
    {
        // Ask > Allow (silence) — ask must outrank a plain allow.
        var outputs = new List<HookHandlerOutput>
        {
            AskOutput("needs confirmation"),
            AllowOutput(),
        };

        PreToolUseDecision result = HookRegistry.AggregateDecision(outputs, EmptyInput);

        Assert.IsType<PreToolUseDecision.Ask>(result);
    }

    // ── Test 6 ───────────────────────────────────────────────────────────────

    [Fact]
    public void Aggregate_TwoAsks_FirstNullReasonSecondHasReason_SecondReasonKept()
    {
        // When several handlers return Ask, the first NON-NULL reason is kept.
        var outputs = new List<HookHandlerOutput>
        {
            AskOutput(null),          // null reason — skipped
            AskOutput("second reason"),
        };

        PreToolUseDecision result = HookRegistry.AggregateDecision(outputs, EmptyInput);

        var ask = Assert.IsType<PreToolUseDecision.Ask>(result);
        Assert.Equal("second reason", ask.Reason);
    }

    // ── Test 7 ───────────────────────────────────────────────────────────────

    [Fact]
    public void Aggregate_TwoAsks_BothHaveReasons_FirstReasonKept()
    {
        // When several handlers return Ask and all have reasons, the first one wins.
        var outputs = new List<HookHandlerOutput>
        {
            AskOutput("first reason"),
            AskOutput("second reason"),
        };

        PreToolUseDecision result = HookRegistry.AggregateDecision(outputs, EmptyInput);

        var ask = Assert.IsType<PreToolUseDecision.Ask>(result);
        Assert.Equal("first reason", ask.Reason);
    }
}
