namespace Agency.Llm.OpenAI;

using Agency.Llm.Abstractions;
using global::OpenAI.Chat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.ClientModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

/// <summary>
/// A client for interacting with the OpenAI API.
/// </summary>
public class OpenAIClient : ILlmClient
{
    /// <summary>
    /// The activity source name used for OpenAI telemetry.
    /// </summary>
    public static readonly string ActivitySourceName = "Agency.Llm.OpenAI";

    /// <summary>
    /// The meter name used for OpenAI telemetry.
    /// </summary>
    public static readonly string MeterName = "Agency.Llm.OpenAI";

    private static readonly ActivitySource _activitySource = new(ActivitySourceName);
    private static readonly Meter _meter = new(MeterName);

    private static readonly Counter<long> _requestCounter = _meter.CreateCounter<long>(
        "llm.client.requests",
        description: "Total number of Llm requests");

    private static readonly Counter<long> _errorCounter = _meter.CreateCounter<long>(
        "llm.client.errors",
        description: "Total number of failed Llm requests");

    private static readonly Histogram<double> _durationHistogram = _meter.CreateHistogram<double>(
        "llm.client.duration",
        unit: "ms",
        description: "Duration of Llm requests in milliseconds");

    private static readonly Counter<long> _tokenCounter = _meter.CreateCounter<long>(
        "llm.client.tokens",
        description: "Number of tokens consumed in Llm requests");

    private readonly global::OpenAI.OpenAIClient _client;

    private readonly ILogger<OpenAIClient> _logger;

    /// <summary>
    /// Creates a client from configured options.
    /// </summary>
    public OpenAIClient(IOptions<OpenAIClientOptions> options, ILogger<OpenAIClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _logger = logger ?? NullLogger<OpenAIClient>.Instance;

        var credential = new ApiKeyCredential(options.Value.ApiKey);
        var clientOptions = new global::OpenAI.OpenAIClientOptions();

        if (options.Value.BaseUrl is not null)
        {
            clientOptions.Endpoint = new Uri(options.Value.BaseUrl);
        }

        this._client = new global::OpenAI.OpenAIClient(credential, clientOptions);
    }

    /// <summary>
    /// Creates a client using the <c>OPENAI_API_KEY</c> environment variable.
    /// </summary>
    public OpenAIClient(ILogger<OpenAIClient>? logger = null)
    {
        _logger = logger ?? NullLogger<OpenAIClient>.Instance;
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
        this._client = new global::OpenAI.OpenAIClient(new ApiKeyCredential(apiKey));
    }

    /// <summary>
    /// Sends a completion request to OpenAI.
    /// </summary>
    public async Task<LlmResponse> SendAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        long? maxTokens = 1024,
        float? temperature = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("SendAsync");
        activity?.SetTag("gen_ai.system", "openai");
        activity?.SetTag("gen_ai.request.model", model);

        var tags = new TagList { { "gen_ai.system", "openai" }, { "gen_ai.request.model", model }, { "llm.method", "send" } };
        _requestCounter.Add(1, tags);

        var sw = Stopwatch.StartNew();
        _logger.LogInformation("Sending request to OpenAI. Model={Model}", model);

        try
        {
            ChatClient chatClient = this._client.GetChatClient(model);

            ChatCompletionOptions chatOptions = new()
            {
                MaxOutputTokenCount = (int?)(maxTokens ?? 1024),
                Temperature = temperature * 2f,
            };

            var result = await chatClient.CompleteChatAsync(
                [new SystemChatMessage(systemPrompt), new UserChatMessage(userPrompt)],
                chatOptions,
                cancellationToken);

            sw.Stop();
            var inputTokens = result.Value.Usage?.InputTokenCount ?? 0;
            var outputTokens = result.Value.Usage?.OutputTokenCount ?? 0;

            _durationHistogram.Record(sw.Elapsed.TotalMilliseconds, tags);
            _tokenCounter.Add(inputTokens, new TagList { { "gen_ai.system", "openai" }, { "gen_ai.request.model", model }, { "gen_ai.token.type", "input" } });
            _tokenCounter.Add(outputTokens, new TagList { { "gen_ai.system", "openai" }, { "gen_ai.request.model", model }, { "gen_ai.token.type", "output" } });

            activity?.SetTag("gen_ai.usage.input_tokens", inputTokens);
            activity?.SetTag("gen_ai.usage.output_tokens", outputTokens);

            _logger.LogInformation(
                "OpenAI request completed. Model={Model}, InputTokens={InputTokens}, OutputTokens={OutputTokens}, DurationMs={DurationMs}",
                model, inputTokens, outputTokens, sw.Elapsed.TotalMilliseconds);

            var messageText = string.Concat(result.Value.Content.Select(static part => part.Text ?? string.Empty));
            return new LlmResponse(
                messageText,
                FinishReasonConverter.ToStopReason(result.Value.FinishReason.ToString()),
                new LlmTokenUsage(inputTokens, outputTokens));
        }
        catch (Exception ex)
        {
            sw.Stop();
            _durationHistogram.Record(sw.Elapsed.TotalMilliseconds, tags);
            _errorCounter.Add(1, tags);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "OpenAI request failed. Model={Model}", model);
            throw;
        }
    }

    /// <summary>
    /// Streams a completion response from OpenAI.
    /// </summary>
    public async IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        long? maxTokens = 1024,
        float? temperature = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("StreamAsync");
        activity?.SetTag("gen_ai.system", "openai");
        activity?.SetTag("gen_ai.request.model", model);

        var tags = new TagList { { "gen_ai.system", "openai" }, { "gen_ai.request.model", model }, { "llm.method", "stream" } };
        _requestCounter.Add(1, tags);

        var sw = Stopwatch.StartNew();
        _logger.LogInformation("Starting streaming request to OpenAI. Model={Model}", model);

        ChatClient chatClient = this._client.GetChatClient(model);

        ChatCompletionOptions chatOptions = new()
        {
            MaxOutputTokenCount = (int?)(maxTokens ?? 1024),
            Temperature = temperature * 2f,
        };

        int inputTokens = 0;
        int outputTokens = 0;
        StopReason? stopReason = null;
        Exception? streamError = null;

        // Drive the enumerator manually: yield is inside try-finally (no catch) ✓
        // MoveNextAsync exceptions are caught in the inner try-catch (no yield inside) ✓
        var enumerator = chatClient
            .CompleteChatStreamingAsync([new SystemChatMessage(systemPrompt), new UserChatMessage(userPrompt)], chatOptions, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync();
                }
                catch (Exception ex)
                {
                    streamError = ex;
                    break;
                }

                if (!moved)
                {
                    break;
                }

                var update = enumerator.Current;

                if (update.Usage is not null)
                {
                    inputTokens = update.Usage.InputTokenCount;
                    outputTokens = update.Usage.OutputTokenCount;
                }

                if (update.FinishReason is not null)
                {
                    stopReason = FinishReasonConverter.ToStopReason(update.FinishReason.ToString());
                }

                foreach (var part in update.ContentUpdate)
                {
                    if (!string.IsNullOrEmpty(part.Text))
                    {
                        yield return new LlmStreamChunk(part.Text, null, null);
                    }
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
            sw.Stop();

            _durationHistogram.Record(sw.Elapsed.TotalMilliseconds, tags);

            if (streamError is not null)
            {
                _errorCounter.Add(1, tags);
                activity?.SetStatus(ActivityStatusCode.Error, streamError.Message);
                _logger.LogError(streamError, "OpenAI streaming request failed. Model={Model}", model);
            }
            else
            {
                _tokenCounter.Add(inputTokens, new TagList { { "gen_ai.system", "openai" }, { "gen_ai.request.model", model }, { "gen_ai.token.type", "input" } });
                _tokenCounter.Add(outputTokens, new TagList { { "gen_ai.system", "openai" }, { "gen_ai.request.model", model }, { "gen_ai.token.type", "output" } });
                activity?.SetTag("gen_ai.usage.input_tokens", inputTokens);
                activity?.SetTag("gen_ai.usage.output_tokens", outputTokens);
                _logger.LogInformation(
                    "OpenAI streaming request completed. Model={Model}, InputTokens={InputTokens}, OutputTokens={OutputTokens}, DurationMs={DurationMs}",
                    model, inputTokens, outputTokens, sw.Elapsed.TotalMilliseconds);
            }
        }

        if (streamError is not null)
        {
            ExceptionDispatchInfo.Capture(streamError).Throw();
        }

        yield return new LlmStreamChunk(null, stopReason ?? StopReason.Unknown, new LlmTokenUsage(inputTokens, outputTokens));
    }
}

/// <summary>
/// Options for configuring the OpenAI client.
/// </summary>
public class OpenAIClientOptions
{
    /// <summary>
    /// Gets or sets the OpenAI API key.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the API base URL.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// The maximum number of times to retry failed requests.
    /// Defaults to null (uses SDK default).
    /// </summary>
    /// <summary>
    /// Gets or sets the retry count.
    /// </summary>
    public int? MaxRetries { get; set; } = null;

    /// <summary>
    /// Sets the maximum time allowed for a complete HTTP call, not including retries.
    /// Defaults to null (uses SDK default).
    /// </summary>
    /// <summary>
    /// Gets or sets the request timeout.
    /// </summary>
    public TimeSpan? Timeout { get; set; } = null;
}
