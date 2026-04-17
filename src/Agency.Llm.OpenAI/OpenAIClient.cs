namespace Agency.Llm.OpenAI;

using Agency.Llm.Common;
using Agency.Llm.Common.Tools;
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
using System.Text.Json;

using OurMessage = Agency.Llm.Common.Messages.AgentMessage;
using OurRole = Agency.Llm.Common.Messages.MessageRole;
using OurTextBlock = Agency.Llm.Common.Messages.TextBlock;
using OurToolUseBlock = Agency.Llm.Common.Messages.ToolUseBlock;
using OurToolResultBlock = Agency.Llm.Common.Messages.ToolResultBlock;

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
    public OpenAIClient(IOptions<LlmClientOptions> options, ILogger<OpenAIClient>? logger = null)
        : this (options.Value, logger)
    {
    }

    /// <summary>
    /// Creates a client from configured options.
    /// </summary>
    public OpenAIClient(LlmClientOptions clientOptions, ILogger<OpenAIClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(clientOptions);

        this._logger = logger ?? NullLogger<OpenAIClient>.Instance;

        var credential = new ApiKeyCredential(clientOptions.ApiKey);
        var openAIClientOptions = new global::OpenAI.OpenAIClientOptions();

        if (clientOptions.BaseUrl is not null)
        {
            openAIClientOptions.Endpoint = new Uri(clientOptions.BaseUrl);
        }

        if (clientOptions.Timeout is { } timeout && timeout > TimeSpan.Zero)
        {
            openAIClientOptions.NetworkTimeout = timeout;
        }

        this._client = new global::OpenAI.OpenAIClient(credential, openAIClientOptions);
    }

    /// <summary>
    /// Creates a client using the <c> OPENAI_API_KEY</c> environment variable.
    /// </summary>
    public OpenAIClient(ILogger<OpenAIClient>? logger = null)
    {
        this._logger = logger ?? NullLogger<OpenAIClient>.Instance;
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
        this._client = new global::OpenAI.OpenAIClient(new ApiKeyCredential(apiKey));
    }

    public string ClientType => "OpenAI";

    /// <summary>
    /// Sends a structured agent request to OpenAI, converting our canonical message types to OpenAI SDK types and back.
    /// </summary>
    public async Task<AgentLlmResponse> SendAgentAsync(
        string model,
        string systemPrompt,
        IReadOnlyList<OurMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct = default)
    {
        ChatClient chatClient = this._client.GetChatClient(model);

        var chatMessages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
        };
        foreach (var msg in messages)
        {
            chatMessages.AddRange(ConvertToOpenAIMessages(msg));
        }

        var options = new ChatCompletionOptions { MaxOutputTokenCount = 8096 };
        if (tools.Count > 0)
        {
            foreach (var tool in tools)
            {
                options.Tools.Add(ChatTool.CreateFunctionTool(
                    tool.Name,
                    tool.Description,
                    BinaryData.FromString(tool.InputSchema.GetRawText())));
            }
        }

        var result = await chatClient.CompleteChatAsync(chatMessages, options, ct);
        var completion = result.Value;

        // Build content blocks from text + tool calls.
        var contentBlocks = new List<Agency.Llm.Common.Messages.ContentBlock>();

        var text = string.Concat(completion.Content.Select(static p => p.Text ?? string.Empty));
        if (!string.IsNullOrEmpty(text))
        {
            contentBlocks.Add(new OurTextBlock(text));
        }

        if (completion.ToolCalls is { Count: > 0 } toolCalls)
        {
            foreach (var call in toolCalls)
            {
                var inputElement = JsonDocument.Parse(call.FunctionArguments.ToString()).RootElement.Clone();
                contentBlocks.Add(new OurToolUseBlock(call.Id, call.FunctionName, inputElement));
            }
        }

        var agentMsg = new OurMessage(OurRole.Assistant, contentBlocks);
        var usage = new LlmTokenUsage(
            completion.Usage?.InputTokenCount ?? 0,
            completion.Usage?.OutputTokenCount ?? 0);
        var stopReason = FinishReasonConverter.ToStopReason(completion.FinishReason.ToString());

        return new AgentLlmResponse(agentMsg, stopReason, usage);
    }

    /// <summary>
    /// Converts one of our canonical <see cref="OurMessage"/> objects to the OpenAI SDK <see cref="ChatMessage"/>
    /// types. User messages containing tool results are expanded into multiple <see cref="ToolChatMessage"/> objects
    /// (one per result), because the OpenAI API represents each tool result as a separate top-level message.
    /// </summary>
    private static IEnumerable<ChatMessage> ConvertToOpenAIMessages(OurMessage message)
    {
        if (message.Role == OurRole.Assistant)
        {
            var toolCalls = message.Content
                .OfType<OurToolUseBlock>()
                .Select(static tub => ChatToolCall.CreateFunctionToolCall(
                    tub.Id,
                    tub.Name,
                    BinaryData.FromString(tub.Input.GetRawText())))
                .ToList();

            if (toolCalls.Count > 0)
            {
                yield return new AssistantChatMessage(toolCalls);
            }
            else
            {
                var assistantText = string.Concat(
                    message.Content.OfType<OurTextBlock>().Select(static t => t.Text));
                yield return new AssistantChatMessage(assistantText);
            }
        }
        else // User (initial prompt or tool results)
        {
            var toolResults = message.Content.OfType<OurToolResultBlock>().ToList();
            if (toolResults.Count > 0)
            {
                // Each tool result is a separate ToolChatMessage in the OpenAI protocol.
                foreach (var trb in toolResults)
                {
                    yield return new ToolChatMessage(trb.ToolUseId, trb.Content);
                }
            }
            else
            {
                var userText = string.Concat(
                    message.Content.OfType<OurTextBlock>().Select(static t => t.Text));
                yield return new UserChatMessage(userText);
            }
        }
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
        this._logger.LogInformation("Sending request to OpenAI. Model={Model}", model);

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

            this._logger.LogInformation(
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
            this._logger.LogError(ex, "OpenAI request failed. Model={Model}", model);
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
        this._logger.LogInformation("Starting streaming request to OpenAI. Model={Model}", model);

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
                this._logger.LogError(streamError, "OpenAI streaming request failed. Model={Model}", model);
            }
            else
            {
                _tokenCounter.Add(inputTokens, new TagList { { "gen_ai.system", "openai" }, { "gen_ai.request.model", model }, { "gen_ai.token.type", "input" } });
                _tokenCounter.Add(outputTokens, new TagList { { "gen_ai.system", "openai" }, { "gen_ai.request.model", model }, { "gen_ai.token.type", "output" } });
                activity?.SetTag("gen_ai.usage.input_tokens", inputTokens);
                activity?.SetTag("gen_ai.usage.output_tokens", outputTokens);
                this._logger.LogInformation(
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

    /// <summary>
    /// Streams a structured agent response from OpenAI, yielding text deltas immediately and complete tool-use blocks
    /// once each function call's arguments are fully received.
    /// </summary>
    public async IAsyncEnumerable<AgentStreamChunk> StreamAgentAsync(
        string model,
        string systemPrompt,
        IReadOnlyList<OurMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ChatClient chatClient = this._client.GetChatClient(model);

        var chatMessages = new List<ChatMessage> { new SystemChatMessage(systemPrompt) };
        foreach (var msg in messages)
        {
            chatMessages.AddRange(ConvertToOpenAIMessages(msg));
        }

        var options = new ChatCompletionOptions { MaxOutputTokenCount = 8096 };
        if (tools.Count > 0)
        {
            foreach (var tool in tools)
            {
                options.Tools.Add(ChatTool.CreateFunctionTool(
                    tool.Name,
                    tool.Description,
                    BinaryData.FromString(tool.InputSchema.GetRawText())));
            }
        }

        int inputTokens = 0;
        int outputTokens = 0;
        StopReason? stopReason = null;
        Exception? streamError = null;

        // Accumulate tool call deltas by index: each entry is (id, name, argsBuilder).
        var toolCallAccumulators = new System.Collections.Generic.Dictionary<int, (string Id, System.Text.StringBuilder Name, System.Text.StringBuilder Args)>();

        var enumerator = chatClient
            .CompleteChatStreamingAsync(chatMessages, options, ct)
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
                        yield return new AgentStreamChunk(part.Text, null, null, null);
                    }
                }

                foreach (var toolCallUpdate in update.ToolCallUpdates)
                {
                    if (!toolCallAccumulators.TryGetValue(toolCallUpdate.Index, out var acc))
                    {
                        acc = (toolCallUpdate.ToolCallId ?? string.Empty, new System.Text.StringBuilder(), new System.Text.StringBuilder());
                        toolCallAccumulators[toolCallUpdate.Index] = acc;
                    }

                    if (!string.IsNullOrEmpty(toolCallUpdate.FunctionName))
                    {
                        acc.Name.Append(toolCallUpdate.FunctionName);
                    }

                    if (toolCallUpdate.FunctionArgumentsUpdate is not null)
                    {
                        acc.Args.Append(toolCallUpdate.FunctionArgumentsUpdate);
                    }

                    // Update the accumulator (structs are value types — reassign).
                    toolCallAccumulators[toolCallUpdate.Index] = (
                        string.IsNullOrEmpty(acc.Id) && !string.IsNullOrEmpty(toolCallUpdate.ToolCallId)
                            ? toolCallUpdate.ToolCallId!
                            : acc.Id,
                        acc.Name,
                        acc.Args);
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync();

            if (streamError is not null)
            {
                this._logger.LogError(streamError, "OpenAI streaming agent request failed. Model={Model}", model);
            }
            else
            {
                this._logger.LogInformation(
                    "OpenAI streaming agent request completed. Model={Model}, InputTokens={InputTokens}, OutputTokens={OutputTokens}",
                    model, inputTokens, outputTokens);
            }
        }

        if (streamError is not null)
        {
            ExceptionDispatchInfo.Capture(streamError).Throw();
        }

        // Yield completed tool-use blocks.
        foreach (var entry in toolCallAccumulators.OrderBy(static kv => kv.Key))
        {
            var argsJson = entry.Value.Args.Length > 0 ? entry.Value.Args.ToString() : "{}";
            var inputElement = JsonDocument.Parse(argsJson).RootElement.Clone();
            yield return new AgentStreamChunk(null, new OurToolUseBlock(entry.Value.Id, entry.Value.Name.ToString(), inputElement), null, null);
        }

        yield return new AgentStreamChunk(null, null, stopReason ?? StopReason.Unknown, new LlmTokenUsage(inputTokens, outputTokens));
    }

    public async Task<IReadOnlyList<Model>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        List<Model> models = new();
        var result = await this._client.GetOpenAIModelClient().GetModelsAsync(cancellationToken);
        foreach (var model in result.Value)
        {
            models.Add(new Model(model.Id, model.Id));
        }
        return models;
    }
}
