using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Agency.Memory.Common.Events;

/// <summary>
/// Simple in-process implementation of <see cref="IAsyncEventBus"/> using a
/// <see cref="ConcurrentDictionary"/> of subscriber lists per event type.
/// </summary>
/// <remarks>
/// Suitable for single-process deployments. Exceptions in individual handlers are
/// caught, logged, and do not prevent other handlers from running.
/// </remarks>
internal sealed class InMemoryEventBus : IAsyncEventBus
{
    private readonly ConcurrentDictionary<Type, List<object>> _handlers = new();
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
        where T : Agency.Agentic.AgentEvent
    {
        if (!this._handlers.TryGetValue(typeof(T), out List<object>? handlers))
        {
            return;
        }

        List<object> snapshot;
        lock (handlers)
        {
            snapshot = [.. handlers];
        }

        foreach (object h in snapshot)
        {
            try
            {
                await ((Func<T, CancellationToken, Task>)h)(evt, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                this._logger.LogError(ex, "Event handler threw for event type {EventType}", typeof(T).Name);
            }
        }
    }

    /// <inheritdoc/>
    public IDisposable Subscribe<T>(Func<T, CancellationToken, Task> handler)
        where T : Agency.Agentic.AgentEvent
    {
        List<object> handlers = this._handlers.GetOrAdd(typeof(T), _ => []);
        lock (handlers)
        {
            handlers.Add(handler);
        }

        return new Subscription(() =>
        {
            if (this._handlers.TryGetValue(typeof(T), out List<object>? list))
            {
                lock (list)
                {
                    list.Remove(handler);
                }
            }
        });
    }

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
