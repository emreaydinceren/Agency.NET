using System.Text.Json;
using System.Text.Json.Serialization;

using Agency.Harness.Loop;

namespace Agency.Harness.Test.Loop;

/// <summary>
/// Phase 0 / T-MODEL-1: verifies that all Loop Kit domain records and enums
/// round-trip through <see cref="System.Text.Json"/> via the source-generated
/// <see cref="LoopJsonContext"/> — AOT/trim-safe, no reflection fallback.
/// </summary>
public sealed class LoopModelTests
{
    // ── Shared options wired to the source-gen context ────────────────────────

    private static readonly JsonSerializerOptions Options =
        new(LoopJsonContext.Default.Options);

    // ── GoalSpec ──────────────────────────────────────────────────────────────

    /// <summary>GoalSpec with only the required field round-trips correctly.</summary>
    [Fact]
    public void GoalSpec_RoundTrip_RequiredFieldOnly()
    {
        var original = new GoalSpec { Condition = "build passes" };

        string json = JsonSerializer.Serialize(original, LoopJsonContext.Default.GoalSpec);
        GoalSpec? restored = JsonSerializer.Deserialize(json, LoopJsonContext.Default.GoalSpec);

        Assert.NotNull(restored);
        Assert.Equal(original.Condition, restored.Condition);
        Assert.Equal(12, restored.MaxTurns);           // default
        Assert.Null(restored.Budget);
        Assert.Null(restored.TokenBudget);
        Assert.Null(restored.WallClockSeconds);
    }

    /// <summary>GoalSpec with all optional cap fields set round-trips correctly.</summary>
    [Fact]
    public void GoalSpec_RoundTrip_AllCapsSet()
    {
        var original = new GoalSpec
        {
            Condition = "all tests green",
            MaxTurns = 5,
            Budget = 0.50m,
            TokenBudget = 100_000L,
            WallClockSeconds = 300,
        };

        string json = JsonSerializer.Serialize(original, LoopJsonContext.Default.GoalSpec);
        GoalSpec? restored = JsonSerializer.Deserialize(json, LoopJsonContext.Default.GoalSpec);

        Assert.NotNull(restored);
        Assert.Equal(original, restored);
    }

    // ── Verdict ───────────────────────────────────────────────────────────────

    /// <summary>Verdict.Continue round-trips through the source-gen context.</summary>
    [Fact]
    public void Verdict_Continue_RoundTrip()
    {
        var original = new Verdict.Continue("needs more work");

        // STJ source-gen uses the simple class name as the property: Verdict.Continue → .Continue
        string json = JsonSerializer.Serialize(original, LoopJsonContext.Default.Continue);
        Verdict.Continue? restored =
            JsonSerializer.Deserialize(json, LoopJsonContext.Default.Continue);

        Assert.NotNull(restored);
        Assert.Equal(original.Reason, restored.Reason);
    }

    /// <summary>Verdict.Done round-trips through the source-gen context.</summary>
    [Fact]
    public void Verdict_Done_RoundTrip()
    {
        var original = new Verdict.Done("goal achieved");

        // STJ source-gen uses the simple class name as the property: Verdict.Done → .Done
        string json = JsonSerializer.Serialize(original, LoopJsonContext.Default.Done);
        Verdict.Done? restored =
            JsonSerializer.Deserialize(json, LoopJsonContext.Default.Done);

        Assert.NotNull(restored);
        Assert.Equal(original.Reason, restored.Reason);
    }

    // ── LoopOutcome ───────────────────────────────────────────────────────────

    /// <summary>All LoopOutcome values serialize as their string name (camelCase by convention).</summary>
    [Theory]
    [InlineData(LoopOutcome.Achieved)]
    [InlineData(LoopOutcome.CapReached)]
    [InlineData(LoopOutcome.BudgetExceeded)]
    [InlineData(LoopOutcome.Error)]
    [InlineData(LoopOutcome.Cancelled)]
    public void LoopOutcome_SerializesAsString(LoopOutcome outcome)
    {
        string json = JsonSerializer.Serialize(outcome, LoopJsonContext.Default.LoopOutcome);
        LoopOutcome restored = JsonSerializer.Deserialize(json, LoopJsonContext.Default.LoopOutcome);

        Assert.Equal(outcome, restored);
    }

    // ── New AgentEvent subtypes ───────────────────────────────────────────────

    /// <summary>GoalSetEvent round-trips through the source-gen context.</summary>
    [Fact]
    public void GoalSetEvent_RoundTrip()
    {
        var original = new GoalSetEvent(new GoalSpec { Condition = "done" });

        string json = JsonSerializer.Serialize(original, LoopJsonContext.Default.GoalSetEvent);
        GoalSetEvent? restored =
            JsonSerializer.Deserialize(json, LoopJsonContext.Default.GoalSetEvent);

        Assert.NotNull(restored);
        Assert.Equal(original.Goal.Condition, restored.Goal.Condition);
    }

    /// <summary>TurnStartedEvent round-trips through the source-gen context.</summary>
    [Fact]
    public void TurnStartedEvent_RoundTrip()
    {
        var original = new TurnStartedEvent(TurnIndex: 3, Directive: "keep going");

        string json = JsonSerializer.Serialize(original, LoopJsonContext.Default.TurnStartedEvent);
        TurnStartedEvent? restored =
            JsonSerializer.Deserialize(json, LoopJsonContext.Default.TurnStartedEvent);

        Assert.NotNull(restored);
        Assert.Equal(original, restored);
    }

    /// <summary>VerdictEvent with a Continue verdict round-trips through the source-gen context.</summary>
    [Fact]
    public void VerdictEvent_Continue_RoundTrip()
    {
        var original = new VerdictEvent(TurnIndex: 1, Verdict: new Verdict.Continue("not yet"));

        string json = JsonSerializer.Serialize(original, LoopJsonContext.Default.VerdictEvent);
        VerdictEvent? restored =
            JsonSerializer.Deserialize(json, LoopJsonContext.Default.VerdictEvent);

        Assert.NotNull(restored);
        Assert.Equal(original.TurnIndex, restored.TurnIndex);

        var continuedVerdict = Assert.IsType<Verdict.Continue>(restored.Verdict);
        Assert.Equal("not yet", continuedVerdict.Reason);
    }

    /// <summary>VerdictEvent with a Done verdict round-trips through the source-gen context.</summary>
    [Fact]
    public void VerdictEvent_Done_RoundTrip()
    {
        var original = new VerdictEvent(TurnIndex: 2, Verdict: new Verdict.Done("finished!"));

        string json = JsonSerializer.Serialize(original, LoopJsonContext.Default.VerdictEvent);
        VerdictEvent? restored =
            JsonSerializer.Deserialize(json, LoopJsonContext.Default.VerdictEvent);

        Assert.NotNull(restored);
        Assert.Equal(original.TurnIndex, restored.TurnIndex);

        var doneVerdict = Assert.IsType<Verdict.Done>(restored.Verdict);
        Assert.Equal("finished!", doneVerdict.Reason);
    }

    /// <summary>LoopResultEvent round-trips through the source-gen context.</summary>
    [Fact]
    public void LoopResultEvent_RoundTrip()
    {
        var original = new LoopResultEvent(
            Outcome: LoopOutcome.Achieved,
            FinalText: "All done.",
            TotalUsage: new LlmTokenUsage(500, 200),
            TotalCostUsd: 0.01m);

        string json = JsonSerializer.Serialize(original, LoopJsonContext.Default.LoopResultEvent);
        LoopResultEvent? restored =
            JsonSerializer.Deserialize(json, LoopJsonContext.Default.LoopResultEvent);

        Assert.NotNull(restored);
        Assert.Equal(original.Outcome, restored.Outcome);
        Assert.Equal(original.FinalText, restored.FinalText);
        Assert.Equal(original.TotalUsage.InputTokens, restored.TotalUsage.InputTokens);
        Assert.Equal(original.TotalUsage.OutputTokens, restored.TotalUsage.OutputTokens);
        Assert.Equal(original.TotalCostUsd, restored.TotalCostUsd);
    }

    // ── AOT/trim-safety assertion ─────────────────────────────────────────────

    /// <summary>
    /// Asserts that serialization resolves through <see cref="LoopJsonContext.Default"/>
    /// and does not fall back to the reflection-based serializer.
    /// </summary>
    [Fact]
    public void LoopJsonContext_ResolvesTypesWithoutReflectionFallback()
    {
        // Each of these must return a non-null JsonTypeInfo — if the type is not
        // registered in the source-gen context the call throws NotSupportedException.
        Assert.NotNull(LoopJsonContext.Default.GoalSpec);
        Assert.NotNull(LoopJsonContext.Default.Continue);   // Verdict.Continue
        Assert.NotNull(LoopJsonContext.Default.Done);       // Verdict.Done
        Assert.NotNull(LoopJsonContext.Default.LoopOutcome);
        Assert.NotNull(LoopJsonContext.Default.GoalSetEvent);
        Assert.NotNull(LoopJsonContext.Default.TurnStartedEvent);
        Assert.NotNull(LoopJsonContext.Default.VerdictEvent);
        Assert.NotNull(LoopJsonContext.Default.LoopResultEvent);
        Assert.NotNull(LoopJsonContext.Default.LlmTokenUsage);
    }
}
