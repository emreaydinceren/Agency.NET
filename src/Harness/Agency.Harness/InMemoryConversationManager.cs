namespace Agency.Harness;

/// <summary>
/// Simple in-memory <see cref="IConversationManager"/> backed by a <see cref="List{T}"/>.
/// The <see cref="Messages"/> property returns the live list as a read-only view,
/// so any reference captured before an <see cref="Append"/> call reflects subsequent additions.
/// </summary>
public sealed class InMemoryConversationManager : IConversationManager
{
    private readonly List<ChatMessage> _messages = [];

    /// <inheritdoc/>
    public IReadOnlyList<ChatMessage> Messages => this._messages;

    /// <inheritdoc/>
    public void Append(ChatMessage message) => this._messages.Add(message);
}
