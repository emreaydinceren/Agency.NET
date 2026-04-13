using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Agency.Agentic;

/// <summary>
/// Drives the agent loop: build system prompt → call LLM → handle tool calls → repeat until a
/// <see cref="StopCondition"/> fires. Yields <see cref="AgentEvent"/> s as they happen.
/// </summary>
public sealed class Agent
{
    private readonly ILlmClient _llm;
    private readonly string _model;
    private readonly StopCondition _stop;
    private readonly bool _stream;
    private readonly ILogger<Agent>? _logger;

    /// <param name="llm">The LLM client; must implement <see cref="ILlmClient.SendAgentAsync"/>.</param>
    /// <param name="model">The model identifier forwarded to the provider on every call.</param>
    /// <param name="stopWhen">
    /// Predicate evaluated after each turn. Defaults to <c> Any(NoToolCalls, StepCountIs(20))</c>.
    /// </param>
    /// <param name="stream">
    /// When <see langword="true"/> (default), uses the streaming code path and emits <see cref="TextDeltaEvent"/> s as
    /// tokens arrive. Pass <see langword="false"/> to use the simpler <c> SendAgentAsync</c> batch path.
    /// </param>
    /// <param name="logger">Optional structured logger.</param>
    public Agent(
        ILlmClient llm,
        string model,
        StopCondition? stopWhen = null,
        bool stream = true,
        ILogger<Agent>? logger = null)
    {
        this._llm = llm ?? throw new ArgumentNullException(nameof(llm));
        this._model = model ?? throw new ArgumentNullException(nameof(model));
        this._stop = stopWhen ?? StopConditions.Any(StopConditions.NoToolCalls, StopConditions.StepCountIs(20));
        this._stream = stream;
        this._logger = logger;
    }

    public string Model => this._model;

    public string ClientType => this._llm.ClientType;

    /// <summary>
    /// Runs the agent loop over <paramref name="ctx"/>, yielding events as they occur. The first event is always
    /// <see cref="SessionStartedEvent"/>; the last is always <see cref="AgentResultEvent"/>. When streaming is enabled,
    /// <see cref="TextDeltaEvent"/> s are emitted for each text token.
    /// </summary>
    public async IAsyncEnumerable<AgentEvent> RunAsync(
        Context ctx,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new SessionStartedEvent(Guid.NewGuid().ToString("N"));

        // 1. Seed conversation with the user prompt if the history is empty.
        if (ctx.Conversation.Messages.Count == 0)
        {
            ctx.Conversation.Append(new AgentMessage(
                MessageRole.User,
                [new TextBlock(ctx.Query.Prompt)]));
        }

        IReadOnlyList<ToolDefinition> tools = ctx.Tools.Registry.ListDefinitions();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            ctx.IterationCount++;

            // 2. Build a fresh system prompt every iteration (D3).
            string systemPrompt = SystemPromptBuilder.Build(ctx);

            // 3. Call the LLM — streaming or batch depending on configuration.
            AgentMessage lastAssistant;
            LlmTokenUsage turnUsage;

            if (this._stream)
            {
                // Streaming path: iterate chunk by chunk, yielding TextDeltaEvents live, then
                // reconstruct the full AgentMessage from accumulated text and tool-use blocks.
                var textBuilder = new System.Text.StringBuilder();
                var streamedBlocks = new List<ContentBlock>();
                turnUsage = new LlmTokenUsage(0, 0);

                await foreach (AgentStreamChunk chunk in this._llm.StreamAgentAsync(
                    this._model, systemPrompt, ctx.Conversation.Messages, tools, ct))
                {
                    if (chunk.Text is not null)
                    {
                        textBuilder.Append(chunk.Text);
                        yield return new TextDeltaEvent(chunk.Text);
                    }
                    else if (chunk.ToolUse is not null)
                    {
                        streamedBlocks.Add(chunk.ToolUse);
                    }
                    else if (chunk.StopReason is not null && chunk.Usage is not null)
                    {
                        turnUsage = chunk.Usage;
                    }
                }

                if (textBuilder.Length > 0)
                {
                    streamedBlocks.Insert(0, new TextBlock(textBuilder.ToString()));
                }

                lastAssistant = new AgentMessage(MessageRole.Assistant, streamedBlocks);
            }
            else
            {
                AgentLlmResponse response = await this._llm.SendAgentAsync(
                    this._model, systemPrompt, ctx.Conversation.Messages, tools, ct);
                lastAssistant = response.Message;
                turnUsage = response.Usage;
            }

            ctx.TotalUsage = new(
                ctx.TotalUsage.InputTokens + turnUsage.InputTokens,
                ctx.TotalUsage.OutputTokens + turnUsage.OutputTokens);

            ctx.Conversation.Append(lastAssistant);
            yield return new AssistantTurnEvent(lastAssistant);
            yield return new IterationCompletedEvent(ctx.IterationCount, turnUsage);

            // 4. Evaluate stop conditions.
            if (this._stop(ctx, lastAssistant))
            {
                AgentResultStatus status = DetermineStatus(ctx, lastAssistant);
                string? finalText = ExtractFinalText(lastAssistant);
                yield return new AgentResultEvent(status, finalText, ctx.TotalUsage, ctx.TotalCostUsd);
                yield break;
            }

            // 5. Execute tool calls in parallel (D9).
            var toolUses = lastAssistant.Content.OfType<ToolUseBlock>().ToList();
            if (toolUses.Count == 0)
            {
                // Defensive: stop predicate disagreed with reality — treat as success.
                yield return new AgentResultEvent(
                    AgentResultStatus.Success, ExtractFinalText(lastAssistant),
                    ctx.TotalUsage, ctx.TotalCostUsd);
                yield break;
            }

            var resultBlocks = new ContentBlock[toolUses.Count];
            var toolTasks = toolUses.Select(async (use, index) =>
            {
                ct.ThrowIfCancellationRequested();
                ToolResult result;
                try
                {
                    result = await ctx.Tools.Registry.InvokeAsync(use.Name, use.Input, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this._logger?.LogWarning(ex, "Tool {Tool} failed", use.Name);
                    result = new ToolResult($"Tool error: {ex.Message}", IsError: true);
                }

                resultBlocks[index] = new ToolResultBlock(use.Id, result.Content, result.IsError);
                return new ToolInvokedEvent(use.Name, use.Input, result);
            });

            ToolInvokedEvent[] toolEvents = await Task.WhenAll(toolTasks);
            foreach (ToolInvokedEvent evt in toolEvents)
            {
                yield return evt;
            }

            ctx.Conversation.Append(new AgentMessage(MessageRole.User, resultBlocks));
        }
    }

    internal static AgentResultStatus DetermineStatus(Context _, AgentMessage last)
    {
        // If the assistant's last message still contains pending tool calls, the agent
        // was stopped mid-flight by a stop condition — regardless of which one fired.
        return last.Content.OfType<ToolUseBlock>().Any()
            ? AgentResultStatus.MaxStepsReached
            : AgentResultStatus.Success;
    }

    internal static string? ExtractFinalText(AgentMessage msg)
    {
        string? text = string.Concat(msg.Content.OfType<TextBlock>().Select(t => t.Text));
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
