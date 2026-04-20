using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Agency.Embeddings.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Embeddings;

namespace Agency.Embeddings.OpenAI;

/// <summary>
/// Generates vector embeddings using the model configured in <see cref="EmbeddingOptions"/>,
/// calling the OpenAI-compatible API exposed by LM Studio.
/// </summary>
public sealed class EmbeddingGenerator : IEmbeddingGenerator
{
    /// <summary>
    /// The activity source name used for embedding telemetry.
    /// </summary>
    public const string ActivitySourceName = "Agency.Embeddings.OpenAI";

    /// <summary>
    /// The meter name used for embedding telemetry.
    /// </summary>
    public const string MeterName = "Agency.Embeddings.OpenAI";

    private static readonly ActivitySource _activitySource = new(ActivitySourceName);
    private static readonly Meter _meter = new(MeterName);

    private static readonly Counter<long> _requestCount =
        _meter.CreateCounter<long>("embedding.requests", unit: "{request}", description: "Total number of embedding requests.");

    private static readonly Histogram<double> _requestDuration =
        _meter.CreateHistogram<double>("embedding.duration", unit: "ms", description: "Duration of embedding requests in milliseconds.");

    private static readonly Counter<long> _tokenCount =
        _meter.CreateCounter<long>("embedding.tokens", unit: "{token}", description: "Total number of input tokens consumed by embedding requests.");

    private readonly EmbeddingClient _client;
    private readonly string? _modelId;
    private readonly ILogger<EmbeddingGenerator> _logger;

    /// <summary>Initialises the generator from application configuration via the DI options system.</summary>
    public EmbeddingGenerator(IOptions<EmbeddingOptions> options, ILogger<EmbeddingGenerator>? logger = null)
        : this(options.Value, logger) { }

    /// <summary>Initialises the generator directly from an options instance.</summary>
    public EmbeddingGenerator(EmbeddingOptions options, ILogger<EmbeddingGenerator>? logger = null)
        : this(options, httpMessageHandler: null, logger) { }

    /// <summary>Internal constructor that allows injecting a custom HTTP handler for testing.</summary>
    internal EmbeddingGenerator(EmbeddingOptions options, HttpMessageHandler? httpMessageHandler, ILogger<EmbeddingGenerator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.BaseUrl);
        ArgumentException.ThrowIfNullOrEmpty(options.ApiKey);

        this._modelId = options.ModelId;
        this._logger = logger ?? NullLogger<EmbeddingGenerator>.Instance;

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(options.BaseUrl),
        };

        if (httpMessageHandler is not null)
        {
            clientOptions.Transport = new HttpClientPipelineTransport(new HttpClient(httpMessageHandler));
        }

        this._client = new EmbeddingClient(options.ModelId, new ApiKeyCredential(options.ApiKey), clientOptions);
    }

    /// <summary>Generates an embedding vector for a single input string.</summary>
    public async Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(
        string input,
        CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("embedding.generate", ActivityKind.Client);
        activity?.SetTag("gen_ai.system", "openai");
        activity?.SetTag("gen_ai.operation.name", "embeddings");
        activity?.SetTag("gen_ai.request.model", this._modelId);
        activity?.SetTag("input.length", input?.Length ?? 0);

        var stopwatch = Stopwatch.StartNew();
        this._logger.LogDebug("Generating embedding for input of length {InputLength}", input?.Length ?? 0);

        try
        {
            var result = await this._client.GenerateEmbeddingAsync(input, cancellationToken: cancellationToken);

            stopwatch.Stop();

            _requestCount.Add(1, new TagList { { "operation", "single" }, { "status", "success" } });
            _requestDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "single" } });

            activity?.SetStatus(ActivityStatusCode.Ok);

            this._logger.LogDebug("Embedding generated in {ElapsedMs}ms", stopwatch.Elapsed.TotalMilliseconds);

            return result.Value.ToFloats();
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _requestCount.Add(1, new TagList { { "operation", "single" }, { "status", "error" } });
            _requestDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "single" } });

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                { "exception.type", ex.GetType().FullName },
                { "exception.message", ex.Message },
                { "exception.stacktrace", ex.ToString() },
            }));

            this._logger.LogError(ex, "Error generating embedding after {ElapsedMs}ms", stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    /// <summary>Generates embedding vectors for a batch of input strings.</summary>
    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(
        IEnumerable<string> inputs,
        CancellationToken cancellationToken = default)
    {
        var inputList = inputs as ICollection<string> ?? inputs.ToList();

        using var activity = _activitySource.StartActivity("embedding.generate_batch", ActivityKind.Client);
        activity?.SetTag("gen_ai.system", "openai");
        activity?.SetTag("gen_ai.operation.name", "embeddings");
        activity?.SetTag("gen_ai.request.model", this._modelId);
        activity?.SetTag("input.count", inputList.Count);

        var stopwatch = Stopwatch.StartNew();
        this._logger.LogDebug("Generating embeddings for batch of {InputCount} inputs", inputList.Count);

        try
        {
            var result = await this._client.GenerateEmbeddingsAsync(inputList, cancellationToken: cancellationToken);

            stopwatch.Stop();
            int tokens = result.Value.Usage.InputTokenCount;

            _requestCount.Add(1, new TagList { { "operation", "batch" }, { "status", "success" } });
            _requestDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "batch" } });
            _tokenCount.Add(tokens, new TagList { { "operation", "batch" } });

            activity?.SetTag("gen_ai.response.usage.input_tokens", tokens);
            activity?.SetTag("gen_ai.response.embedding_count", result.Value.Count);
            activity?.SetStatus(ActivityStatusCode.Ok);

            this._logger.LogDebug("Batch embeddings generated in {ElapsedMs}ms. Count: {Count}, Input tokens: {Tokens}",
                stopwatch.Elapsed.TotalMilliseconds, result.Value.Count, tokens);

            return result.Value.Select(static e => e.ToFloats()).ToList();
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _requestCount.Add(1, new TagList { { "operation", "batch" }, { "status", "error" } });
            _requestDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new TagList { { "operation", "batch" } });

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                { "exception.type", ex.GetType().FullName },
                { "exception.message", ex.Message },
                { "exception.stacktrace", ex.ToString() },
            }));

            this._logger.LogError(ex, "Error generating batch embeddings after {ElapsedMs}ms. Input count: {InputCount}",
                stopwatch.Elapsed.TotalMilliseconds, inputList.Count);
            throw;
        }
    }
}
