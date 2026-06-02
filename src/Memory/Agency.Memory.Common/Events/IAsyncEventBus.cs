namespace Agency.Memory.Common.Events;

/// <summary>
/// Minimal in-process event bus for publishing <see cref="Agency.Agentic.AgentEvent"/> instances
/// from background services. Subscribers register via <see cref="Subscribe{T}"/>.
/// </summary>
/// <remarks>
/// This is intentionally a thin, in-process bus backed by a
/// <see cref="System.Threading.Channels.Channel{T}"/>. It is not a distributed message broker.
/// </remarks>
public interface IAsyncEventBus
{
    /// <summary>
    /// Publishes <paramref name="evt"/> to all registered subscribers for <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The concrete event type, must derive from <see cref="Agency.Agentic.AgentEvent"/>.</typeparam>
    /// <param name="evt">The event instance to publish.</param>
    /// <param name="ct">Cancellation token.</param>
    Task PublishAsync<T>(T evt, CancellationToken ct = default) where T : Agency.Agentic.AgentEvent;

    /// <summary>
    /// Registers <paramref name="handler"/> as a subscriber for events of type <typeparamref name="T"/>.
    /// Returns a disposal registration to unsubscribe.
    /// </summary>
    /// <remarks>
    /// Dispatch is polymorphic: a handler subscribed to a base type (for example
    /// <c>DistillationSettledEvent</c>) is also invoked for published derived events
    /// (such as <c>DistillationCompletedEvent</c> or <c>DistillationFailedEvent</c>).
    /// </remarks>
    /// <typeparam name="T">The event type to subscribe to; may be a base type.</typeparam>
    /// <param name="handler">The async handler invoked for each matching published event.</param>
    /// <returns>A disposable that removes the subscription when disposed.</returns>
    IDisposable Subscribe<T>(Func<T, CancellationToken, Task> handler) where T : Agency.Agentic.AgentEvent;
}
