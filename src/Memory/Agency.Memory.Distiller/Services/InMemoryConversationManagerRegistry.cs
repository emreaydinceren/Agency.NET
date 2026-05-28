using System.Collections.Concurrent;
using Agency.Agentic;

namespace Agency.Memory.Distiller.Services;

/// <summary>
/// In-process <see cref="IConversationManagerRegistry"/> backed by a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
internal sealed class InMemoryConversationManagerRegistry : IConversationManagerRegistry
{
    private readonly ConcurrentDictionary<string, IConversationManager> _registry = new();

    /// <inheritdoc/>
    public IConversationManager? Get(string sessionId) =>
        this._registry.TryGetValue(sessionId, out IConversationManager? m) ? m : null;

    /// <inheritdoc/>
    public void Register(string sessionId, IConversationManager manager) =>
        this._registry[sessionId] = manager;

    /// <inheritdoc/>
    public void Unregister(string sessionId) =>
        this._registry.TryRemove(sessionId, out _);
}
