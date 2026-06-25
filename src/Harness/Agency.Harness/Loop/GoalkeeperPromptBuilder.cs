namespace Agency.Harness.Loop;

/// <summary>
/// Builds the system-prompt and user-message content for the Goalkeeper's cheap-model call.
/// Pure static functions — no I/O, no side effects.
/// </summary>
internal static class GoalkeeperPromptBuilder
{
    private const string DefaultRubric =
        "Be strict: only answer DONE when the transcript contains clear, explicit evidence " +
        "that the condition is satisfied. When in doubt, answer CONTINUE.";

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
        // Flatten the transcript into a readable block, keeping role labels.
        var sb = new System.Text.StringBuilder();
        foreach (ChatMessage msg in transcript)
        {
            string role = msg.Role == ChatRole.Assistant ? "ASSISTANT" :
                          msg.Role == ChatRole.User ? "USER" : msg.Role.Value.ToUpperInvariant();
            sb.Append('[').Append(role).Append("] ");
            sb.AppendLine(msg.Text ?? string.Empty);
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
