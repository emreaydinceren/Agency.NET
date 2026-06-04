using System.Collections.Concurrent;
using System.Threading.Channels;
using Agency.Harness.Contexts;
using Agency.Memory.Common.Jobs;
using Agency.Memory.Common.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agency.Memory.Distiller.Services;

/// <summary>
/// Manages per-session inactivity timers that enqueue a <see cref="DistillationJob"/>
/// with <see cref="DistillationTrigger.Inactivity"/> when a session has been idle
/// for longer than <see cref="DistillerOptions.InactivityTimeout"/> (Spec §10).
/// </summary>
/// <remarks>
/// Singleton; state is per session via a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// Uses <see cref="TimeProvider"/> for testability with
/// <c>Microsoft.Extensions.TimeProvider.Testing.FakeTimeProvider</c>.
/// </remarks>
internal sealed class InactivityTimerService : IHostedService, IDisposable
{
    private readonly ConcurrentDictionary<string, SessionTimerState> _timers = new();
    private readonly ChannelSessionRegistry _channelRegistry;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _timeout;
    private readonly ILogger<InactivityTimerService> _logger;

    /// <summary>
    /// Initialises a new <see cref="InactivityTimerService"/>.
    /// </summary>
    /// <param name="channelRegistry">Registry that resolves per-session channel writers.</param>
    /// <param name="options">Distiller options providing the inactivity timeout.</param>
    /// <param name="timeProvider">Time provider for testable delay behaviour.</param>
    /// <param name="logger">Structured logger.</param>
    internal InactivityTimerService(
        ChannelSessionRegistry channelRegistry,
        IOptions<DistillerOptions> options,
        TimeProvider timeProvider,
        ILogger<InactivityTimerService> logger)
    {
        this._channelRegistry = channelRegistry ?? throw new ArgumentNullException(nameof(channelRegistry));
        this._timeout = options?.Value.InactivityTimeout ?? TimeSpan.FromMinutes(5);
        this._timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this._logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        this.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Restarts the inactivity timer for the given session. If no timer exists, starts one.
    /// </summary>
    /// <param name="userId">The owning user.</param>
    /// <param name="sessionId">The session to restart the timer for.</param>
    /// <param name="currentTurnIndex">The most recent turn index at restart time.</param>
    /// <param name="focus">Snapshot of the session focus at restart time; stored so the enqueued job carries the correct focus (Spec §6.7.1 / P2).</param>
    internal void Restart(string userId, string sessionId, int currentTurnIndex, FocusContext? focus = null)
    {
        // Dispose any existing timer for this session and create a fresh one.
        if (this._timers.TryGetValue(sessionId, out SessionTimerState? existing))
        {
            existing.Dispose();
        }

        var state = new SessionTimerState(userId, sessionId, currentTurnIndex, focus);
        this._timers[sessionId] = state;

        var timer = this._timeProvider.CreateTimer(
            callback: _ => this.OnTimerExpired(sessionId),
            state: null,
            dueTime: this._timeout,
            period: Timeout.InfiniteTimeSpan);

        state.SetTimer(timer);
    }

    /// <summary>
    /// Stops and removes the timer for the given session. Called on session disposal.
    /// </summary>
    /// <param name="sessionId">The session whose timer to stop.</param>
    internal void Stop(string sessionId)
    {
        if (this._timers.TryRemove(sessionId, out SessionTimerState? state))
        {
            state.Dispose();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (SessionTimerState state in this._timers.Values)
        {
            state.Dispose();
        }

        this._timers.Clear();
    }

    private void OnTimerExpired(string sessionId)
    {
        if (!this._timers.TryRemove(sessionId, out SessionTimerState? state))
        {
            return;
        }

        state.Dispose();

        this._logger.LogInformation(
            "Inactivity timer expired for session {SessionId}. Enqueuing distillation job.",
            sessionId);

        ChannelWriter<DistillationJob> writer = this._channelRegistry.GetOrCreateWriter(
            state.UserId, sessionId);

        var job = new DistillationJob(
            UserId: state.UserId,
            SessionId: sessionId,
            Trigger: DistillationTrigger.Inactivity,
            UpToTurnIndex: state.TurnIndex,
            Focus: state.Focus);

        if (!writer.TryWrite(job))
        {
            this._logger.LogWarning(
                "Inactivity timer: channel full or closed for session {SessionId}. Job dropped.",
                sessionId);
        }
    }

    private sealed class SessionTimerState : IDisposable
    {
        private ITimer? _timer;
        private int _disposed;

        internal string UserId { get; }
        internal string SessionId { get; }
        internal int TurnIndex { get; }

        /// <summary>Gets the session focus snapshot taken at timer-restart time.</summary>
        internal FocusContext? Focus { get; }

        internal SessionTimerState(string userId, string sessionId, int turnIndex, FocusContext? focus = null)
        {
            this.UserId = userId;
            this.SessionId = sessionId;
            this.TurnIndex = turnIndex;
            this.Focus = focus;
        }

        internal void SetTimer(ITimer timer)
        {
            this._timer = timer;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this._disposed, 1) == 0)
            {
                this._timer?.Dispose();
            }
        }
    }
}
