namespace Agency.Llm.Claude;

using Agency.Llm.Abstractions;
using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

/// <summary>
/// A client for interacting with the Anthropic Claude API.
/// </summary>
public class ClaudeClient : ILlmClient
{
    /// <summary>
    /// The activity source name used for Claude telemetry.
    /// </summary>
    public static readonly string ActivitySourceName = "Agency.Llm.Claude";

    /// <summary>
    /// The meter name used for Claude telemetry.
    /// </summary>
    public static readonly string MeterName = "Agency.Llm.Claude";

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

    private readonly AnthropicClient _client;

    private readonly ILogger<ClaudeClient> _logger;

    /// <summary>
    /// Creates a client from configured options.
    /// </summary>
    public ClaudeClient(IOptions<ClaudeClientOptions> options, ILogger<ClaudeClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _logger = logger ?? NullLogger<ClaudeClient>.Instance;

        var co = new ClientOptions
        {
            ApiKey = options.Value.ApiKey,
            MaxRetries = options.Value.MaxRetries ?? ClientOptions.DefaultMaxRetries,
            Timeout = options.Value.Timeout ?? ClientOptions.DefaultTimeout,
        };

        if (options.Value.BaseUrl is not null)
        {
            co.BaseUrl = options.Value.BaseUrl;
        }

        this._client = new AnthropicClient(co);
    }

    /// <summary>
    /// Creates a client using the default Anthropic client configuration.
    /// </summary>
    public ClaudeClient(ILogger<ClaudeClient>? logger = null)
    {
        _logger = logger ?? NullLogger<ClaudeClient>.Instance;
        this._client = new AnthropicClient();
    }

    /// <summary>
    /// Sends a completion request to Claude.
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
        activity?.SetTag("gen_ai.system", "anthropic");
        activity?.SetTag("gen_ai.request.model", model);

        var tags = new TagList { { "gen_ai.system", "anthropic" }, { "gen_ai.request.model", model }, { "llm.method", "send" } };
        _requestCounter.Add(1, tags);

        var sw = Stopwatch.StartNew();
        _logger.LogInformation("Sending request to Claude. Model={Model}", model);

        try
        {
            MessageCreateParams messageToSend = new()
            {
                MaxTokens = maxTokens ?? 1024,
                Model = model,
                System = systemPrompt,
                Temperature = temperature,
                Messages = [
                    new()
                    {
                        Role = Role.User,
                        Content = userPrompt
                    },
                ],
            };

            var message = await _client.Messages.Create(messageToSend, cancellationToken);
            message.Validate();

            sw.Stop();
            var inputTokens = message.Usage.InputTokens;
            var outputTokens = message.Usage.OutputTokens;

            _durationHistogram.Record(sw.Elapsed.TotalMilliseconds, tags);
            _tokenCounter.Add(inputTokens, new TagList { { "gen_ai.system", "anthropic" }, { "gen_ai.request.model", model }, { "gen_ai.token.type", "input" } });
            _tokenCounter.Add(outputTokens, new TagList { { "gen_ai.system", "anthropic" }, { "gen_ai.request.model", model }, { "gen_ai.token.type", "output" } });

            activity?.SetTag("gen_ai.response.model", message.Model);
            activity?.SetTag("gen_ai.usage.input_tokens", inputTokens);
            activity?.SetTag("gen_ai.usage.output_tokens", outputTokens);

            _logger.LogInformation(
                "Claude request completed. Model={Model}, InputTokens={InputTokens}, OutputTokens={OutputTokens}, DurationMs={DurationMs}",
                model, inputTokens, outputTokens, sw.Elapsed.TotalMilliseconds);

            var messageText = string.Concat(message.Content.Select(static block => ExtractBlockText(block)));
            return new LlmResponse(
                messageText,
                FinishReasonConverter.ToStopReason(message.StopReason?.ToString()),
                new LlmTokenUsage(inputTokens, outputTokens));
        }
        catch (Exception ex)
        {
            sw.Stop();
            _durationHistogram.Record(sw.Elapsed.TotalMilliseconds, tags);
            _errorCounter.Add(1, tags);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Claude request failed. Model={Model}", model);
            throw;
        }
    }

    private static string ExtractBlockText(object? block)
    {
        if (block is null)
        {
            return string.Empty;
        }

        var blockType = block.GetType();
        if (blockType.GetProperty("Text")?.GetValue(block) is string text)
        {
            return text;
        }

        var value = blockType.GetProperty("Value")?.GetValue(block);
        if (value is null)
        {
            return string.Empty;
        }

        return value.GetType().GetProperty("Text")?.GetValue(value) as string ?? string.Empty;
    }

    /// <summary>
    /// Streams a completion response from Claude.
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
        activity?.SetTag("gen_ai.system", "anthropic");
        activity?.SetTag("gen_ai.request.model", model);

        var tags = new TagList { { "gen_ai.system", "anthropic" }, { "gen_ai.request.model", model }, { "llm.method", "stream" } };
        _requestCounter.Add(1, tags);

        var sw = Stopwatch.StartNew();
        _logger.LogInformation("Starting streaming request to Claude. Model={Model}", model);

        MessageCreateParams messageToSend = new()
        {
            MaxTokens = maxTokens ?? 1024,
            Model = model,
            System = systemPrompt,
            Temperature = temperature,
            Messages = [
                new()
                {
                    Role = Role.User,
                    Content = userPrompt
                },
            ],
        };

        long inputTokens = 0;
        long outputTokens = 0;
        Agency.Llm.Abstractions.StopReason? stopReason = null;
        Exception? streamError = null;

        // Drive the enumerator manually: yield is inside try-finally (no catch) ✓
        // MoveNextAsync exceptions are caught in the inner try-catch (no yield inside) ✓
        var enumerator = _client.Messages
            .CreateStreaming(messageToSend, cancellationToken)
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

                var e = enumerator.Current;

                if (e.Value is RawMessageStartEvent startEvent)
                {
                    inputTokens = startEvent.Message.Usage.InputTokens;
                }
                else if (e.Value is RawMessageDeltaEvent deltaUsageEvent)
                {
                    outputTokens = deltaUsageEvent.Usage.OutputTokens;
                    stopReason = FinishReasonConverter.ToNullableStopReason(deltaUsageEvent.Delta.StopReason?.ToString());
                }
                else if (e.Value is RawContentBlockDeltaEvent contentDeltaEvent &&
                         contentDeltaEvent.Delta.Value is TextDelta textDelta)
                {
                    yield return new LlmStreamChunk(textDelta.Text, null, null);
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
                _logger.LogError(streamError, "Claude streaming request failed. Model={Model}", model);
            }
            else
            {
                _tokenCounter.Add(inputTokens, new TagList { { "gen_ai.system", "anthropic" }, { "gen_ai.request.model", model }, { "gen_ai.token.type", "input" } });
                _tokenCounter.Add(outputTokens, new TagList { { "gen_ai.system", "anthropic" }, { "gen_ai.request.model", model }, { "gen_ai.token.type", "output" } });
                activity?.SetTag("gen_ai.usage.input_tokens", inputTokens);
                activity?.SetTag("gen_ai.usage.output_tokens", outputTokens);
                _logger.LogInformation(
                    "Claude streaming request completed. Model={Model}, InputTokens={InputTokens}, OutputTokens={OutputTokens}, DurationMs={DurationMs}",
                    model, inputTokens, outputTokens, sw.Elapsed.TotalMilliseconds);
            }
        }

        if (streamError is not null)
        {
            ExceptionDispatchInfo.Capture(streamError).Throw();
        }

        yield return new LlmStreamChunk(null, stopReason ?? Agency.Llm.Abstractions.StopReason.Unknown, new LlmTokenUsage(inputTokens, outputTokens));
    }
}

/// <summary>
/// Options for configuring the Claude client.
/// </summary>
public class ClaudeClientOptions
{
    /// <summary>
    /// Gets or sets the Anthropic API key.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the API base URL.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// The maximum number of times to retry failed requests, with a short exponential backoff between requests.
    ///
    /// <para>
    /// Only the following error types are retried:
    /// <list type="bullet">
    ///   <item>Connection errors (for example, due to a network connectivity problem)</item>
    ///   <item>408 Request Timeout</item>
    ///   <item>409 Conflict</item>
    ///   <item>429 Rate Limit</item>
    ///   <item>5xx Internal</item>
    /// </list>
    /// </para>
    ///
    /// <para>The API may also explicitly instruct the SDK to retry or not retry a request.</para>
    ///
    /// <para>Defaults to 2 when null. Set to 0 to
    /// disable retries, which also ignores API instructions to retry.</para>
    /// </summary>
    /// <summary>
    /// Gets or sets the retry count.
    /// </summary>
    public int? MaxRetries { get; set; } = null;

    /// <summary>
    /// Sets the maximum time allowed for a complete HTTP call, not including retries.
    ///
    /// <para>This includes resolving DNS, connecting, writing the request body, server processing, as
    /// well as reading the response body.</para>
    ///
    /// <para>Defaults to <c>TimeSpan.FromMinutes(10)</c> when null.</para>
    /// </summary>
    /// <summary>
    /// Gets or sets the request timeout.
    /// </summary>
    public TimeSpan? Timeout { get; set; } = null;
}