namespace Agency.Harness.Looping;

/// <summary>
/// Builds the system-prompt and user-message content for the Goalkeeper's cheap-model call.
/// Pure static functions — no I/O, no side effects.
/// </summary>
internal static class GoalkeeperPromptBuilder
{
    /// <summary>
    /// Tool names whose call/result round-trip is loop-control metadata, not evidence of task
    /// progress. A message describing or echoing the goal condition (e.g. the tool's own
    /// confirmation text) must never be shown to the judge as if it were transcript evidence —
    /// see the false-positive Done verdict this guards against (turn-0 self-referential echo).
    /// </summary>
    private static readonly HashSet<string> ControlPlaneToolNames =
        new(StringComparer.Ordinal) { "enable_goalkeeper", "disable_goalkeeper" };

    private const string ControlPlanePlaceholder =
        "(goalkeeper control action — armed/disarmed the goal; not evidence of task progress)";

    private const string DefaultRubric =
        "Be strict: only answer DONE when the transcript contains clear, explicit evidence " +
        "that the condition is satisfied. When in doubt, answer CONTINUE. " +
        "Text that merely describes or repeats the goal condition (e.g. a tool confirmation " +
        "message, or the assistant narrating that it is arming the goalkeeper) is NOT evidence " +
        "that the condition is satisfied — only treat the condition as met when it is " +
        "independently and substantively true elsewhere in the transcript.";

    /// <summary>
    /// Builds the system instruction that tells the cheap model how to respond.
    /// </summary>
    /// <param name="rubric">
    /// Optional extra instructions appended after the default strictness rubric
    /// (maps to <c>LoopOptions.GoalkeeperRubric</c>).
    /// </param>
    /// <returns>A system-prompt string to pass as <see cref="ChatOptions.Instructions"/>.</returns>
    internal static string BuildSystemPrompt(string? rubric = null)
    {
        string rubricSection = string.IsNullOrWhiteSpace(rubric)
            ? DefaultRubric
            : $"{DefaultRubric}\n\n{rubric.Trim()}";

        return $"""
            You are a strict, independent goal-checker (the "Goalkeeper").
            Your only job is to read a conversation transcript and decide whether a stated
            goal condition has been satisfied.

            RUBRIC
            {rubricSection}

            RESPONSE FORMAT — follow this exactly, no other text:

            VERDICT: done
            REASON: <one short sentence explaining why the condition is satisfied>

            — OR —

            VERDICT: continue
            REASON: <one short sentence explaining what is still missing>

            Use lower-case "done" or "continue" exactly as shown. Do not add any other text
            before or after the two lines.
            """;
    }

    /// <summary>
    /// Builds the user message that presents the condition and the transcript to the model.
    /// </summary>
    /// <param name="condition">The verifiable end-state from <see cref="GoalSpec.Condition"/>.</param>
    /// <param name="transcript">The conversation history produced by the worker so far.</param>
    /// <returns>A user-role <see cref="ChatMessage"/> ready to pass to the cheap client.</returns>
    internal static ChatMessage BuildUserMessage(
        string condition,
        IReadOnlyList<ChatMessage> transcript)
    {
        // Identify the call ids of enable_goalkeeper/disable_goalkeeper invocations so both
        // halves of the round-trip (the assistant's tool call — which may carry narration text
        // restating the condition — and the tool's result echo) can be excluded below.
        var controlPlaneCallIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (ChatMessage msg in transcript)
        {
            foreach (FunctionCallContent call in msg.Contents.OfType<FunctionCallContent>())
            {
                if (ControlPlaneToolNames.Contains(call.Name))
                {
                    controlPlaneCallIds.Add(call.CallId);
                }
            }
        }

        // Flatten the transcript into a readable block, keeping role labels.
        var sb = new System.Text.StringBuilder();
        foreach (ChatMessage msg in transcript)
        {
            bool isControlPlaneMessage =
                msg.Contents.OfType<FunctionCallContent>().Any(c => ControlPlaneToolNames.Contains(c.Name)) ||
                msg.Contents.OfType<FunctionResultContent>().Any(r => controlPlaneCallIds.Contains(r.CallId));

            bool isInstructionsMessage =
                msg.AdditionalProperties?.ContainsKey(global::Agency.Harness.Agent.InstructionsMessageMarkerKey) ?? false;

            if (isInstructionsMessage)
            {
                continue;
            }

            string role = msg.Role == ChatRole.Assistant ? "ASSISTANT" :
                          msg.Role == ChatRole.User ? "USER" : msg.Role.Value.ToUpperInvariant();
            sb.Append('[').Append(role).Append("] ");
            sb.AppendLine(isControlPlaneMessage ? ControlPlanePlaceholder : msg.Text ?? string.Empty);
        }

        string transcriptText = sb.ToString();

        string content =
            $"""
            GOAL CONDITION:
            {condition}

            TRANSCRIPT:
            {transcriptText}
            Check the transcript above against the goal condition and respond with your verdict.
            """;

        return new ChatMessage(ChatRole.User, content);
    }
}
