using Agency.Agentic;

namespace Agency.Memory.Distiller.Services;

/// <summary>
/// Provides read-only access to the <see cref="IConversationManager"/> for a given session.
/// </summary>
/// <remarks>
/// The Distiller reads conversation turns from the registry; the session owner is
/// responsible for registering and unregistering its conversation manager.
/// </remarks>
internal interface IConversationManagerRegistry
{
    /// <summary>
    /// Gets the <see cref="IConversationManager"/> for the given session, or
    /// <see langword="null"/> if the session is not registered.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <returns>The conversation manager, or <see langword="null"/>.</returns>
    IConversationManager? Get(string sessionId);

    /// <summary>
    /// Registers <paramref name="manager"/> for the given session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="manager">The conversation manager to register.</param>
    void Register(string sessionId, IConversationManager manager);

    /// <summary>
    /// Unregisters the conversation manager for the given session.
    /// </summary>
    /// <param name="sessionId">The session identifier to remove.</param>
    void Unregister(string sessionId);
}
