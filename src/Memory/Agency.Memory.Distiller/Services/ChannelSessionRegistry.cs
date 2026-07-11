using System.Collections.Concurrent;
using System.Threading.Channels;
using Agency.Memory.Common.Jobs;
using Agency.Memory.Common.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agency.Memory.Distiller.Services;

/// <summary>
/// Maintains per-session bounded <see cref="Channel{T}"/> instances for
/// <see cref="DistillationJob"/> messages.
/// </summary>
/// <remarks>
/// <para>
/// Each session gets its own <see cref="Channel{T}"/> created with
/// <see cref="BoundedChannelFullMode.DropOldest"/> semantics and a capacity of
/// <see cref="DistillerOptions.PerSessionQueueCapacity"/> (default 32).
/// This implements the "one queue per session" invariant of Spec §5 and the
/// bounded channel behaviour of Spec §10.3.
/// </para>
/// <para>
/// <b>Design deviation (§6.2/§10.2):</b> the spec describes a single MPSC
/// <see cref="Channel{T}"/> consumed by a single <c>await foreach</c>. The
/// implementation instead uses one bounded channel per session to get
/// per-session <see cref="BoundedChannelFullMode.DropOldest"/> backpressure
/// cheaply. The per-session channels are consumed by a single
/// <see cref="DistillerBackgroundService"/> loop; when all session channels are
/// empty the loop suspends on <see cref="WaitForWorkAsync"/> — a
/// <see cref="SemaphoreSlim"/> that any writer releases on every successful
/// enqueue — so there is no busy-polling delay between enqueue and pickup.
/// </para>
/// </remarks>
internal sealed partial class ChannelSessionRegistry : IDisposable
{
    // Released by NotifyingChannelWriter on every successful TryWrite / WriteAsync
    // so the background consumer wakes immediately instead of busy-polling.
    private readonly SemaphoreSlim _workSignal = new(0);

    private readonly ConcurrentDictionary<string, Channel<DistillationJob>> _channels = new();
    private readonly int _capacity;
    private readonly ILogger<ChannelSessionRegistry> _logger;

    /// <summary>
    /// Initialises a new <see cref="ChannelSessionRegistry"/>.
    /// </summary>
    /// <param name="options">Distiller options providing channel capacity.</param>
    /// <param name="logger">Structured logger for drop-oldest warnings.</param>
    internal ChannelSessionRegistry(IOptions<DistillerOptions> options, ILogger<ChannelSessionRegistry> logger)
    {
        this._capacity = options?.Value.PerSessionQueueCapacity ?? 32;
        this._logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets the existing channel for the session, or creates a new bounded one.
    /// </summary>
    /// <param name="userId">The user id (used in log messages only; key is <paramref name="sessionId"/>).</param>
    /// <param name="sessionId">The session identifier.</param>
    /// <returns>The channel for the session.</returns>
    internal Channel<DistillationJob> GetOrCreate(string userId, string sessionId)
    {
        return this._channels.GetOrAdd(sessionId, _ => CreateChannel(userId, sessionId));
    }

    /// <summary>
    /// Gets a notifying channel writer for the session, creating the channel if needed.
    /// </summary>
    /// <remarks>
    /// The returned writer releases <see cref="WaitForWorkAsync"/>'s semaphore on
    /// every successful write so the background consumer wakes immediately.
    /// </remarks>
    /// <param name="userId">The user id.</param>
    /// <param name="sessionId">The session identifier.</param>
    /// <returns>A <see cref="ChannelWriter{T}"/> that signals the work semaphore on write.</returns>
    internal ChannelWriter<DistillationJob> GetOrCreateWriter(string userId, string sessionId) =>
        new NotifyingChannelWriter(this.GetOrCreate(userId, sessionId).Writer, this._workSignal);

    /// <summary>
    /// Gets a snapshot of all active session channels for consumption by background readers.
    /// </summary>
    /// <returns>A read-only view of the current session channels.</returns>
    internal IReadOnlyDictionary<string, Channel<DistillationJob>> GetAll() =>
        this._channels;

    /// <summary>
    /// Suspends the caller until at least one <see cref="DistillationJob"/> has been enqueued
    /// via any writer returned by <see cref="GetOrCreateWriter"/>, or until
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="cancellationToken">Token that cancels the wait.</param>
    internal Task WaitForWorkAsync(CancellationToken cancellationToken) =>
        this._workSignal.WaitAsync(cancellationToken);

    /// <summary>
    /// Removes the channel for the given session and completes its writer.
    /// </summary>
    /// <param name="sessionId">The session to remove.</param>
    internal void Remove(string sessionId)
    {
        if (this._channels.TryRemove(sessionId, out Channel<DistillationJob>? ch))
        {
            ch.Writer.TryComplete();
        }
    }

    private Channel<DistillationJob> CreateChannel(string userId, string sessionId)
    {
        var options = new BoundedChannelOptions(this._capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        };

        Channel<DistillationJob> ch = Channel.CreateBounded<DistillationJob>(options);

        this.LogChannelCreated(userId, sessionId, this._capacity);

        return ch;
    }

    /// <summary>Disposes the work-signal semaphore.</summary>
    public void Dispose()
    {
        this._workSignal.Dispose();
    }

    /// <summary>Logs that a bounded per-session distillation channel was created.</summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "Created bounded distillation channel for user {UserId} session {SessionId} (capacity={Capacity}).")]
    private partial void LogChannelCreated(string userId, string sessionId, int capacity);

    /// <summary>
    /// Wraps a <see cref="ChannelWriter{T}"/> and releases a <see cref="SemaphoreSlim"/>
    /// after every successful write so the background consumer wakes immediately.
    /// </summary>
    private sealed class NotifyingChannelWriter(
        ChannelWriter<DistillationJob> inner,
        SemaphoreSlim signal) : ChannelWriter<DistillationJob>
    {
        /// <inheritdoc/>
        public override bool TryWrite(DistillationJob item)
        {
            bool written = inner.TryWrite(item);
            if (written)
            {
                signal.Release();
            }

            return written;
        }

        /// <inheritdoc/>
        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
            inner.WaitToWriteAsync(cancellationToken);

        /// <inheritdoc/>
        public override async ValueTask WriteAsync(DistillationJob item, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            signal.Release();
        }

        /// <inheritdoc/>
        public override bool TryComplete(Exception? error = null) =>
            inner.TryComplete(error);
    }
}
