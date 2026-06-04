using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Agency.Memory.Common.Events;
using Agency.Memory.Common.Jobs;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agency.Memory.Consolidator.Services;

/// <summary>
/// Background service that listens for <see cref="ConsolidationJob"/>s and runs a
/// consolidator sub-agent for each affected user (Spec §6.3 / §10.2).
/// </summary>
/// <remarks>
/// Per-user serial execution: at most one consolidation pass runs per user at any time.
/// Multiple triggers for the same user while one is in-flight are coalesced into one
/// pending re-run via a <see cref="ConcurrentDictionary{TKey,TValue}"/> pending flag.
///
/// The sub-agent runner is injected as a delegate so tests can stub it without requiring
/// a real LLM. In production, <see cref="ConsolidatorSubAgentFactory.CreateRunner"/> provides
/// the real implementation.
/// </remarks>
internal sealed class ConsolidatorBackgroundService : BackgroundService, IConsolidationTrigger
{
    internal const string ActivitySourceName = "Agency.Memory.Consolidator";
    internal const string MeterName = "Agency.Memory.Consolidator";

    /// <summary>Max records before a warning is emitted (Spec §6.3 V1 scale guard).</summary>
    internal const int MaxRecordsPerPass = 500;

    private static readonly ActivitySource _activitySource = new(ActivitySourceName);
    private static readonly Meter _meter = new(MeterName);

    private static readonly Counter<long> _jobCounter =
        _meter.CreateCounter<long>("memory.consolidator.jobs", description: "Total consolidation jobs processed");

    private static readonly Counter<long> _errorCounter =
        _meter.CreateCounter<long>("memory.consolidator.errors", description: "Consolidation failures");

    private readonly IMemoryStore _store;

    /// <summary>
    /// The agent runner delegate.
    /// Signature: <c>(userId, records, ct) => Task&lt;(int Merges, int Updates, int Deletes)&gt;</c>.
    /// Runs the sub-agent and returns the mutation tallies when the agent terminates.
    /// </summary>
    private readonly Func<string, IReadOnlyList<Record>, CancellationToken, Task<(int Merges, int Updates, int Deletes)>>? _agentRunner;

    private readonly IAsyncEventBus _eventBus;
    private readonly IOptions<ConsolidatorOptions> _options;
    private readonly ILogger<ConsolidatorBackgroundService> _logger;

    /// <summary>
    /// Tracks users currently running a consolidation pass.
    /// Value = true means a second trigger arrived while running; it will be run after.
    /// Value = false means the user is running with no pending re-run.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _inFlight = new();

    /// <summary>
    /// Queue of jobs waiting to be processed by the background loop.
    /// </summary>
    private readonly System.Threading.Channels.Channel<ConsolidationJob> _channel =
        System.Threading.Channels.Channel.CreateUnbounded<ConsolidationJob>();

    /// <summary>
    /// Subscription to <see cref="DistillationCompletedEvent"/> from the event bus.
    /// </summary>
    private IDisposable? _subscription;

    /// <summary>
    /// Initialises a new <see cref="ConsolidatorBackgroundService"/>.
    /// </summary>
    /// <param name="store">The memory store to load records from.</param>
    /// <param name="agentRunner">
    /// Delegate that executes the consolidator sub-agent for a user and returns mutation tallies.
    /// May be <see langword="null"/> in tests that verify empty-store behaviour.
    /// </param>
    /// <param name="eventBus">The in-process event bus for subscribing and publishing.</param>
    /// <param name="options">Consolidator configuration options.</param>
    /// <param name="logger">Logger.</param>
    internal ConsolidatorBackgroundService(
        IMemoryStore store,
        Func<string, IReadOnlyList<Record>, CancellationToken, Task<(int Merges, int Updates, int Deletes)>>? agentRunner,
        IAsyncEventBus eventBus,
        IOptions<ConsolidatorOptions> options,
        ILogger<ConsolidatorBackgroundService> logger)
    {
        this._store = store ?? throw new ArgumentNullException(nameof(store));
        this._agentRunner = agentRunner;
        this._eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        this._options = options ?? throw new ArgumentNullException(nameof(options));
        this._logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Only auto-subscribe when trigger mode is OnSessionEnd.
        // Manual mode requires the host to call IConsolidationTrigger.RequestAsync explicitly.
        if (this._options.Value.Trigger == ConsolidationTrigger.OnSessionEnd)
        {
            this._subscription = this._eventBus.Subscribe<DistillationCompletedEvent>(
                async (evt, ct) =>
                {
                    var job = new ConsolidationJob(evt.UserId, evt.SessionId);
                    await this._channel.Writer.WriteAsync(job, ct).ConfigureAwait(false);
                    this._logger.LogDebug(
                        "Enqueued ConsolidationJob for UserId={UserId} from session {SessionId}",
                        evt.UserId, evt.SessionId);
                });
        }

        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task RequestAsync(string userId, CancellationToken ct = default)
    {
        var job = new ConsolidationJob(userId, string.Empty);
        await this._channel.Writer.WriteAsync(job, ct).ConfigureAwait(false);
        this._logger.LogDebug("Manual ConsolidationJob enqueued for UserId={UserId}", userId);
    }

    /// <inheritdoc/>
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        this._subscription?.Dispose();
        this._channel.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        this._logger.LogInformation("ConsolidatorBackgroundService started.");

        await foreach (ConsolidationJob job in this._channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            // Per-user serialization: skip enqueueing if already in-flight; mark pending instead.
            if (!this._inFlight.TryAdd(job.UserId, false))
            {
                // Already in-flight — mark as pending-re-run.
                this._inFlight[job.UserId] = true;
                this._logger.LogDebug(
                    "Consolidation already in-flight for UserId={UserId}; marking pending.", job.UserId);
                continue;
            }

            try
            {
                await this.ProcessJobAsync(job, stoppingToken).ConfigureAwait(false);
            }
            finally
            {
                // Check if a pending re-run was flagged.
                if (this._inFlight.TryGetValue(job.UserId, out bool pending) && pending)
                {
                    // Re-queue a job for the same user.
                    this._inFlight[job.UserId] = false;
                    var rerunJob = new ConsolidationJob(job.UserId, job.TriggeredBySessionId);
                    await this._channel.Writer.WriteAsync(rerunJob, stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    this._inFlight.TryRemove(job.UserId, out _);
                }
            }
        }

        this._logger.LogInformation("ConsolidatorBackgroundService stopping.");
    }

    /// <summary>
    /// Processes a single <see cref="ConsolidationJob"/>: loads records, runs the sub-agent, emits event.
    /// </summary>
    /// <param name="job">The job to process.</param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task ProcessJobAsync(ConsolidationJob job, CancellationToken ct)
    {
        using Activity? activity = _activitySource.StartActivity("memory.consolidate");
        activity?.SetTag("memory.user_id", job.UserId);
        activity?.SetTag("memory.session_id", job.TriggeredBySessionId);

        _jobCounter.Add(1);

        this._logger.LogInformation(
            "Starting consolidation for UserId={UserId} triggered by session {SessionId}",
            job.UserId, job.TriggeredBySessionId);

        IReadOnlyList<Record> records;
        try
        {
            records = await this._store.GetAllForUserAsync(job.UserId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _errorCounter.Add(1);
            this._logger.LogError(ex, "Failed to load records for UserId={UserId}", job.UserId);
            return;
        }

        if (records.Count == 0)
        {
            this._logger.LogInformation(
                "No records for UserId={UserId}; skipping consolidation.", job.UserId);

            await this._eventBus.PublishAsync(new ConsolidationCompletedEvent(
                UserId: job.UserId,
                Merges: 0,
                Updates: 0,
                Deletes: 0), ct).ConfigureAwait(false);

            return;
        }

        if (records.Count > MaxRecordsPerPass)
        {
            this._logger.LogWarning(
                "UserId={UserId} has {Count} records, exceeding MaxRecordsPerPass={Max}. " +
                "Consolidation will proceed on full corpus (v1 — deferred per-domain batching).",
                job.UserId, records.Count, MaxRecordsPerPass);
        }

        if (this._agentRunner is null)
        {
            this._logger.LogWarning(
                "No agent runner configured; skipping sub-agent for UserId={UserId}.", job.UserId);

            await this._eventBus.PublishAsync(new ConsolidationCompletedEvent(
                UserId: job.UserId,
                Merges: 0,
                Updates: 0,
                Deletes: 0), ct).ConfigureAwait(false);

            return;
        }

        int merges = 0, updates = 0, deletes = 0;
        try
        {
            (merges, updates, deletes) = await this._agentRunner(job.UserId, records, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _errorCounter.Add(1);
            this._logger.LogError(ex, "Consolidation sub-agent failed for UserId={UserId}", job.UserId);
        }

        await this._eventBus.PublishAsync(new ConsolidationCompletedEvent(
            UserId: job.UserId,
            Merges: merges,
            Updates: updates,
            Deletes: deletes), ct).ConfigureAwait(false);

        this._logger.LogInformation(
            "Consolidation complete for UserId={UserId}.", job.UserId);
    }

    // ── Test-support helpers (internal) ──────────────────────────────────────

    /// <summary>
    /// Marks a pending re-run for the given user, simulating a second trigger arriving
    /// while a run is in-flight. Used in tests only.
    /// </summary>
    /// <param name="userId">The user to mark as having a pending run.</param>
    internal void EnqueuePendingIfCoalesced(string userId)
    {
        // If already tracked as in-flight, set the pending flag.
        // If not tracked (e.g., first call), add it with pending=true.
        this._inFlight.AddOrUpdate(userId, true, (_, _) => true);
    }

    /// <summary>
    /// Drains any pending re-runs synchronously for test harness use.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    internal async Task DrainPendingAsync(CancellationToken ct)
    {
        foreach (var (userId, pending) in this._inFlight.ToList())
        {
            if (pending)
            {
                this._inFlight[userId] = false;
                var job = new ConsolidationJob(userId, string.Empty);
                await this.ProcessJobAsync(job, ct).ConfigureAwait(false);
                this._inFlight.TryRemove(userId, out _);
            }
        }
    }
}
