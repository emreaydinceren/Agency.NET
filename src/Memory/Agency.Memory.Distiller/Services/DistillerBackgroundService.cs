using System.Diagnostics;
using System.Diagnostics.Metrics;
using Agency.Memory.Common.Events;
using Agency.Memory.Common.Jobs;
using Agency.Memory.Common.Options;
using Agency.Memory.Common.Records;
using Agency.Memory.Common.Storage;
using Agency.Memory.Distiller.Prompts;
using Agency.Agentic;
using Agency.Agentic.Contexts;
using Agency.Embeddings.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agency.Memory.Distiller.Services;

/// <summary>
/// Background service that dequeues <see cref="DistillationJob"/>s and converts
/// conversation turns into durable <see cref="Record"/>s (Spec §6.2).
/// </summary>
/// <remarks>
/// Reads from per-session channels in <see cref="ChannelSessionRegistry"/>.
/// Retries transient failures with exponential backoff; dead-letters permanent failures.
/// Emits <see cref="DistillationCompletedEvent"/> / <see cref="DistillationFailedEvent"/>
/// after every job.
/// </remarks>
internal sealed class DistillerBackgroundService : BackgroundService
{
    internal const string ActivitySourceName = "Agency.Memory.Distiller";
    internal const string MeterName = "Agency.Memory.Distiller";

    private static readonly ActivitySource _activitySource = new(ActivitySourceName);
    private static readonly Meter _meter = new(MeterName);

    private static readonly Counter<long> _jobCounter =
        _meter.CreateCounter<long>("memory.distiller.jobs", description: "Total distillation jobs processed");

    private static readonly Counter<long> _errorCounter =
        _meter.CreateCounter<long>("memory.distiller.errors", description: "Permanent distillation failures");

    private static readonly Histogram<double> _durationHistogram =
        _meter.CreateHistogram<double>("memory.distiller.duration", unit: "ms",
            description: "Distillation job duration");

    private readonly ChannelSessionRegistry _channelRegistry;
    private readonly IConversationManagerRegistry _conversationRegistry;
    private readonly ILlmClientAdapter _llm;
    private readonly IEmbeddingGenerator _embedder;
    private readonly IMemoryStore _store;
    private readonly IWatermarkStore _watermarks;
    private readonly IDeadLetterStore _deadLetter;
    private readonly IAsyncEventBus _eventBus;
    private readonly IOptions<DistillerOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DistillerBackgroundService> _logger;

    /// <summary>
    /// Initialises a new <see cref="DistillerBackgroundService"/>.
    /// </summary>
    internal DistillerBackgroundService(
        ChannelSessionRegistry channelRegistry,
        IConversationManagerRegistry conversationRegistry,
        ILlmClientAdapter llm,
        IEmbeddingGenerator embedder,
        IMemoryStore store,
        IWatermarkStore watermarks,
        IDeadLetterStore deadLetter,
        IAsyncEventBus eventBus,
        IOptions<DistillerOptions> options,
        TimeProvider timeProvider,
        ILogger<DistillerBackgroundService> logger)
    {
        this._channelRegistry = channelRegistry ?? throw new ArgumentNullException(nameof(channelRegistry));
        this._conversationRegistry = conversationRegistry ?? throw new ArgumentNullException(nameof(conversationRegistry));
        this._llm = llm ?? throw new ArgumentNullException(nameof(llm));
        this._embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        this._store = store ?? throw new ArgumentNullException(nameof(store));
        this._watermarks = watermarks ?? throw new ArgumentNullException(nameof(watermarks));
        this._deadLetter = deadLetter ?? throw new ArgumentNullException(nameof(deadLetter));
        this._eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        this._options = options ?? throw new ArgumentNullException(nameof(options));
        this._timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this._logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        this._logger.LogInformation("DistillerBackgroundService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Poll all active session channels.
            bool processed = false;
            foreach (var (sessionId, channel) in this._channelRegistry.GetAll())
            {
                while (channel.Reader.TryRead(out DistillationJob? job))
                {
                    await this.ProcessJobAsync(job, stoppingToken).ConfigureAwait(false);
                    processed = true;
                }
            }

            if (!processed)
            {
                // Yield to avoid busy-waiting when no jobs are pending.
                await Task.Delay(TimeSpan.FromMilliseconds(50), this._timeProvider, stoppingToken)
                    .ConfigureAwait(false);
            }
        }

        this._logger.LogInformation("DistillerBackgroundService stopping.");
    }

    /// <summary>
    /// Processes a single <see cref="DistillationJob"/> with retry logic.
    /// </summary>
    private async Task ProcessJobAsync(DistillationJob job, CancellationToken ct)
    {
        using Activity? activity = _activitySource.StartActivity("memory.distill");
        activity?.SetTag("memory.user_id", job.UserId);
        activity?.SetTag("memory.session_id", job.SessionId);
        activity?.SetTag("memory.trigger", job.Trigger.ToString());

        var sw = Stopwatch.StartNew();
        _jobCounter.Add(1);

        this._logger.LogInformation(
            "Processing distillation job: UserId={UserId}, SessionId={SessionId}, Trigger={Trigger}, UpToTurnIndex={UpTo}",
            job.UserId, job.SessionId, job.Trigger, job.UpToTurnIndex);

        // C.4: Watermark guard — idempotency check before any LLM work.
        int currentWatermark = await this._watermarks.GetAsync(job.UserId, job.SessionId, ct)
            .ConfigureAwait(false);

        if (job.UpToTurnIndex <= currentWatermark)
        {
            this._logger.LogInformation(
                "Skipping job: watermark {Watermark} >= UpToTurnIndex {UpTo}. SessionId={SessionId}",
                currentWatermark, job.UpToTurnIndex, job.SessionId);

            await this._eventBus.PublishAsync(new DistillationCompletedEvent(
                UserId: job.UserId,
                SessionId: job.SessionId,
                RecordsWritten: 0,
                NewWatermark: currentWatermark), ct).ConfigureAwait(false);

            return;
        }

        // Load turns in (watermark, upToTurnIndex].
        IConversationManager? convo = this._conversationRegistry.Get(job.SessionId);
        if (convo is null)
        {
            this._logger.LogWarning(
                "No conversation found for session {SessionId}. Skipping.", job.SessionId);

            await this._eventBus.PublishAsync(new DistillationCompletedEvent(
                UserId: job.UserId,
                SessionId: job.SessionId,
                RecordsWritten: 0,
                NewWatermark: currentWatermark), ct).ConfigureAwait(false);

            return;
        }

        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> allMessages = convo.Messages;
        // Messages are 0-indexed; turns in (watermark, upToTurnIndex] means
        // indices [watermark, upToTurnIndex - 1] (0-based).
        var turns = allMessages
            .Skip(currentWatermark)
            .Take(job.UpToTurnIndex - currentWatermark)
            .ToList();

        if (turns.Count == 0)
        {
            this._logger.LogInformation(
                "No turns in window for session {SessionId}. Skipping LLM call.", job.SessionId);

            await this._eventBus.PublishAsync(new DistillationCompletedEvent(
                UserId: job.UserId,
                SessionId: job.SessionId,
                RecordsWritten: 0,
                NewWatermark: currentWatermark), ct).ConfigureAwait(false);

            return;
        }

        // C.5: Retry loop with error classification.
        DistillerOptions opts = this._options.Value;
        int maxAttempts = opts.MaxRetries + 1;
        Exception? lastException = null;
        bool parseRetried = false;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (attempt > 0)
            {
                TimeSpan delay = TimeSpan.FromTicks(opts.RetryBaseDelay.Ticks * (long)Math.Pow(2, attempt - 1));
                this._logger.LogWarning(
                    "Retrying distillation (attempt {Attempt}/{Max}) after {Delay}ms. SessionId={SessionId}",
                    attempt + 1, maxAttempts, delay.TotalMilliseconds, job.SessionId);

                await Task.Delay(delay, this._timeProvider, ct).ConfigureAwait(false);
            }

            try
            {
                int recordsWritten = await this.ExtractAndUpsertAsync(
                    job, turns, ct).ConfigureAwait(false);

                int newWatermark = await this._watermarks.AdvanceAsync(
                    job.UserId, job.SessionId, job.UpToTurnIndex, ct).ConfigureAwait(false);

                sw.Stop();
                _durationHistogram.Record(sw.Elapsed.TotalMilliseconds);

                await this._eventBus.PublishAsync(new DistillationCompletedEvent(
                    UserId: job.UserId,
                    SessionId: job.SessionId,
                    RecordsWritten: recordsWritten,
                    NewWatermark: newWatermark), ct).ConfigureAwait(false);

                this._logger.LogInformation(
                    "Distillation complete: {Count} records written, watermark={Watermark}. SessionId={SessionId}",
                    recordsWritten, newWatermark, job.SessionId);

                return; // success
            }
            catch (OperationCanceledException)
            {
                throw; // never dead-letter cancellation
            }
            catch (ExtractionParseException ex) when (!parseRetried)
            {
                // One parse retry with stricter prompt signal.
                parseRetried = true;
                lastException = ex;
                this._logger.LogWarning(ex, "Parse failure on attempt {Attempt}; will retry once.", attempt + 1);
                // Continue to next attempt — the retry prompt is inherently stricter.
                continue;
            }
            catch (ExtractionParseException ex)
            {
                // Second parse failure → permanent.
                lastException = ex;
                this._logger.LogError(ex, "Permanent parse failure (second attempt). Dead-lettering job.");
                await this.DeadLetterAsync(job, ex, ct).ConfigureAwait(false);
                _errorCounter.Add(1);
                return;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                lastException = ex;
                this._logger.LogWarning(ex,
                    "Transient failure on attempt {Attempt}/{Max}.", attempt + 1, maxAttempts);

                if (attempt == maxAttempts - 1)
                {
                    // Exhausted retries → dead-letter.
                    await this.DeadLetterAsync(job, ex, ct).ConfigureAwait(false);
                    _errorCounter.Add(1);
                    return;
                }

                // Continue to next attempt.
                continue;
            }
            catch (Exception ex)
            {
                // Permanent failure class.
                lastException = ex;
                this._logger.LogError(ex, "Permanent distillation failure. Dead-lettering job.");
                await this.DeadLetterAsync(job, ex, ct).ConfigureAwait(false);
                _errorCounter.Add(1);
                return;
            }
        }

        // Should not reach here — all paths return inside the loop.
        if (lastException is not null)
        {
            await this.DeadLetterAsync(job, lastException, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Calls the LLM, parses the response, embeds each record, and upserts them.
    /// </summary>
    /// <returns>The number of records written.</returns>
    private async Task<int> ExtractAndUpsertAsync(
        DistillationJob job,
        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> turns,
        CancellationToken ct)
    {
        // Build context for the prompt.
        IReadOnlyList<string> knownDomains = await this.GetKnownDomainsAsync(job.UserId, ct)
            .ConfigureAwait(false);
        IReadOnlyList<Record> recentFacts = await this.GetRecentFactsAsync(job.UserId, ct)
            .ConfigureAwait(false);

        string prompt = EpisodeExtractionPrompt.Render(
            job,
            turns,
            FocusContext.Empty,
            knownDomains,
            recentFacts);

        string llmResponse = await this._llm.SendAsync(prompt, ct).ConfigureAwait(false);

        IReadOnlyList<Record> records = EpisodeExtractionParser.Parse(llmResponse, job.UserId, job.SessionId);

        int count = 0;
        foreach (Record record in records)
        {
            // Embed: Title + "\n\n" + Value (Spec §6.2 Implementation notes).
            string embeddingText = record.Title + "\n\n" + record.Value;
            ReadOnlyMemory<float> embedding = await this._embedder
                .GenerateEmbeddingAsync(embeddingText, ct).ConfigureAwait(false);

            Record withEmbedding = record with { Embedding = embedding };
            await this._store.UpsertAsync(withEmbedding, ct).ConfigureAwait(false);
            count++;
        }

        return count;
    }

    private async Task<IReadOnlyList<string>> GetKnownDomainsAsync(string userId, CancellationToken ct)
    {
        IReadOnlyList<Record> all = await this._store.GetAllForUserAsync(userId, ct).ConfigureAwait(false);
        return all.Select(static r => r.Domain).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<IReadOnlyList<Record>> GetRecentFactsAsync(string userId, CancellationToken ct)
    {
        IReadOnlyList<Record> all = await this._store.GetAllForUserAsync(userId, ct).ConfigureAwait(false);
        return all
            .Where(static r => r.ContentType == ContentType.Fact)
            .OrderByDescending(static r => r.UpdatedAt)
            .Take(10)
            .ToList();
    }

    private async Task DeadLetterAsync(DistillationJob job, Exception ex, CancellationToken ct)
    {
        try
        {
            await this._deadLetter.WriteAsync(
                job.UserId, job.SessionId, "distillation", job, ex, ct).ConfigureAwait(false);

            await this._eventBus.PublishAsync(new DistillationFailedEvent(
                UserId: job.UserId,
                SessionId: job.SessionId,
                Reason: ex.Message,
                DeadLettered: true), ct).ConfigureAwait(false);
        }
        catch (Exception dlEx)
        {
            this._logger.LogError(dlEx, "Failed to write to dead-letter for session {SessionId}.", job.SessionId);
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> for exceptions that should be retried (Spec §8.6 transient class).
    /// </summary>
    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException httpEx
            && (httpEx.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                || httpEx.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
        || ex is TaskCanceledException { InnerException: TimeoutException }
        || ex is Npgsql.NpgsqlException { IsTransient: true };
}
