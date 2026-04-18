using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Agency.Agentic.Contexts;
using Agency.Agentic.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agency.Agentic;

/// <summary>
/// Drives the agent loop: build system prompt → call LLM → handle tool calls → repeat until a
/// <see cref="StopCondition"/> fires. Yields <see cref="AgentEvent"/>s as they happen.
/// </summary>
public sealed class Agent
{
    public const string ActivitySourceName = "Agency.Agentic.Agent";
    public const string MeterName = "Agency.Agentic.Agent";

    private static readonly ActivitySource _activitySource = new(ActivitySourceName);
    private static readonly Meter _meter = new(MeterName);

    private static readonly Counter<long> _turnCounter = _meter.CreateCounter<long>(
        "agent.turns",
        description: "Total number of agent chat turns");

    private static readonly Counter<long> _errorCounter = _meter.CreateCounter<long>(
        "agent.errors",
        description: "Total number of failed agent turns");

    private static readonly Histogram<double> _turnDurationHistogram = _meter.CreateHistogram<double>(
        "agent.turn.duration",
        unit: "ms",
        description: "Duration of an agent chat turn in milliseconds");

    private static readonly Counter<long> _tokenCounter = _meter.CreateCounter<long>(
        "agent.tokens",
        description: "Number of tokens consumed by the agent");

    private static readonly Counter<long> _toolCallCounter = _meter.CreateCounter<long>(
        "agent.tool.calls",
        description: "Total number of tool calls executed by the agent");

    private readonly IChatClient _llm;
    private readonly string _model;
    private readonly string _clientType;
    private readonly StopCondition _stop;
    private readonly bool _stream;
    private readonly ILogger<Agent> _logger;

    /// <param name="llm">The <see cref="IChatClient"/> used for all LLM calls.</param>
    /// <param name="model">The model identifier forwarded to the provider on every call.</param>
    /// <param name="clientType">Provider display name used in telemetry tags and UI (e.g. "Claude").</param>
    /// <param name="stopWhen">
    /// Predicate evaluated after each turn. Defaults to <c>Any(NoToolCalls, StepCountIs(20))</c>.
    /// </param>
    /// <param name="stream">
    /// When <see langword="true"/> (default), uses the streaming code path and emits <see cref="TextDeltaEvent"/>s as
    /// tokens arrive. Pass <see langword="false"/> to use the simpler batch path.
    /// </param>
    /// <param name="logger">Optional structured logger.</param>
    public Agent(
        IChatClient llm,
        string model,
        string? clientType = null,
        StopCondition? stopWhen = null,
        bool stream = true,
        ILogger<Agent>? logger = null)
    {
        this._llm = llm ?? throw new ArgumentNullException(nameof(llm));
        this._model = model ?? throw new ArgumentNullException(nameof(model));
        this._clientType = clientType ?? "Unknown";
        this._stop = stopWhen ?? StopConditions.Any(StopConditions.NoToolCalls, StopConditions.StepCountIs(20));
        this._stream = stream;
        this._logger = logger ?? NullLogger<Agent>.Instance;
    }

    public string Model => this._model;

    public string ClientType => this._clientType;

    /// <summary>
    /// Creates a new <see cref="Context"/> for a multi-turn conversation session,
    /// pre-populated with temporal context and the initial user prompt.
    /// </summary>
    /// <param name="initialPrompt">The first user message that seeds the conversation.</param>
    /// <param name="tools">Optional tool context; defaults to <see cref="ToolContext.Empty"/>.</param>
    public static Context CreateContext(string initialPrompt, ToolContext? tools = null) =>
        new()
        {
            Query = new QueryContext { Prompt = initialPrompt },
            Temporal = new TemporalContext { CurrentDateUtc = DateTimeOffset.UtcNow },
            Tools = tools ?? ToolContext.Empty,
        };

    /// <summary>
    /// Executes a single user turn within an ongoing conversation. On the first turn
    /// (empty conversation), delegates directly to <see cref="RunAsync"/> which seeds
    /// from <see cref="QueryContext.Prompt"/>. On subsequent turns, appends
    /// <paramref name="userMessage"/> before calling <see cref="RunAsync"/>.
    /// Applies a per-turn timeout when <see cref="AgentOptions.TurnTimeoutSeconds"/> is configured.
    /// </summary>
    public async IAsyncEnumerable<AgentEvent> ChatAsync(
        string userMessage,
        Context ctx,
        AgentOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var activity = _activitySource.StartActivity("Agent.ChatAsync");
        activity?.SetTag("agent.model", this._model);
        activity?.SetTag("agent.client_type", this._clientType);
        activity?.SetTag("agent.stream", this._stream);

        var tags = new TagList
        {
            { "agent.model", this._model },
            { "agent.client_type", this._clientType },
            { "agent.stream", this._stream },
        };

        _turnCounter.Add(1, tags);
        var sw = Stopwatch.StartNew();
        long prevInputTokens = ctx.TotalUsage.InputTokens;
        long prevOutputTokens = ctx.TotalUsage.OutputTokens;

        this._logger.LogInformation(
            "Starting agent chat turn. Model={Model}, ClientType={ClientType}, Stream={Stream}",
            this._model, this._clientType, this._stream);

        if (ctx.Conversation.Messages.Count > 0)
        {
            ctx.Conversation.Append(new ChatMessage(ChatRole.User, userMessage));
        }

        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        int? timeout = options?.TurnTimeoutSeconds;
        if (timeout is > 0)
        {
            turnCts.CancelAfter(TimeSpan.FromSeconds(timeout.Value));
            activity?.SetTag("agent.turn.timeout_seconds", timeout.Value);
        }

        var enumerator = this.RunAsync(ctx, turnCts.Token).GetAsyncEnumerator(turnCts.Token);
        Exception? turnError = null;

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
                    turnError = ex;
                    break;
                }

                if (!moved)
                {
                    break;
                }

                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
            sw.Stop();

            _turnDurationHistogram.Record(sw.Elapsed.TotalMilliseconds, tags);

            if (turnError is not null)
            {
                _errorCounter.Add(1, tags);
                activity?.SetStatus(ActivityStatusCode.Error, turnError.Message);
                this._logger.LogError(
                    turnError,
                    "Agent chat turn failed. Model={Model}, ClientType={ClientType}",
                    this._model, this._clientType);
            }
            else
            {
                long deltaIn = ctx.TotalUsage.InputTokens - prevInputTokens;
                long deltaOut = ctx.TotalUsage.OutputTokens - prevOutputTokens;

                _tokenCounter.Add(deltaIn, new TagList
                {
                    { "agent.model", this._model },
                    { "agent.client_type", this._clientType },
                    { "agent.token.type", "input" },
                });

                _tokenCounter.Add(deltaOut, new TagList
                {
                    { "agent.model", this._model },
                    { "agent.client_type", this._clientType },
                    { "agent.token.type", "output" },
                });

                activity?.SetTag("agent.usage.input_tokens", deltaIn);
                activity?.SetTag("agent.usage.output_tokens", deltaOut);

                this._logger.LogInformation(
                    "Agent chat turn completed. Model={Model}, InputTokens={InputTokens}, OutputTokens={OutputTokens}, DurationMs={DurationMs}",
                    this._model, deltaIn, deltaOut, sw.Elapsed.TotalMilliseconds);
            }
        }

        if (turnError is not null)
        {
            ExceptionDispatchInfo.Capture(turnError).Throw();
        }
    }

    /// <summary>
    /// Runs the agent loop over <paramref name="ctx"/>, yielding events as they occur. The first event is always
    /// <see cref="SessionStartedEvent"/>; the last is always <see cref="AgentResultEvent"/>. When streaming is enabled,
    /// <see cref="TextDeltaEvent"/>s are emitted for each text token.
    /// </summary>
    internal async IAsyncEnumerable<AgentEvent> RunAsync(
        Context ctx,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new SessionStartedEvent(Guid.NewGuid().ToString("N"));

        // 1. Seed conversation with the user prompt if the history is empty.
        if (ctx.Conversation.Messages.Count == 0)
        {
            ctx.Conversation.Append(new ChatMessage(ChatRole.User, ctx.Query.Prompt));
        }

        IReadOnlyList<ToolDefinition> toolDefs = ctx.Tools.Registry.ListDefinitions();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            ctx.IterationCount++;

            // 2. Build a fresh system prompt every iteration.
            string systemPrompt = SystemPromptBuilder.Build(ctx);

            // 3. Build ChatOptions with model, system prompt, max tokens, and tools.
            var options = new ChatOptions
            {
                ModelId = this._model,
                Instructions = systemPrompt,
                MaxOutputTokens = 8096,
            };

            if (toolDefs.Count > 0)
            {
                options.Tools = toolDefs
                    .Select(static t => (AITool)ToolDefinitionFunction.Create(t))
                    .ToList();
            }

            // 4. Call the LLM — streaming or batch depending on configuration.
            ChatMessage lastAssistant;
            LlmTokenUsage turnUsage;

            if (this._stream)
            {
                // Streaming path: iterate chunk by chunk, yielding TextDeltaEvents live.
                // Collect all updates so we can build a ChatResponse for usage after the loop.
                var allUpdates = new List<ChatResponseUpdate>();
                Exception? streamError = null;

                var enumerator = this._llm
                    .GetStreamingResponseAsync(ctx.Conversation.Messages, options, ct)
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
                        allUpdates.Add(update);

                        foreach (var content in update.Contents)
                        {
                            if (content is TextContent tc && !string.IsNullOrEmpty(tc.Text))
                            {
                                yield return new TextDeltaEvent(tc.Text);
                            }
                            else if (content is FunctionCallContent fcc)
                            {
                                yield return new ToolUseReceivedEvent(fcc.Name, fcc.CallId);
                            }
                        }
                    }
                }
                finally
                {
                    await enumerator.DisposeAsync();
                }

                if (streamError is not null)
                {
                    ExceptionDispatchInfo.Capture(streamError).Throw();
                }

                // Aggregate the streamed updates into a full ChatResponse to get usage + message.
                var streamedResponse = allUpdates.ToChatResponse();
                turnUsage = streamedResponse.Usage is { } u
                    ? new LlmTokenUsage(u.InputTokenCount ?? 0, u.OutputTokenCount ?? 0)
                    : new LlmTokenUsage(0, 0);

                lastAssistant = streamedResponse.Messages
                    .LastOrDefault(static m => m.Role == ChatRole.Assistant)
                    ?? new ChatMessage(ChatRole.Assistant, []);
            }
            else
            {
                // Batch path.
                var response = await this._llm.GetResponseAsync(ctx.Conversation.Messages, options, ct);
                lastAssistant = response.Messages.LastOrDefault(static m => m.Role == ChatRole.Assistant)
                    ?? new ChatMessage(ChatRole.Assistant, []);
                turnUsage = response.Usage is { } u
                    ? new LlmTokenUsage(u.InputTokenCount ?? 0, u.OutputTokenCount ?? 0)
                    : new LlmTokenUsage(0, 0);
            }

            ctx.TotalUsage = new(
                ctx.TotalUsage.InputTokens + turnUsage.InputTokens,
                ctx.TotalUsage.OutputTokens + turnUsage.OutputTokens);

            ctx.Conversation.Append(lastAssistant);
            yield return new AssistantTurnEvent(lastAssistant);
            yield return new IterationCompletedEvent(ctx.IterationCount, turnUsage);

            // 5. Evaluate stop conditions.
            if (this._stop(ctx, lastAssistant))
            {
                AgentResultStatus status = DetermineStatus(ctx, lastAssistant);
                string? finalText = ExtractFinalText(lastAssistant);
                yield return new AgentResultEvent(status, finalText, ctx.TotalUsage, ctx.TotalCostUsd);
                yield break;
            }

            // 6. Execute tool calls in parallel.
            var toolCalls = lastAssistant.Contents.OfType<FunctionCallContent>().ToList();
            if (toolCalls.Count == 0)
            {
                // Defensive: stop predicate disagreed with reality — treat as success.
                yield return new AgentResultEvent(
                    AgentResultStatus.Success, ExtractFinalText(lastAssistant),
                    ctx.TotalUsage, ctx.TotalCostUsd);
                yield break;
            }

            var resultMessages = new FunctionResultContent[toolCalls.Count];
            var toolTasks = toolCalls.Select(async (call, index) =>
            {
                ct.ThrowIfCancellationRequested();
                var input = ToJsonElement(call.Arguments);
                ToolResult result;
                try
                {
                    result = await ctx.Tools.Registry.InvokeAsync(call.Name, input, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this._logger.LogWarning(ex, "Tool {Tool} failed", call.Name);
                    result = new ToolResult($"Tool error: {ex.Message}", IsError: true);
                }

                _toolCallCounter.Add(1, new TagList
                {
                    { "agent.model", this._model },
                    { "agent.client_type", this._clientType },
                    { "agent.tool.name", call.Name },
                    { "agent.tool.error", result.IsError },
                });

                var resultContent = result.IsError
                    ? new FunctionResultContent(call.CallId, $"[Error] {result.Content}")
                    : new FunctionResultContent(call.CallId, result.Content);

                resultMessages[index] = resultContent;
                return new ToolInvokedEvent(call.Name, input, result);
            });

            ToolInvokedEvent[] toolEvents = await Task.WhenAll(toolTasks);
            foreach (ToolInvokedEvent evt in toolEvents)
            {
                yield return evt;
            }

            // Add one Tool-role message per result so each callId is paired correctly.
            foreach (var resultContent in resultMessages)
            {
                ctx.Conversation.Append(new ChatMessage(ChatRole.Tool, [resultContent]));
            }
        }
    }

    internal static AgentResultStatus DetermineStatus(Context _, ChatMessage last)
    {
        return last.Contents.OfType<FunctionCallContent>().Any()
            ? AgentResultStatus.MaxStepsReached
            : AgentResultStatus.Success;
    }

    internal static string? ExtractFinalText(ChatMessage msg)
    {
        string? text = string.Concat(msg.Contents.OfType<TextContent>().Select(static t => t.Text));
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static readonly JsonElement _emptyElement =
        JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>(),
            AgentJsonContext.Default.DictionaryStringObject);

    internal static JsonElement ToJsonElement(IDictionary<string, object?>? arguments)
    {
        if (arguments is null or { Count: 0 })
        {
            return _emptyElement;
        }

        return JsonSerializer.SerializeToElement(arguments);
    }
}
