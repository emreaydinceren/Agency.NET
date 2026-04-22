namespace Agency.Ingestion;

using Agency.VectorStore.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

/// <summary>
/// Default orchestration of the load → split → store ingestion pipeline.
/// </summary>
public sealed class DefaultIngestionPipeline<TValue> : IIngestionPipeline<TValue>
{
    /// <summary>The activity source name used for ingestion telemetry.</summary>
    public const string ActivitySourceName = "Agency.Ingestion";

    /// <summary>The meter name used for ingestion telemetry.</summary>
    public const string MeterName = "Agency.Ingestion";

    private static readonly ActivitySource _activitySource = new(ActivitySourceName);
    private static readonly Meter _meter = new(MeterName);

    private static readonly Counter<long> _documentsCounter = _meter.CreateCounter<long>(
        "ingestion.documents_total",
        description: "Total number of document chunks processed by the ingestion pipeline.");

    private static readonly Histogram<double> _durationHistogram = _meter.CreateHistogram<double>(
        "ingestion.duration_ms",
        unit: "ms",
        description: "Duration of ingestion pipeline runs in milliseconds.");

    private readonly Func<Document, TValue> _chunkConverter;
    private readonly int _maxDegreeOfParallelism;
    private readonly ILogger<DefaultIngestionPipeline<TValue>> _logger;

    /// <summary>
    /// Creates a new pipeline with the specified chunk converter and optional settings.
    /// </summary>
    /// <param name="chunkConverter">Converts a <see cref="Document"/> chunk to the store value type.</param>
    /// <param name="maxDegreeOfParallelism">Max concurrent document loads. Defaults to <see cref="Environment.ProcessorCount"/>.</param>
    /// <param name="logger">Optional logger.</param>
    public DefaultIngestionPipeline(
        Func<Document, TValue> chunkConverter,
        int maxDegreeOfParallelism = 0,
        ILogger<DefaultIngestionPipeline<TValue>>? logger = null)
    {
        this._chunkConverter = chunkConverter ?? throw new ArgumentNullException(nameof(chunkConverter));
        this._maxDegreeOfParallelism = maxDegreeOfParallelism > 0
            ? maxDegreeOfParallelism
            : Environment.ProcessorCount;
        this._logger = logger ?? NullLogger<DefaultIngestionPipeline<TValue>>.Instance;
    }

    /// <inheritdoc/>
    public async Task<IngestionResult> ExecuteAsync(
        IDocumentLoader loader,
        ITextSplitter splitter,
        IVectorStore store,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(splitter);
        ArgumentNullException.ThrowIfNull(store);

        using var activity = _activitySource.StartActivity("ingestion.execute");
        var sw = Stopwatch.StartNew();
        int succeeded = 0;
        int failed = 0;
        var failedKeys = new ConcurrentBag<string>();

        this._logger.LogInformation("Ingestion pipeline started.");

        await Parallel.ForEachAsync(
            loader.LoadAsync(ct),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = this._maxDegreeOfParallelism,
                CancellationToken = ct,
            },
            async (document, token) =>
            {
                var chunks = splitter.Split(document).ToList();

                for (int i = 0; i < chunks.Count; i++)
                {
                    string key = $"{document.SourceId}:chunk:{i}";
                    var metadata = BuildChunkMetadata(chunks[i].Metadata, document.SourceId, i);

                    try
                    {
                        await store.UpsertAsync<TValue>(key, this._chunkConverter(chunks[i]), metadata, token);
                        Interlocked.Increment(ref succeeded);
                        _documentsCounter.Add(1, new TagList { { "status", "success" } });
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        failedKeys.Add(key);
                        _documentsCounter.Add(1, new TagList { { "status", "failure" } });
                        this._logger.LogError(ex, "Failed to upsert chunk. Key={Key}", key);
                    }
                }
            });

        sw.Stop();
        _durationHistogram.Record(sw.Elapsed.TotalMilliseconds);

        activity?.SetTag("ingestion.succeeded", succeeded);
        activity?.SetTag("ingestion.failed", failed);

        if (failed > 0)
        {
            activity?.SetStatus(ActivityStatusCode.Error, $"{failed} chunk(s) failed to ingest.");
        }

        this._logger.LogInformation(
            "Ingestion pipeline completed. Succeeded={Succeeded}, Failed={Failed}, DurationMs={DurationMs:F1}",
            succeeded, failed, sw.Elapsed.TotalMilliseconds);

        return new IngestionResult(succeeded, failed,
            failedKeys.Count > 0 ? failedKeys.ToList() : null);
    }

    private static Dictionary<string, object> BuildChunkMetadata(
        Dictionary<string, object>? existing,
        string sourceId,
        int chunkIndex)
    {
        var meta = new Dictionary<string, object>(existing ?? [], StringComparer.Ordinal);
        meta["source_file"] = sourceId;
        meta["chunk_index"] = chunkIndex;
        meta["ingested_at"] = DateTimeOffset.UtcNow;
        return meta;
    }
}
