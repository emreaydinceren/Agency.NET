
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Agency.KeyValueStore.Common;

/// <summary>
/// Owns the <see cref="ActivitySource"/>, <see cref="Meter"/>, operation counter, and duration histogram
/// for a KV store provider, and provides a single <see cref="ExecuteAsync{T}"/> wrapper that handles
/// Stopwatch timing, metric recording, activity status, and exception events uniformly across providers.
/// </summary>
public sealed class KvStoreTelemetry
{
    /// <summary>Sentinel session identifier stored in the database for session-less (global) entries.</summary>
    public const string GlobalSession = "*";

    private readonly ActivitySource _activitySource;
    private readonly Meter _meter;
    private readonly Counter<long> _operationCount;
    private readonly Histogram<double> _operationDuration;

    /// <summary>
    /// Initializes a new <see cref="KvStoreTelemetry"/> that emits spans under <paramref name="activitySourceName"/>
    /// and metrics under <paramref name="meterName"/>.
    /// </summary>
    /// <param name="activitySourceName">The <see cref="ActivitySource"/> name for this provider.</param>
    /// <param name="meterName">The <see cref="Meter"/> name for this provider.</param>
    public KvStoreTelemetry(string activitySourceName, string meterName)
    {
        this._activitySource = new ActivitySource(activitySourceName);
        this._meter = new Meter(meterName);
        this._operationCount = this._meter.CreateCounter<long>("kvstore.operations", unit: "{operation}", description: "Total number of KV store operations executed.");
        this._operationDuration = this._meter.CreateHistogram<double>("kvstore.duration", unit: "ms", description: "Duration of KV store operations in milliseconds.");
    }

    /// <summary>Resolves a nullable session id to the global sentinel value <c>"*"</c>.</summary>
    /// <param name="sessionId">The caller-supplied session identifier, or <see langword="null"/> for global scope.</param>
    public static string ResolveSessionId(string? sessionId) => sessionId ?? GlobalSession;

    /// <summary>Starts a new <see cref="ActivityKind.Client"/> activity with the given span name.</summary>
    /// <param name="spanName">The name of the span to start.</param>
    public Activity? StartActivity(string spanName) => this._activitySource.StartActivity(spanName, ActivityKind.Client);

    /// <summary>
    /// Runs <paramref name="body"/>, measures elapsed time, records the operation counter and duration histogram,
    /// sets the activity status to <see cref="ActivityStatusCode.Ok"/> on success or <see cref="ActivityStatusCode.Error"/>
    /// on failure (including an <c>exception</c> event), invokes the logging callbacks, then re-throws on failure.
    /// </summary>
    /// <typeparam name="T">The result type returned by <paramref name="body"/>.</typeparam>
    /// <param name="operation">The operation name recorded on the metric tags (e.g. <c>"search"</c>).</param>
    /// <param name="activity">The ambient activity started before calling this method, or <see langword="null"/>.</param>
    /// <param name="body">The provider-specific work to execute inside the telemetry wrapper.</param>
    /// <param name="onSuccess">Optional callback receiving the result and elapsed milliseconds, used for success logging.</param>
    /// <param name="onError">Optional callback receiving the exception and elapsed milliseconds, used for error logging.</param>
    public async Task<T> ExecuteAsync<T>(
        string operation,
        Activity? activity,
        Func<Task<T>> body,
        Action<T, double>? onSuccess = null,
        Action<Exception, double>? onError = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            T result = await body().ConfigureAwait(false);
            sw.Stop();
            this._operationCount.Add(1, new TagList { { "operation", operation }, { "status", "success" } });
            this._operationDuration.Record(sw.Elapsed.TotalMilliseconds, new TagList { { "operation", operation } });
            activity?.SetStatus(ActivityStatusCode.Ok);
            onSuccess?.Invoke(result, sw.Elapsed.TotalMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            this._operationCount.Add(1, new TagList { { "operation", operation }, { "status", "error" } });
            this._operationDuration.Record(sw.Elapsed.TotalMilliseconds, new TagList { { "operation", operation } });
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                { "exception.type", ex.GetType().FullName },
                { "exception.message", ex.Message },
                { "exception.stacktrace", ex.ToString() },
            }));
            onError?.Invoke(ex, sw.Elapsed.TotalMilliseconds);
            throw;
        }
    }
}
