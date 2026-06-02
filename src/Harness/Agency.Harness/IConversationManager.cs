namespace Agency.Harness;

/// <summary>
/// Manages the ordered list of messages exchanged during an agent session.
/// The default implementation is <see cref="InMemoryConversationManager"/>;
/// future implementations may add sliding-window truncation or summarization.
/// </summary>
public interface IConversationManager
{
    /// <summary>Gets the current ordered list of messages.</summary>
    IReadOnlyList<ChatMessage> Messages { get; }

    /// <summary>Appends <paramref name="message"/> to the conversation.</summary>
    void Append(ChatMessage message);
}
