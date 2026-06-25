using Agency.Harness;
using Agency.Harness.Loop;

namespace Agency.Harness.Console.Test;

/// <summary>
/// T-CON-1: unit tests for loop-event rendering in <see cref="ConsoleChatSession"/>.
/// Each test injects a <see cref="TextWriterChatOutput"/> backed by a <see cref="StringWriter"/>
/// as the <see cref="IChatOutput"/> seam and asserts the expected text is produced.
/// Mirrors the pattern used in <see cref="ToolPreviewPanelRenderTests"/> and
/// <see cref="DumpContextSchemaRenderTests"/>.
/// </summary>
public sealed class LoopEventRenderTests
{
    // ── Seam helper ──────────────────────────────────────────────────────────

    private static (IChatOutput Output, StringWriter Writer) MakeOutput()
    {
        var sw = new StringWriter();
        return (new TextWriterChatOutput(sw), sw);
    }

    // ── GoalSetEvent ─────────────────────────────────────────────────────────

    /// <summary>
    /// A <see cref="GoalSetEvent"/> must render a banner that includes the goal
    /// condition text and the <see cref="GoalSpec.MaxTurns"/> cap.
    /// </summary>
    [Fact]
    public void GoalSetEvent_RendersConditionAndMaxTurns()
    {
        var (output, sw) = MakeOutput();
        var evt = new GoalSetEvent(new GoalSpec { Condition = "build passes", MaxTurns = 5 });

        ConsoleChatSession.RenderLoopEvent(output, evt);

        string rendered = sw.ToString();
        Assert.Contains("build passes", rendered);
        Assert.Contains("5", rendered);
    }

    // ── VerdictEvent (Continue) ───────────────────────────────────────────────

    /// <summary>
    /// A <see cref="VerdictEvent"/> with a <see cref="Verdict.Continue"/> verdict must
    /// render the word "continue" (case-insensitive) and the reason text.
    /// </summary>
    [Fact]
    public void VerdictEvent_Continue_RendersVerdictKindAndReason()
    {
        var (output, sw) = MakeOutput();
        var evt = new VerdictEvent(TurnIndex: 0, Verdict: new Verdict.Continue("tests still failing"));

        ConsoleChatSession.RenderLoopEvent(output, evt);

        string rendered = sw.ToString();
        Assert.Contains("continue", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tests still failing", rendered);
    }

    // ── VerdictEvent (Done) ───────────────────────────────────────────────────

    /// <summary>
    /// A <see cref="VerdictEvent"/> with a <see cref="Verdict.Done"/> verdict must
    /// render the word "done" (case-insensitive) and the reason text.
    /// </summary>
    [Fact]
    public void VerdictEvent_Done_RendersVerdictKindAndReason()
    {
        var (output, sw) = MakeOutput();
        var evt = new VerdictEvent(TurnIndex: 1, Verdict: new Verdict.Done("all tests pass"));

        ConsoleChatSession.RenderLoopEvent(output, evt);

        string rendered = sw.ToString();
        Assert.Contains("done", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("all tests pass", rendered);
    }

    // ── LoopResultEvent ───────────────────────────────────────────────────────

    /// <summary>
    /// A <see cref="LoopResultEvent"/> must render the <see cref="LoopOutcome"/>
    /// and the total cost in USD.
    /// </summary>
    [Fact]
    public void LoopResultEvent_RendersOutcomeAndCost()
    {
        var (output, sw) = MakeOutput();
        var evt = new LoopResultEvent(
            LoopOutcome.Achieved,
            FinalText: "done!",
            TotalUsage: new LlmTokenUsage(100, 50),
            TotalCostUsd: 0.0012m);

        ConsoleChatSession.RenderLoopEvent(output, evt);

        string rendered = sw.ToString();
        Assert.Contains("Achieved", rendered);
        Assert.Contains("0.0012", rendered);
    }
}
