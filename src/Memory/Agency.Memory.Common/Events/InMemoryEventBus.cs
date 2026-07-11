using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Agency.Memory.Common.Events;

/// <summary>
/// Simple in-process implementation of <see cref="IAsyncEventBus"/> using a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> of subscriber lists per event type.
/// </summary>
/// <remarks>
/// Suitable for single-process deployments. Exceptions in individual handlers are
/// caught, logged, and do not prevent other handlers from running.
/// </remarks>
internal sealed partial class InMemoryEventBus : IAsyncEventBus
{
    /// <summary>
    /// Subscriber lists keyed by the subscribed event type. Each handler is stored as a
    /// down-casting wrapper so publication can dispatch polymorphically to base-type subscribers.
    /// </summary>
    private readonly ConcurrentDictionary<Type, List<Func<Agency.Harness.AgentEvent, CancellationToken, Task>>> _handlers = new();
    private readonly ILogger<InMemoryEventBus> _logger;

    /// <summary>
    /// Initialises a new <see cref="InMemoryEventBus"/>.
    /// </summary>
    /// <param name="logger">Logger for handler exception reporting.</param>
    internal InMemoryEventBus(ILogger<InMemoryEventBus> logger)
    {
        this._logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task PublishAsync<T>(T evt, CancellationToken ct = default)
        where T : Agency.Harness.AgentEvent
    {
        ArgumentNullException.ThrowIfNull(evt);

        // Dispatch polymorphically against the runtime type so subscribers to a base event type
        // (e.g. DistillationSettledEvent) receive published derived events.
        Type runtimeType = evt.GetType();
        var snapshot = new List<Func<Agency.Harness.AgentEvent, CancellationToken, Task>>();

        foreach (KeyValuePair<Type, List<Func<Agency.Harness.AgentEvent, CancellationToken, Task>>> entry in this._handlers)
        {
            if (entry.Key.IsAssignableFrom(runtimeType))
            {
                lock (entry.Value)
                {
                    snapshot.AddRange(entry.Value);
                }
            }
        }

        foreach (Func<Agency.Harness.AgentEvent, CancellationToken, Task> h in snapshot)
        {
            try
            {
                await h(evt, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                this.LogEventHandlerThrew(ex, runtimeType.Name);
            }
        }
    }

    /// <inheritdoc/>
    public IDisposable Subscribe<T>(Func<T, CancellationToken, Task> handler)
        where T : Agency.Harness.AgentEvent
    {
        ArgumentNullException.ThrowIfNull(handler);

        // Wrap the typed handler so it can live in a single AgentEvent-keyed list and be invoked
        // with a down-cast event during polymorphic dispatch.
        Func<Agency.Harness.AgentEvent, CancellationToken, Task> wrapper = (evt, ct) => handler((T)evt, ct);

        List<Func<Agency.Harness.AgentEvent, CancellationToken, Task>> handlers =
            this._handlers.GetOrAdd(typeof(T), static _ => []);
        lock (handlers)
        {
            handlers.Add(wrapper);
        }

        return new Subscription(() =>
        {
            if (this._handlers.TryGetValue(typeof(T), out List<Func<Agency.Harness.AgentEvent, CancellationToken, Task>>? list))
            {
                lock (list)
                {
                    list.Remove(wrapper);
                }
            }
        });
    }

    /// <summary>Logs that a subscriber's event handler threw during dispatch.</summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "Event handler threw for event type {EventType}")]
    private partial void LogEventHandlerThrew(Exception ex, string eventType);

    private sealed class Subscription : IDisposable
    {
        private readonly Action _onDispose;
        private bool _disposed;

        internal Subscription(Action onDispose)
        {
            this._onDispose = onDispose;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (!this._disposed)
            {
                this._disposed = true;
                this._onDispose();
            }
        }
    }
}
