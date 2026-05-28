using System.Collections.Concurrent;
using Agency.Agentic;
using Agency.Memory.Distiller.Services;

namespace Agency.Memory.Distiller.Test.Stubs;

/// <summary>In-memory stub for <see cref="IConversationManagerRegistry"/>.</summary>
internal sealed class FakeConversationManagerRegistry : IConversationManagerRegistry
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
