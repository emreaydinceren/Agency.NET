
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

[assembly: InternalsVisibleTo("Agency.VectorStore.Sql.Postgres")]
[assembly: InternalsVisibleTo("Agency.VectorStore.Sql.Sqlite")]

namespace Agency.VectorStore.Common;

/// <summary>
/// Owns the <see cref="ActivitySource"/>, <see cref="Meter"/>, operation counter, and duration histogram
/// for a vector store provider, and provides a single <see cref="ExecuteAsync{T}"/> wrapper that handles
/// Stopwatch timing, metric recording, activity status, and exception events uniformly across providers.
/// Also hosts the <see cref="LoggerMessage"/>-generated log methods shared by all vector store providers.
/// </summary>
public sealed partial class VectorStoreTelemetry : IDisposable
{
    /// <summary>Sentinel session identifier stored in the database for session-less (global) entries.</summary>
    public const string GlobalSession = "*";

    /// <summary>Sentinel project identifier stored in the database for project-less (global) entries.</summary>
    public const string GlobalProject = "*";

    private readonly ActivitySource _activitySource;
    private readonly Meter _meter;
    private readonly Counter<long> _operationCount;
    private readonly Histogram<double> _operationDuration;

    /// <summary>
    /// Initializes a new <see cref="VectorStoreTelemetry"/> that emits spans under <paramref name="activitySourceName"/>
    /// and metrics under <paramref name="meterName"/>.
    /// </summary>
    /// <param name="activitySourceName">The <see cref="ActivitySource"/> name for this provider.</param>
    /// <param name="meterName">The <see cref="Meter"/> name for this provider.</param>
    public VectorStoreTelemetry(string activitySourceName, string meterName)
    {
        this._activitySource = new ActivitySource(activitySourceName);
        this._meter = new Meter(meterName);
        this._operationCount = this._meter.CreateCounter<long>("vectorstore.operations", unit: "{operation}", description: "Total number of vector store operations executed.");
        this._operationDuration = this._meter.CreateHistogram<double>("vectorstore.duration", unit: "ms", description: "Duration of vector store operations in milliseconds.");
    }

    /// <summary>Resolves a nullable session id to the global sentinel value <c>"*"</c>.</summary>
    /// <param name="sessionId">The caller-supplied session identifier, or <see langword="null"/> for global scope.</param>
    public static string ResolveSessionId(string? sessionId) => sessionId ?? GlobalSession;

    /// <summary>Resolves a nullable project id to the global sentinel value <c>"*"</c>.</summary>
    /// <param name="projectId">The caller-supplied project identifier, or <see langword="null"/> for global scope.</param>
    public static string ResolveProjectId(string? projectId) => projectId ?? GlobalProject;

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
        ArgumentNullException.ThrowIfNull(body);

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

    /// <summary>Disposes the underlying <see cref="ActivitySource"/> and <see cref="Meter"/>.</summary>
    /// <remarks>
    /// Satisfies CA1001's requirement that a type owning disposable fields be itself disposable. In practice,
    /// every current consumer holds this type as a <c>private static readonly</c> process-lifetime singleton, so
    /// this is never actually invoked - acceptable because <see cref="ActivitySource"/>/<see cref="Meter"/> hold
    /// no OS-level handles, making this the standard OpenTelemetry pattern for long-lived instrumentation, not a leak.
    /// </remarks>
    public void Dispose()
    {
        this._activitySource.Dispose();
        this._meter.Dispose();
    }

    /// <summary>Logs that vector store schema initialization is starting.</summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "Initializing vector store schema with {Dimensions} dimensions")]
    internal static partial void LogInitializingSchema(ILogger logger, int dimensions);

    /// <summary>Logs that vector store schema initialization completed.</summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "Vector store schema initialization completed in {ElapsedMs}ms")]
    internal static partial void LogSchemaInitialized(ILogger logger, double elapsedMs);

    /// <summary>Logs that vector store schema initialization failed.</summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "Error initializing vector store schema after {ElapsedMs}ms")]
    internal static partial void LogErrorInitializingSchema(ILogger logger, Exception ex, double elapsedMs);

    /// <summary>Logs that a vector store search is starting.</summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "Searching vector store with limit {Limit} and metadata filter present: {HasFilter}")]
    internal static partial void LogSearching(ILogger logger, int limit, bool hasFilter);

    /// <summary>Logs that a vector store search completed.</summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "Vector store search completed in {ElapsedMs}ms. Results returned: {ResultCount}")]
    internal static partial void LogSearchCompleted(ILogger logger, double elapsedMs, int resultCount);

    /// <summary>Logs that a vector store search failed.</summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "Error searching vector store after {ElapsedMs}ms")]
    internal static partial void LogErrorSearching(ILogger logger, Exception ex, double elapsedMs);

    /// <summary>Logs that a vector store upsert is starting.</summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "Upserting vector store entry for user {UserId} session {SessionId} key {Key} with metadata present: {HasMetadata}")]
    internal static partial void LogUpserting(ILogger logger, string userId, string? sessionId, string key, bool hasMetadata);

    /// <summary>Logs that a vector store upsert completed.</summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "Vector store upsert completed in {ElapsedMs}ms for user {UserId} session {SessionId} key {Key}")]
    internal static partial void LogUpserted(ILogger logger, double elapsedMs, string userId, string? sessionId, string key);

    /// <summary>Logs that a vector store upsert failed.</summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "Error upserting vector store entry after {ElapsedMs}ms for user {UserId} session {SessionId} key {Key}")]
    internal static partial void LogErrorUpserting(ILogger logger, Exception ex, double elapsedMs, string userId, string? sessionId, string key);

    /// <summary>Logs that a vector store delete is starting.</summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "Deleting vector store entry for user {UserId} session {SessionId} key {Key}")]
    internal static partial void LogDeleting(ILogger logger, string userId, string? sessionId, string key);

    /// <summary>Logs that a vector store delete completed.</summary>
    [LoggerMessage(Level = LogLevel.Debug, Message = "Vector store delete completed in {ElapsedMs}ms for user {UserId} session {SessionId} key {Key}. Deleted: {Deleted}")]
    internal static partial void LogDeleted(ILogger logger, double elapsedMs, string userId, string? sessionId, string key, bool deleted);

    /// <summary>Logs that a vector store delete failed.</summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "Error deleting vector store entry after {ElapsedMs}ms for user {UserId} session {SessionId} key {Key}")]
    internal static partial void LogErrorDeleting(ILogger logger, Exception ex, double elapsedMs, string userId, string? sessionId, string key);
}
