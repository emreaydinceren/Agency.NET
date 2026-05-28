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
/// Each session gets its own <see cref="Channel{T}"/> created with
/// <see cref="BoundedChannelFullMode.DropOldest"/> semantics and a capacity of
/// <see cref="DistillerOptions.PerSessionQueueCapacity"/> (default 32).
/// This implements the "one queue per session" invariant of Spec §5 and the
/// bounded channel behaviour of Spec §10.3.
/// </remarks>
internal sealed class ChannelSessionRegistry
{
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
    /// Gets the channel writer for the session, creating the channel if needed.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="sessionId">The session identifier.</param>
    /// <returns>The <see cref="ChannelWriter{T}"/> for the session.</returns>
    internal ChannelWriter<DistillationJob> GetOrCreateWriter(string userId, string sessionId) =>
        this.GetOrCreate(userId, sessionId).Writer;

    /// <summary>
    /// Gets a snapshot of all active session channels for consumption by background readers.
    /// </summary>
    /// <returns>A read-only view of the current session channels.</returns>
    internal IReadOnlyDictionary<string, Channel<DistillationJob>> GetAll() =>
        this._channels;

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

        // Wrap the channel to intercept drops.
        Channel<DistillationJob> ch = Channel.CreateBounded<DistillationJob>(options);

        this._logger.LogDebug(
            "Created bounded distillation channel for user {UserId} session {SessionId} (capacity={Capacity}).",
            userId, sessionId, this._capacity);

        return ch;
    }
}
