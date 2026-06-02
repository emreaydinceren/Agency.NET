using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agency.Memory.Hygiene;

/// <summary>
/// A background service that runs periodic hygiene sweeps on the memory store,
/// pruning records that exceed their TTL and low-importance stale records.
/// </summary>
/// <remarks>
/// Two independent passes are executed per sweep:
/// <list type="number">
///   <item><description>TTL pass: deletes records older than their per-<see cref="ContentType"/> TTL
///   (and not recently accessed).</description></item>
///   <item><description>Importance pass: deletes records below the importance threshold
///   that have not been accessed within <see cref="MemoryOptions.StalePruneAge"/>.</description></item>
/// </list>
/// The sweep interval is <see cref="MemoryOptions.HygieneSchedule"/> with ±15 min jitter
/// to avoid thundering-herd when multiple processes start simultaneously.
/// </remarks>
internal sealed class HygieneSweeperBackgroundService : BackgroundService
{
    private readonly IMemoryStore _store;
    private readonly IOptions<MemoryOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HygieneSweeperBackgroundService> _logger;

    /// <summary>
    /// Initialises a new instance of <see cref="HygieneSweeperBackgroundService"/>.
    /// </summary>
    /// <param name="store">The memory store to sweep.</param>
    /// <param name="options">The memory configuration options.</param>
    /// <param name="timeProvider">The time provider (injectable for testing).</param>
    /// <param name="logger">The logger.</param>
    public HygieneSweeperBackgroundService(
        IMemoryStore store,
        IOptions<MemoryOptions> options,
        TimeProvider timeProvider,
        ILogger<HygieneSweeperBackgroundService> logger)
    {
        this._store = store;
        this._options = options;
        this._timeProvider = timeProvider;
        this._logger = logger;
    }

    /// <summary>
    /// Applies a random jitter of ±15 minutes to the given interval.
    /// </summary>
    /// <param name="baseInterval">The base sweep interval.</param>
    /// <returns>The interval with jitter applied.</returns>
    public static TimeSpan ApplyJitter(TimeSpan baseInterval)
    {
        var jitterMinutes = Random.Shared.Next(-15, 16); // -15 to +15 inclusive
        return baseInterval + TimeSpan.FromMinutes(jitterMinutes);
    }

    /// <summary>
    /// Executes the background sweep loop until the cancellation token is triggered.
    /// </summary>
    /// <param name="stoppingToken">The cancellation token that signals the host is shutting down.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = ApplyJitter(this._options.Value.HygieneSchedule);

            try
            {
                await Task.Delay(interval, this._timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await this.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
#pragma warning disable CA1031 // catch broad exception to prevent background service crash
            catch (Exception ex)
            {
                this._logger.LogError(ex, "Hygiene sweep failed unexpectedly.");
            }
#pragma warning restore CA1031
        }
    }

    /// <summary>
    /// Executes a single hygiene sweep pass (TTL + importance pruning).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="SweepResult"/> containing deletion counts for each pass.</returns>
    internal async Task<SweepResult> RunOnceAsync(CancellationToken ct)
    {
        var opts = this._options.Value;
        var ttlDeleted = 0;

        // Measure all staleness windows from the injected clock so the sweep is deterministic
        // under a virtual clock in tests (TI-4).
        var now = this._timeProvider.GetUtcNow();

        // TTL pass — one DELETE per configured content type.
        foreach (var (contentType, ttl) in opts.Ttl)
        {
            var deleted = await this._store.DeleteWhereTtlExceededAsync(contentType, ttl, now, ct);
            ttlDeleted += deleted;

            this._logger.LogInformation(
                "TTL sweep deleted {Count} {ContentType} records.",
                deleted,
                contentType);
        }

        // Importance-pruning pass.
        var importanceDeleted = await this._store.DeleteWhereLowImportanceStaleAsync(
            opts.ImportancePruneThreshold,
            opts.StalePruneAge,
            now,
            ct);

        this._logger.LogInformation(
            "Importance pruning deleted {Count} records.",
            importanceDeleted);

        return new SweepResult(ttlDeleted, importanceDeleted);
    }
}
