using System.Text.Json;
using Agency.Harness;
using dotacp.protocol;
using Newtonsoft.Json.Linq;

namespace Agency.Acp.Turns;

/// <summary>
/// Translates a harness <see cref="AgentEvent"/> into the ACP <see cref="SessionUpdate"/> it maps
/// to (spec §6.8), or <see langword="null"/> for an event with no ACP analogue
/// (<see cref="SessionStartedEvent"/>, <see cref="AssistantTurnEvent"/>) or one that
/// <see cref="TurnDriver"/> handles specially rather than forwarding as a plain update
/// (<see cref="PermissionRequestedEvent"/>, <see cref="AgentResultEvent"/>).
/// </summary>
internal static class EventTranslator
{
    /// <summary>
    /// Per-turn message ids for streamed text/thought chunks: a fresh id per assistant message so
    /// the client can tell one streamed message from the next (spec §6.8, <c>SessionUpdateAgentMessageChunk.MessageId</c>).
    /// </summary>
    public sealed class MessageIds
    {
        /// <summary>Gets the id shared by every <see cref="AssistantTextDeltaEvent"/> in the current assistant message.</summary>
        public MessageId TextId { get; private set; } = NewId();

        /// <summary>Gets the id shared by every <see cref="AssistantThoughtDeltaEvent"/> in the current assistant message.</summary>
        public MessageId ThoughtId { get; private set; } = NewId();

        /// <summary>Starts a new assistant message: subsequent deltas get fresh ids.</summary>
        public void StartNewMessage()
        {
            this.TextId = NewId();
            this.ThoughtId = NewId();
        }

        private static MessageId NewId() => Guid.NewGuid().ToString("n");
    }

    /// <summary>
    /// Maps <paramref name="evt"/> to the <see cref="SessionUpdate"/> <see cref="TurnDriver"/> should
    /// forward as a <c>session/update</c> notification, or <see langword="null"/> when the event has
    /// no such mapping.
    /// </summary>
    /// <param name="evt">The event to translate.</param>
    /// <param name="contextWindowSize">
    /// The session's configured context window size, used as <see cref="UsageUpdate.Size"/> for an
    /// <see cref="IterationCompletedEvent"/>; <see langword="null"/> renders as <c>0</c> (spec §6.8:
    /// "<c>Size</c> is <c>0</c> when unknown").
    /// </param>
    /// <param name="ids">
    /// The current turn's message ids, mutated (via <see cref="MessageIds.StartNewMessage"/>) when
    /// <paramref name="evt"/> is an <see cref="AssistantTurnEvent"/>.
    /// </param>
    public static SessionUpdate? Translate(AgentEvent evt, int? contextWindowSize, MessageIds ids)
    {
        ArgumentNullException.ThrowIfNull(evt);
        ArgumentNullException.ThrowIfNull(ids);

        switch (evt)
        {
            case AssistantTextDeltaEvent text:
                return new SessionUpdateAgentMessageChunk
                {
                    Content = new TextContent { Text = text.Text },
                    MessageId = ids.TextId,
                };

            case AssistantThoughtDeltaEvent thought:
                return new SessionUpdateAgentThoughtChunk
                {
                    Content = new TextContent { Text = thought.Text },
                    MessageId = ids.ThoughtId,
                };

            case ToolStartedEvent started:
                return new ToolCall
                {
                    ToolCallId = started.CallId,
                    Title = started.ToolName,
                    Kind = ToolKind.Other,
                    Status = ToolCallStatus.Pending,
                    RawInput = ToRawInput(started.Input),
                };

            case ToolInvokedEvent invoked:
                return new SessionUpdateToolCallUpdate
                {
                    ToolCallId = invoked.CallId,
                    Status = invoked.Result.IsError ? ToolCallStatus.Failed : ToolCallStatus.Completed,
                    RawOutput = invoked.Result.Content,
                };

            case IterationCompletedEvent iteration:
                return new UsageUpdate
                {
                    Size = (ulong)Math.Max(0, contextWindowSize ?? 0),
                    Used = (ulong)Math.Max(0, iteration.TurnUsage.InputTokens),
                };

            case AssistantTurnEvent:
                // Marks a completed assistant message: the next delta (if any) starts a new one.
                ids.StartNewMessage();
                return null;

            default:
                // SessionStartedEvent has no ACP analogue; PermissionRequestedEvent and
                // AgentResultEvent are handled specially by TurnDriver, never through this path.
                return null;
        }
    }

    private static JToken? ToRawInput(JsonElement input) =>
        input.ValueKind == JsonValueKind.Undefined ? null : JToken.Parse(input.GetRawText());
}
