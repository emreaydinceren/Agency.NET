using System.Text.Json;

using Agency.Acp.Turns;
using Agency.Harness;
using Agency.Llm.Common.Tools;
using dotacp.protocol;

using AcpTextContent = dotacp.protocol.TextContent;

namespace Agency.Acp.Test.Events;

/// <summary>
/// One case per row of the <see cref="EventTranslator"/> mapping table (spec §6.8), plus the
/// occupancy-vs-cumulative usage semantics (spec §6.8, §14).
/// </summary>
public sealed class EventTranslatorTests
{
    private static readonly JsonElement EmptyInput = JsonDocument.Parse("{}").RootElement;

    // ── Task 9.1.t: each AgentEvent maps to its session/update ─────────────────

    /// <summary><see cref="AssistantTextDeltaEvent"/> maps to <see cref="SessionUpdateAgentMessageChunk"/>, carrying the current text message id.</summary>
    [Fact]
    public void Translate_AssistantTextDeltaEvent_MapsToAgentMessageChunk()
    {
        var ids = new EventTranslator.MessageIds();

        SessionUpdate? result = EventTranslator.Translate(new AssistantTextDeltaEvent("hello"), null, ids);

        var chunk = Assert.IsType<SessionUpdateAgentMessageChunk>(result);
        var content = Assert.IsType<AcpTextContent>(chunk.Content);
        Assert.Equal("hello", content.Text);
        Assert.Equal(ids.TextId, chunk.MessageId);
    }

    /// <summary><see cref="AssistantThoughtDeltaEvent"/> maps to <see cref="SessionUpdateAgentThoughtChunk"/>, carrying the current thought message id.</summary>
    [Fact]
    public void Translate_AssistantThoughtDeltaEvent_MapsToAgentThoughtChunk()
    {
        var ids = new EventTranslator.MessageIds();

        SessionUpdate? result = EventTranslator.Translate(new AssistantThoughtDeltaEvent("thinking"), null, ids);

        var chunk = Assert.IsType<SessionUpdateAgentThoughtChunk>(result);
        var content = Assert.IsType<AcpTextContent>(chunk.Content);
        Assert.Equal("thinking", content.Text);
        Assert.Equal(ids.ThoughtId, chunk.MessageId);
    }

    /// <summary><see cref="ToolStartedEvent"/> maps to a pending <see cref="ToolCall"/>, with the tool name passed through verbatim (spec §6.5: no <c>mcp__server__tool</c> minting) and no <see cref="ToolKind"/> classification applied (spec §6.8).</summary>
    [Fact]
    public void Translate_ToolStartedEvent_MapsToPendingToolCall_NameUnmodified_NoKindClassification()
    {
        var ids = new EventTranslator.MessageIds();
        var evt = new ToolStartedEvent("call-1", "mcp__server__tool", EmptyInput);

        SessionUpdate? result = EventTranslator.Translate(evt, null, ids);

        var toolCall = Assert.IsType<ToolCall>(result);
        Assert.Equal("call-1", (string)toolCall.ToolCallId);
        Assert.Equal(ToolCallStatus.Pending, toolCall.Status);

        // The name must travel unmodified: no prefix minted, no suffix stripped.
        Assert.Equal("mcp__server__tool", toolCall.Title);

        // Spec §6.8 forbids ToolKind classification: the harness/adapter must not guess a kind
        // from the tool name or input, so every tool call carries the same unclassified value.
        Assert.Equal(ToolKind.Other, toolCall.Kind);
    }

    /// <summary><see cref="ToolInvokedEvent"/> maps to a <see cref="SessionUpdateToolCallUpdate"/> correlated by <see cref="ToolInvokedEvent.CallId"/>, with a successful result mapping to <see cref="ToolCallStatus.Completed"/>.</summary>
    [Fact]
    public void Translate_ToolInvokedEvent_Success_MapsToCompletedToolCallUpdate_CorrelatedByCallId()
    {
        var ids = new EventTranslator.MessageIds();
        var evt = new ToolInvokedEvent("toolA", EmptyInput, new ToolResult("ok")) { CallId = "call-42" };

        SessionUpdate? result = EventTranslator.Translate(evt, null, ids);

        var update = Assert.IsType<SessionUpdateToolCallUpdate>(result);
        Assert.Equal("call-42", (string)update.ToolCallId);
        Assert.Equal(ToolCallStatus.Completed, update.Status);
    }

    /// <summary>A failed <see cref="ToolInvokedEvent"/> (<see cref="ToolResult.IsError"/> true) maps to <see cref="ToolCallStatus.Failed"/>.</summary>
    [Fact]
    public void Translate_ToolInvokedEvent_Error_MapsToFailedToolCallUpdate()
    {
        var ids = new EventTranslator.MessageIds();
        var evt = new ToolInvokedEvent("toolA", EmptyInput, new ToolResult("boom", IsError: true)) { CallId = "call-42" };

        SessionUpdate? result = EventTranslator.Translate(evt, null, ids);

        var update = Assert.IsType<SessionUpdateToolCallUpdate>(result);
        Assert.Equal(ToolCallStatus.Failed, update.Status);
    }

    /// <summary><see cref="SessionStartedEvent"/> has no ACP analogue and is swallowed (spec §6.8).</summary>
    [Fact]
    public void Translate_SessionStartedEvent_EmitsNothing()
    {
        var ids = new EventTranslator.MessageIds();

        SessionUpdate? result = EventTranslator.Translate(new SessionStartedEvent("session-1"), null, ids);

        Assert.Null(result);
    }

    // ── Task 9.2.t: usage is occupancy, not cumulative (spec §6.8, §14) ─────────

    /// <summary>
    /// <see cref="UsageUpdate.Used"/> tracks the latest iteration's input token count, not a running
    /// total: three iterations with decreasing input counts must yield decreasing <see cref="UsageUpdate.Used"/>
    /// values. A cumulative implementation would instead produce a non-decreasing sequence.
    /// </summary>
    [Fact]
    public void Translate_IterationCompletedEvent_UsedTracksLatestInputTokens_NotCumulative()
    {
        var ids = new EventTranslator.MessageIds();

        ulong Used(long inputTokens)
        {
            var evt = new IterationCompletedEvent(1, new LlmTokenUsage(inputTokens, 5), TimeSpan.Zero);
            var update = Assert.IsType<UsageUpdate>(EventTranslator.Translate(evt, 1000, ids));
            return update.Used;
        }

        ulong first = Used(900);
        ulong second = Used(500);
        ulong third = Used(100);

        Assert.Equal(900UL, first);
        Assert.Equal(500UL, second);
        Assert.Equal(100UL, third);
        Assert.True(second < first);
        Assert.True(third < second);
    }

    /// <summary><see cref="UsageUpdate.Size"/> equals the session's configured context window size.</summary>
    [Fact]
    public void Translate_IterationCompletedEvent_SizeEqualsContextWindowSize()
    {
        var ids = new EventTranslator.MessageIds();
        var evt = new IterationCompletedEvent(1, new LlmTokenUsage(50, 5), TimeSpan.Zero);

        var update = Assert.IsType<UsageUpdate>(EventTranslator.Translate(evt, 4096, ids));

        Assert.Equal(4096UL, update.Size);
    }

    /// <summary>
    /// When the context window size is unknown (<see langword="null"/>), <see cref="UsageUpdate.Size"/>
    /// is <c>0</c> but the update is still emitted — spec §6.8 requires <see cref="UsageUpdate.Used"/>
    /// unconditionally, regardless of whether the window size is known.
    /// </summary>
    [Fact]
    public void Translate_IterationCompletedEvent_UnknownContextWindowSize_SizeIsZero_UpdateStillEmitted()
    {
        var ids = new EventTranslator.MessageIds();
        var evt = new IterationCompletedEvent(1, new LlmTokenUsage(50, 5), TimeSpan.Zero);

        SessionUpdate? result = EventTranslator.Translate(evt, null, ids);

        var update = Assert.IsType<UsageUpdate>(result);
        Assert.Equal(0UL, update.Size);
        Assert.Equal(50UL, update.Used);
    }
}
