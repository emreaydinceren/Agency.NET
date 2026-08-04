using Agency.Harness.Looping;

namespace Agency.Harness.Test.Looping;

/// <summary>
/// Unit tests for <see cref="GoalkeeperPromptBuilder"/>, specifically the fix for the
/// turn-0 false-positive Done verdict: <c>enable_goalkeeper</c>/<c>disable_goalkeeper</c>
/// tool call+result messages echo the literal goal condition text into the transcript
/// (either via the tool's own confirmation, or via assistant narration accompanying the
/// tool call), which a judge model can mistake for satisfying evidence.
/// </summary>
public sealed class GoalkeeperPromptBuilderTests
{
    private const string Condition = "the most recent assistant message is exactly the text COUNTER_3_DONE";
    private const string TargetText = "COUNTER_3_DONE";

    /// <summary>Builds the assistant tool-call + tool-result pair produced by arming the goalkeeper.</summary>
    private static (ChatMessage Call, ChatMessage Result) MakeGoalkeeperArmRoundTrip(string callId = "call-1") =>
        (
            new ChatMessage(ChatRole.Assistant,
            [
                new TextContent($"I'll arm the goalkeeper now with condition: {Condition}"),
                new FunctionCallContent(callId, "enable_goalkeeper",
                    new Dictionary<string, object?> { ["condition"] = Condition }),
            ]),
            new ChatMessage(ChatRole.Tool,
            [
                new FunctionResultContent(callId, $"Goalkeeper armed. Condition: {Condition}"),
            ])
        );

    // ── Red/green: control-plane echo must not be presented as evidence ──────────────

    /// <summary>
    /// Reproduces the false-positive root cause: a transcript containing only the
    /// enable_goalkeeper tool call/result round-trip (which echoes the literal target text)
    /// and NO genuine assistant completion must not present that echo as evidence — the
    /// literal target text must appear only in the GOAL CONDITION section, not the transcript.
    /// </summary>
    [Fact]
    public void BuildUserMessage_WhenTranscriptOnlyHasGoalkeeperArmEcho_RedactsControlPlaneRoundTrip()
    {
        (ChatMessage call, ChatMessage result) = MakeGoalkeeperArmRoundTrip();
        IReadOnlyList<ChatMessage> transcript = [call, result];

        ChatMessage userMessage = GoalkeeperPromptBuilder.BuildUserMessage(Condition, transcript);
        string content = userMessage.Text;

        // The target text must appear exactly once — inside "GOAL CONDITION:" — and never
        // again inside the flattened TRANSCRIPT section (the tool round-trip is redacted).
        int occurrences = CountOccurrences(content, TargetText);
        Assert.Equal(1, occurrences);

        string transcriptSection = content[content.IndexOf("TRANSCRIPT:", StringComparison.Ordinal)..];
        Assert.DoesNotContain(TargetText, transcriptSection);
        Assert.Contains("not evidence of task progress", transcriptSection);
    }

    /// <summary>Companion coverage for the disable_goalkeeper tool — same redaction applies.</summary>
    [Fact]
    public void BuildUserMessage_WhenTranscriptHasDisableGoalkeeperEcho_RedactsIt()
    {
        var call = new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent("call-2", "disable_goalkeeper", new Dictionary<string, object?>()),
        ]);
        var result = new ChatMessage(ChatRole.Tool,
        [
            new FunctionResultContent("call-2", "Goalkeeper disarmed."),
        ]);
        IReadOnlyList<ChatMessage> transcript = [call, result];

        ChatMessage userMessage = GoalkeeperPromptBuilder.BuildUserMessage("some condition", transcript);

        Assert.DoesNotContain("Goalkeeper disarmed.", userMessage.Text);
    }

    // ── Regression: genuine completion evidence must remain visible ──────────────────

    /// <summary>
    /// Guards against an overzealous fix: a genuine assistant completion message (produced on
    /// a later turn, unrelated to the goalkeeper tool call) that actually satisfies the
    /// condition must still be visible to the judge, alongside the redacted control-plane echo.
    /// </summary>
    [Fact]
    public void BuildUserMessage_WhenGenuineAssistantMessageSatisfiesCondition_KeepsItVisible()
    {
        (ChatMessage call, ChatMessage result) = MakeGoalkeeperArmRoundTrip();
        var genuineCompletion = new ChatMessage(ChatRole.Assistant, TargetText);
        IReadOnlyList<ChatMessage> transcript = [call, result, genuineCompletion];

        ChatMessage userMessage = GoalkeeperPromptBuilder.BuildUserMessage(Condition, transcript);
        string content = userMessage.Text;

        // Appears once in GOAL CONDITION, once in the genuine completion line — the
        // control-plane echo contributes zero further occurrences.
        Assert.Equal(2, CountOccurrences(content, TargetText));

        string transcriptSection = content[content.IndexOf("TRANSCRIPT:", StringComparison.Ordinal)..];
        Assert.Contains($"[ASSISTANT] {TargetText}", transcriptSection);
    }

    /// <summary>
    /// Non-goalkeeper tool calls (e.g. build/test commands relied on by skills like
    /// refactor-loop) must not be redacted — only the enable_goalkeeper/disable_goalkeeper
    /// round-trip is affected. Coverage is via the assistant-visible text that accompanies
    /// such a call: <see cref="ChatMessage.Text"/> only surfaces <see cref="TextContent"/>
    /// (tool result content is never flattened into it, by design of the underlying
    /// Microsoft.Extensions.AI library — unrelated to this fix), so this asserts that an
    /// unrelated tool call's assistant-authored narration text survives untouched.
    /// </summary>
    [Fact]
    public void BuildUserMessage_WhenTranscriptHasUnrelatedToolCall_KeepsNarrationVisible()
    {
        var call = new ChatMessage(ChatRole.Assistant,
        [
            new TextContent("Running the test suite now."),
            new FunctionCallContent("call-3", "run_tests", new Dictionary<string, object?>()),
        ]);
        IReadOnlyList<ChatMessage> transcript = [call];

        ChatMessage userMessage = GoalkeeperPromptBuilder.BuildUserMessage("all tests pass", transcript);

        Assert.Contains("Running the test suite now.", userMessage.Text);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
