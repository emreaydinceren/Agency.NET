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
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

/// <summary>
/// A client for interacting with the Anthropic Claude API.
/// </summary>
public class ClaudeClient : ILlmClient
{
    public static readonly string ActivitySourceName = "Agency.Llm.Claude";
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

    public ClaudeClient(ILogger<ClaudeClient>? logger = null)
    {
        _logger = logger ?? NullLogger<ClaudeClient>.Instance;
        this._client = new AnthropicClient();
    }

    public async Task SendAsync(
        string model,
        string content,
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
                Temperature = temperature,
                Messages = [
                    new()
                    {
                        Role = Role.User,
                        Content = content
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

    public async IAsyncEnumerable<string> StreamAsync(
        string model,
        string content,
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
            Temperature = temperature,
            Messages = [
                new()
                {
                    Role = Role.User,
                    Content = content
                },
            ],
        };

        long inputTokens = 0;
        long outputTokens = 0;
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
                    break;

                var e = enumerator.Current;

                if (e.Value is RawMessageStartEvent startEvent)
                    inputTokens = startEvent.Message.Usage.InputTokens;
                else if (e.Value is RawMessageDeltaEvent deltaUsageEvent)
                    outputTokens = deltaUsageEvent.Usage.OutputTokens;
                else if (e.Value is RawContentBlockDeltaEvent contentDeltaEvent &&
                         contentDeltaEvent.Delta.Value is TextDelta textDelta)
                    yield return textDelta.Text;
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
            ExceptionDispatchInfo.Capture(streamError).Throw();
    }
}

public class ClaudeClientOptions
{
    public string ApiKey { get; set; } = string.Empty;

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
    public int? MaxRetries { get; set; } = null;

    /// <summary>
    /// Sets the maximum time allowed for a complete HTTP call, not including retries.
    ///
    /// <para>This includes resolving DNS, connecting, writing the request body, server processing, as
    /// well as reading the response body.</para>
    ///
    /// <para>Defaults to <c>TimeSpan.FromMinutes(10)</c> when null.</para>
    /// </summary>
    public TimeSpan? Timeout { get; set; } = null;
}