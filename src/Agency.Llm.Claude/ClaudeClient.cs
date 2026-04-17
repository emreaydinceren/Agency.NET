namespace Agency.Llm.Claude;

using Agency.Llm.Common;
using Agency.Llm.Common.Tools;
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
using System.Text.Json;
// Anthropic SDK response block types (aliased to resolve ambiguity after removing the namespace import).
using ATextBlock = Anthropic.Models.Messages.TextBlock;
using AThinkingBlock = Anthropic.Models.Messages.ThinkingBlock;
using AToolUseBlock = Anthropic.Models.Messages.ToolUseBlock;
using Model = Common.Model;
using OurContentBlock = Agency.Llm.Common.Messages.ContentBlock;
// Our canonical agent message types (same names as Anthropic SDK types — must be aliased).
using OurMessage = Agency.Llm.Common.Messages.AgentMessage;
using OurRole = Agency.Llm.Common.Messages.MessageRole;
using OurTextBlock = Agency.Llm.Common.Messages.TextBlock;
using OurThinkingBlock = Agency.Llm.Common.Messages.ThinkingBlock;
using OurToolResultBlock = Agency.Llm.Common.Messages.ToolResultBlock;
using OurToolUseBlock = Agency.Llm.Common.Messages.ToolUseBlock;

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
    public ClaudeClient(IOptions<LlmClientOptions> options, ILogger<ClaudeClient>? logger = null)
        : this(options.Value, logger)
    {
    }

    /// <summary>
    /// Creates a client from configured options.
    /// </summary>
    public ClaudeClient(LlmClientOptions clientOptions, ILogger<ClaudeClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(clientOptions);

        this._logger = logger ?? NullLogger<ClaudeClient>.Instance;

        var co = new ClientOptions
        {
            ApiKey = clientOptions.ApiKey,
            MaxRetries = clientOptions.MaxRetries ?? ClientOptions.DefaultMaxRetries,
            Timeout = clientOptions.Timeout ?? ClientOptions.DefaultTimeout,
        };

        if (clientOptions.BaseUrl is not null)
        {
            co.BaseUrl = clientOptions.BaseUrl;
        }

        this._client = new AnthropicClient(co);
    }

    /// <summary>
    /// Creates a client using the default Anthropic client configuration.
    /// </summary>
    public ClaudeClient(ILogger<ClaudeClient>? logger = null)
    {
        this._logger = logger ?? NullLogger<ClaudeClient>.Instance;
        this._client = new AnthropicClient();
    }

    public string ClientType => "Claude";

    /// <summary>
    /// Sends a structured agent request to Claude, converting our canonical message types to Anthropic SDK types and
    /// back.
    /// </summary>
    public async Task<AgentLlmResponse> SendAgentAsync(
        string model,
        string systemPrompt,
        IReadOnlyList<OurMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct = default)
    {
        var anthropicTools = tools
            .Select(static t =>
            {
                var schemaDict = t.InputSchema
                    .EnumerateObject()
                    .ToDictionary(static p => p.Name, static p => p.Value);
                return new Tool
                {
                    Name = t.Name,
                    Description = t.Description,
                    InputSchema = InputSchema.FromRawUnchecked(schemaDict),
                };
            })
            .ToList();

        var anthropicMessages = messages
            .Select(static m => ConvertToAnthropicMessage(m))
            .ToList();

        // MessageCreateParams.Tools expects IReadOnlyList<ToolUnion>; convert each Tool.
        IReadOnlyList<ToolUnion>? toolUnions = anthropicTools.Count > 0
            ? anthropicTools.ConvertAll(static t => (ToolUnion)t)
            : null;

        var request = new MessageCreateParams
        {
            Model = model,
            System = systemPrompt,
            MaxTokens = 8096,
            Messages = anthropicMessages,
            Tools = toolUnions,
        };

        var response = await this._client.Messages.Create(request, ct);

        // Convert response content blocks back to our types.
        var contentBlocks = new List<OurContentBlock>();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out ATextBlock? tb) && tb is not null)
            {
                contentBlocks.Add(new OurTextBlock(tb.Text));
            }
            else if (block.TryPickToolUse(out AToolUseBlock? tub) && tub is not null)
            {
                var inputJson = JsonSerializer.Serialize(tub.Input);
                var inputElement = JsonDocument.Parse(inputJson).RootElement.Clone();
                contentBlocks.Add(new OurToolUseBlock(tub.ID, tub.Name, inputElement));
            }
            else if (block.TryPickThinking(out AThinkingBlock? thinking) && thinking is not null)
            {
                contentBlocks.Add(new OurThinkingBlock(thinking.Thinking));
            }
        }

        var agentMsg = new OurMessage(OurRole.Assistant, contentBlocks);
        var usage = new LlmTokenUsage(response.Usage.InputTokens, response.Usage.OutputTokens);
        var stopReason = FinishReasonConverter.ToStopReason(response.StopReason?.ToString());

        return new AgentLlmResponse(agentMsg, stopReason, usage);
    }

    public async Task<IReadOnlyList<Model>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        List<Model> models = [];
        var modelListPage = await this._client.Models.List(cancellationToken: cancellationToken);
        foreach (var modelInfo in modelListPage.Items)
        {
            string? displayName;
            try
            {
                displayName = modelInfo.DisplayName ?? modelInfo.ID;
            }
            catch (Anthropic.Exceptions.AnthropicInvalidDataException)
            {
                displayName = modelInfo.ID;
            }

            models.Add(new Model(modelInfo.ID, displayName ?? modelInfo.ID));
        }
        return models;
    }

    private static MessageParam ConvertToAnthropicMessage(OurMessage message)
    {
        var role = message.Role == OurRole.User ? Role.User : Role.Assistant;

        // Fast path: single text block → send as a plain string (saves allocation).
        if (message.Content.Count == 1 && message.Content[0] is OurTextBlock singleText)
        {
            return new MessageParam { Role = role, Content = singleText.Text };
        }

        // General path: build a list of typed ContentBlockParams.
        var blocks = new List<ContentBlockParam>(message.Content.Count);
        foreach (var block in message.Content)
        {
            if (block is OurTextBlock tb)
            {
                blocks.Add(new TextBlockParam { Text = tb.Text });
            }
            else if (block is OurToolUseBlock tub)
            {
                var inputDict = tub.Input
                    .EnumerateObject()
                    .ToDictionary(static p => p.Name, static p => p.Value);
                blocks.Add(new ToolUseBlockParam
                {
                    ID = tub.Id,
                    Name = tub.Name,
                    Input = inputDict,
                });
            }
            else if (block is OurToolResultBlock trb)
            {
                blocks.Add(new ToolResultBlockParam
                {
                    ToolUseID = trb.ToolUseId,
                    Content = trb.Content,
                    IsError = trb.IsError,
                });
            }
        }

        return new MessageParam
        {
            Role = role,
            Content = (MessageParamContent)blocks,
        };
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
        if (this._logger.IsEnabled(LogLevel.Information))
        {
            this._logger.LogInformation("Sending request to Claude. Model={Model}", model);
        }

        try
        {
            MessageCreateParams messageToSend = new()
            {
                MaxTokens = maxTokens ?? 1024,
                Model = model,
                System = systemPrompt,
                Messages = [
                    new()
                    {
                        Role = Role.User,
                        Content = userPrompt
                    },
                ],
            };

            var message = await this._client.Messages.Create(messageToSend, cancellationToken);
            message.Validate();

            sw.Stop();
            var durationMs = sw.Elapsed.TotalMilliseconds;
            var inputTokens = message.Usage.InputTokens;
            var outputTokens = message.Usage.OutputTokens;

            _durationHistogram.Record(durationMs, tags);
            _tokenCounter.Add(inputTokens, new TagList { { "gen_ai.system", "anthropic" }, { "gen_ai.request.model", model }, { "gen_ai.token.type", "input" } });
            _tokenCounter.Add(outputTokens, new TagList { { "gen_ai.system", "anthropic" }, { "gen_ai.request.model", model }, { "gen_ai.token.type", "output" } });

            activity?.SetTag("gen_ai.response.model", message.Model);
            activity?.SetTag("gen_ai.usage.input_tokens", inputTokens);
            activity?.SetTag("gen_ai.usage.output_tokens", outputTokens);

            if (this._logger.IsEnabled(LogLevel.Information))
            {
                this._logger.LogInformation(
                    "Claude request completed. Model={Model}, InputTokens={InputTokens}, OutputTokens={OutputTokens}, DurationMs={DurationMs}",
                    model, inputTokens, outputTokens, durationMs);
            }

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
            if (this._logger.IsEnabled(LogLevel.Error))
            {
                this._logger.LogError(ex, "Claude request failed. Model={Model}", model);
            }
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
        if (this._logger.IsEnabled(LogLevel.Information))
        {
            this._logger.LogInformation("Starting streaming request to Claude. Model={Model}", model);
        }

        MessageCreateParams messageToSend = new()
        {
            MaxTokens = maxTokens ?? 1024,
            Model = model,
            System = systemPrompt,
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
        Agency.Llm.Common.StopReason? stopReason = null;
        Exception? streamError = null;

        // Drive the enumerator manually: yield is inside try-finally (no catch) ✓
        // MoveNextAsync exceptions are caught in the inner try-catch (no yield inside) ✓
        var enumerator = this._client.Messages
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
                if (this._logger.IsEnabled(LogLevel.Error))
                {
                    this._logger.LogError(streamError, "Claude streaming request failed. Model={Model}", model);
                }
            }
            else
            {
                _tokenCounter.Add(inputTokens, new TagList { { "gen_ai.system", "anthropic" }, { "gen_ai.request.model", model }, { "gen_ai.token.type", "input" } });
                _tokenCounter.Add(outputTokens, new TagList { { "gen_ai.system", "anthropic" }, { "gen_ai.request.model", model }, { "gen_ai.token.type", "output" } });
                activity?.SetTag("gen_ai.usage.input_tokens", inputTokens);
                activity?.SetTag("gen_ai.usage.output_tokens", outputTokens);
                var durationMs = sw.Elapsed.TotalMilliseconds;
                if (this._logger.IsEnabled(LogLevel.Information))
                {
                    this._logger.LogInformation(
                        "Claude streaming request completed. Model={Model}, InputTokens={InputTokens}, OutputTokens={OutputTokens}, DurationMs={DurationMs}",
                        model, inputTokens, outputTokens, durationMs);
                }
            }
        }

        if (streamError is not null)
        {
            ExceptionDispatchInfo.Capture(streamError).Throw();
        }

        yield return new LlmStreamChunk(null, stopReason ?? Agency.Llm.Common.StopReason.Unknown, new LlmTokenUsage(inputTokens, outputTokens));
    }

    /// <summary>
    /// Streams a structured agent response from Claude, yielding text deltas immediately and complete tool-use blocks
    /// once each tool call's input JSON is fully received.
    /// </summary>
    public async IAsyncEnumerable<AgentStreamChunk> StreamAgentAsync(
        string model,
        string systemPrompt,
        IReadOnlyList<OurMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var anthropicTools = tools
            .Select(static t =>
            {
                var schemaDict = t.InputSchema
                    .EnumerateObject()
                    .ToDictionary(static p => p.Name, static p => p.Value);
                return new Tool
                {
                    Name = t.Name,
                    Description = t.Description,
                    InputSchema = InputSchema.FromRawUnchecked(schemaDict),
                };
            })
            .ToList();

        var anthropicMessages = messages
            .Select(static m => ConvertToAnthropicMessage(m))
            .ToList();

        IReadOnlyList<ToolUnion>? toolUnions = anthropicTools.Count > 0
            ? anthropicTools.ConvertAll(static t => (ToolUnion)t)
            : null;

        var request = new MessageCreateParams
        {
            Model = model,
            System = systemPrompt,
            MaxTokens = 8096,
            Messages = anthropicMessages,
            Tools = toolUnions,
        };

        long inputTokens = 0;
        long outputTokens = 0;
        Agency.Llm.Common.StopReason? stopReason = null;
        Exception? streamError = null;

        // Per-block accumulator for tool-use input JSON (keyed by block index).
        var toolUseById = new System.Collections.Generic.Dictionary<int, (string Id, string Name, System.Text.StringBuilder Json)>();
        int currentBlockIndex = -1;
        bool currentBlockIsToolUse = false;

        var enumerator = this._client.Messages
            .CreateStreaming(request, ct)
            .GetAsyncEnumerator(ct);

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
                else if (e.Value is RawContentBlockStartEvent blockStartEvent)
                {
                    currentBlockIndex++;
                    currentBlockIsToolUse = blockStartEvent.ContentBlock.TryPickToolUse(out AToolUseBlock? toolUseBlock)
                        && toolUseBlock is not null;

                    if (currentBlockIsToolUse && toolUseBlock is not null)
                    {
                        toolUseById[currentBlockIndex] = (toolUseBlock.ID, toolUseBlock.Name, new System.Text.StringBuilder());
                    }
                }
                else if (e.Value is RawContentBlockDeltaEvent contentDeltaEvent)
                {
                    if (contentDeltaEvent.Delta.Value is TextDelta textDelta)
                    {
                        yield return new AgentStreamChunk(textDelta.Text, null, null, null);
                    }
                    else if (contentDeltaEvent.Delta.TryPickInputJson(out InputJsonDelta? jsonDelta)
                             && jsonDelta is not null
                             && toolUseById.TryGetValue(currentBlockIndex, out var tool))
                    {
                        tool.Json.Append(jsonDelta.PartialJson);
                    }
                }
                else if (e.Value is RawContentBlockStopEvent && currentBlockIsToolUse
                         && toolUseById.TryGetValue(currentBlockIndex, out var completedTool))
                {
                    var inputJson = completedTool.Json.Length > 0
                        ? completedTool.Json.ToString()
                        : "{}";
                    var inputElement = JsonDocument.Parse(inputJson).RootElement.Clone();
                    yield return new AgentStreamChunk(null, new OurToolUseBlock(completedTool.Id, completedTool.Name, inputElement), null, null);
                    currentBlockIsToolUse = false;
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync();

            if (streamError is not null)
            {
                if (this._logger.IsEnabled(LogLevel.Error))
                {
                    this._logger.LogError(streamError, "Claude streaming agent request failed. Model={Model}", model);
                }
            }
            else
            {
                if (this._logger.IsEnabled(LogLevel.Information))
                {
                    this._logger.LogInformation(
                        "Claude streaming agent request completed. Model={Model}, InputTokens={InputTokens}, OutputTokens={OutputTokens}",
                        model, inputTokens, outputTokens);
                }
            }
        }

        if (streamError is not null)
        {
            ExceptionDispatchInfo.Capture(streamError).Throw();
        }

        yield return new AgentStreamChunk(null, null, stopReason ?? Agency.Llm.Common.StopReason.Unknown, new LlmTokenUsage(inputTokens, outputTokens));
    }
}
