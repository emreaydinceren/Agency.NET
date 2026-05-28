using Agency.Agentic;
using Agency.Memory.Common.Events;

namespace Agency.Memory.Distiller.Test.Stubs;

/// <summary>In-memory stub for <see cref="IAsyncEventBus"/> that captures published events.</summary>
internal sealed class FakeEventBus : IAsyncEventBus
{
    /// <summary>Gets all events published to this bus.</summary>
    internal List<AgentEvent> Published { get; } = [];

    /// <inheritdoc/>
    public Task PublishAsync<T>(T evt, CancellationToken ct = default) where T : AgentEvent
    {
        this.Published.Add(evt);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public IDisposable Subscribe<T>(Func<T, CancellationToken, Task> handler) where T : AgentEvent =>
        new NoopDisposable();

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
