using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Agency.Harness.Contexts;
using Agency.Harness.Hooks;
using Agency.Harness.Permissions;
using Agency.Harness.Skills;
using Agency.Harness.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agency.Harness;

/// <summary>
/// Drives the agent loop: build system prompt → call LLM → handle tool calls → repeat until a
/// <see cref="StopCondition"/> fires. Yields <see cref="AgentEvent"/>s as they happen.
/// </summary>
public sealed class Agent
{
    public const string ActivitySourceName = "Agency.Harness.Agent";
    public const string MeterName = "Agency.Harness.Agent";

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
    private readonly ILogger<Agent> _logger;
    private readonly AgentHooks _hooks;
    private readonly IPermissionEvaluator? _permissions;
    private readonly TimeProvider _timeProvider;
    private readonly bool _logToolPayloads;

    /// <param name="llm">The <see cref="IChatClient"/> used for all LLM calls.</param>
    /// <param name="model">The model identifier forwarded to the provider on every call.</param>
    /// <param name="clientType">Provider display name used in telemetry tags and UI (e.g. "Claude").</param>
    /// <param name="stopWhen">
    /// Predicate evaluated after each turn. Defaults to <c>Any(NoToolCalls, StepCountIs(20))</c>.
    /// </param>
    /// <param name="hooks">Optional lifecycle hook delegates; defaults to <see cref="AgentHooks.None"/>.</param>
    /// <param name="permissions">
    /// Optional permission evaluator. When supplied, every tool call is evaluated against
    /// configured allow/deny rules before invocation; unresolved calls park the turn.
    /// When null, the rules layer is absent and behavior is unchanged — hook <c>Ask</c> results
    /// still park the turn (park/resume is agent machinery, not evaluator machinery).
    /// </param>
    /// <param name="logger">Optional structured logger.</param>
    /// <param name="timeProvider">
    /// Optional clock used for temporal grounding in the system prompt. Defaults to
    /// <see cref="TimeProvider.System"/>. Functional tests inject a pinned provider so the
    /// "Current date/time" line is byte-stable across runs (required for HTTP-cache replay).
    /// </param>
    /// <param name="logToolPayloads">
    /// When true, tool-call inputs and tool error-result content are written to the log verbatim.
    /// Verbose and potentially sensitive, so it is opt-in (host wires it from
    /// <see cref="AgentOptions.LogToolPayloads"/>). When false, payloads are redacted but tool
    /// calls and failures are still logged by name.
    /// </param>
    public Agent(
        IChatClient llm,
        string model,
        string? clientType = null,
        StopCondition? stopWhen = null,
        AgentHooks? hooks = null,
        IPermissionEvaluator? permissions = null,
        ILogger<Agent>? logger = null,
        TimeProvider? timeProvider = null,
        bool logToolPayloads = false)
    {
        this._llm = llm ?? throw new ArgumentNullException(nameof(llm));
        this._model = model ?? throw new ArgumentNullException(nameof(model));
        this._clientType = clientType ?? "Unknown";
        this._stop = stopWhen ?? StopConditions.Any(StopConditions.NoToolCalls, StopConditions.StepCountIs(20));
        this._hooks = hooks ?? AgentHooks.None;
        this._permissions = permissions;
        this._logger = logger ?? NullLogger<Agent>.Instance;
        this._timeProvider = timeProvider ?? TimeProvider.System;
        this._logToolPayloads = logToolPayloads;
    }

    private const string RedactedPayload = "(redacted; set Agent:LogToolPayloads=true to log)";

    /// <summary>
    /// Returns the tool input rendered for logging: the raw JSON when <see cref="AgentOptions.LogToolPayloads"/>
    /// is enabled, otherwise a redaction marker. Keeps payloads out of logs by default.
    /// </summary>
    private string ForLog(JsonElement input) =>
        this._logToolPayloads ? input.GetRawText() : RedactedPayload;

    /// <summary>
    /// Returns a tool's error-result content for logging: the content verbatim when payload logging is
    /// enabled, otherwise a redaction marker. The fact that an error occurred is always logged.
    /// </summary>
    private string ForLog(string content) =>
        this._logToolPayloads ? content : RedactedPayload;

    public string Model => this._model;

    public string ClientType => this._clientType;

    /// <summary>Gets the clock used for temporal grounding; <see cref="TimeProvider.System"/> unless overridden.</summary>
    internal TimeProvider TimeProvider => this._timeProvider;

    /// <summary>
    /// Fires the <see cref="AgentHooks.OnSessionEnd"/> hook for the given context, if one is set.
    /// Called by <see cref="ChatSession.DisposeAsync"/> at the end of a session.
    /// </summary>
    public Task RaiseSessionEndAsync(Context ctx, CancellationToken ct = default) =>
        this._hooks.OnSessionEnd is { } onSessionEnd
            ? onSessionEnd(new SessionEndedHookContext(ctx.Session.Id ?? string.Empty, ctx), ct)
            : Task.CompletedTask;

    /// <summary>
    /// Creates a new <see cref="Context"/> for a multi-turn conversation session,
    /// pre-populated with temporal context and the initial user prompt.
    /// </summary>
    /// <param name="initialPrompt">The first user message that seeds the conversation.</param>
    /// <param name="tools">Optional tool context; defaults to <see cref="ToolContext.Empty"/>.</param>
    /// <param name="environment">Optional environmental context; defaults to <see cref="EnvironmentalContext.Empty"/>.</param>
    /// <param name="user">Optional caller identity; defaults to <see cref="UserSpecificContext.Empty"/>.</param>
    /// <param name="timeProvider">Optional clock for temporal grounding; defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="skills">Optional skill context; defaults to <see cref="SkillContext.Empty"/>.</param>
    /// <param name="session">Optional pre-seeded session context; defaults to <see cref="SessionContext.Empty"/>.</param>
    public static Context CreateContext(
        string initialPrompt,
        ToolContext? tools = null,
        EnvironmentalContext? environment = null,
        UserSpecificContext? user = null,
        TimeProvider? timeProvider = null,
        SkillContext? skills = null,
        SessionContext? session = null) =>
        new()
        {
            Query = new QueryContext { Prompt = initialPrompt },
            Temporal = new TemporalContext { CurrentDateUtc = (timeProvider ?? TimeProvider.System).GetUtcNow() },
            Tools = tools ?? ToolContext.Empty,
            Environment = environment ?? EnvironmentalContext.Empty,
            User = user ?? UserSpecificContext.Empty,
            Skills = skills ?? SkillContext.Empty,
            Session = session ?? SessionContext.Empty,
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

        var tags = new TagList
        {
            { "agent.model", this._model },
            { "agent.client_type", this._clientType },
        };

        _turnCounter.Add(1, tags);
        var sw = Stopwatch.StartNew();
        long prevInputTokens = ctx.TotalUsage.InputTokens;
        long prevOutputTokens = ctx.TotalUsage.OutputTokens;

        this._logger.LogInformation(
            "Starting agent chat turn. Model={Model}, ClientType={ClientType}",
            this._model, this._clientType);

        if (ctx.Conversation.Messages.Count > 0)
        {
            ctx.Conversation.Append(new ChatMessage(ChatRole.User, userMessage));
        }

        // A new user message clears the active-skill permission window (spec: active-skill state
        // is bounded to the single turn in which the skill was invoked).
        ctx.ActiveSkillState.Clear();

        // Fire OnUserPromptSubmit every ChatAsync call, before entering the agent loop.
        if (this._hooks.OnUserPromptSubmit is { } onUserPromptSubmit)
        {
            await onUserPromptSubmit(ctx, ct);
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
    /// <see cref="SessionStartedEvent"/>; the last is always <see cref="AgentResultEvent"/>.
    /// </summary>
    internal async IAsyncEnumerable<AgentEvent> RunAsync(
        Context ctx,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (ctx.Session.Id is null)
        {
            ctx.Session = ctx.Session with { Id = Guid.NewGuid().ToString("N") };
        }
        string sessionId = ctx.Session.Id;
        yield return new SessionStartedEvent(sessionId);

        if (this._hooks.OnSessionStarted is { } onSessionStarted)
        {
            await onSessionStarted(new SessionStartedHookContext(sessionId, ctx), ct);
        }

        // 1. Seed conversation with the user prompt if the history is empty.
        if (ctx.Conversation.Messages.Count == 0)
        {
            ctx.Conversation.Append(new ChatMessage(ChatRole.User, ctx.Query.Prompt));
        }

        IReadOnlyList<ToolDefinition> toolDefs = ctx.Tools.Registry.ListDefinitions();

        await foreach (AgentEvent evt in this.RunIterationsAsync(ctx, toolDefs, ct))
        {
            yield return evt;
        }
    }

    /// <summary>
    /// Resumes a turn that is parked with <see cref="AgentResultStatus.AwaitingPermission"/>.
    /// Validates responses, executes or denies the pended calls, completes the batch, and
    /// continues the standard loop. Throws eagerly (before the first yield) on invalid input.
    /// </summary>
    /// <exception cref="InvalidOperationException">No turn is parked (<see cref="Context.PendingToolBatch"/> is null).</exception>
    /// <exception cref="ArgumentException">Responses are missing, duplicated, or reference unknown request IDs.</exception>
    // options is part of the public API contract (spec §3.1) and reserved for turn-timeout wiring;
    // suppress the unused-parameter diagnostic rather than removing it from the signature.
#pragma warning disable IDE0060
    internal IAsyncEnumerable<AgentEvent> ResumeAsync(
        Context ctx,
        IReadOnlyList<PermissionResponse> responses,
        AgentOptions? options = null,
        CancellationToken ct = default)
#pragma warning restore IDE0060
    {
        // Eager validation (spec §6.3 steps 1–2): runs before the first MoveNextAsync.
        if (ctx.PendingToolBatch is null)
        {
            throw new InvalidOperationException(
                "No turn is parked. Call SendAsync / RunAsync before ResumeAsync.");
        }

        PendingToolBatch batch = ctx.PendingToolBatch;
        List<PendingToolCall> pending = batch.Pending;

        // Validate: exactly one response per pending RequestId, no unknowns, no duplicates.
        var pendingById = pending.ToDictionary(p => p.RequestId);
        var seenIds = new HashSet<Guid>(responses.Count);
        foreach (PermissionResponse resp in responses)
        {
            if (!pendingById.ContainsKey(resp.RequestId))
            {
                throw new ArgumentException(
                    $"Unknown RequestId {resp.RequestId} in responses.", nameof(responses));
            }

            if (!seenIds.Add(resp.RequestId))
            {
                throw new ArgumentException(
                    $"Duplicate RequestId {resp.RequestId} in responses.", nameof(responses));
            }
        }

        if (seenIds.Count != pending.Count)
        {
            var missing = pending.Where(p => !seenIds.Contains(p.RequestId)).Select(p => p.RequestId);
            throw new ArgumentException(
                $"Missing responses for RequestIds: {string.Join(", ", missing)}.", nameof(responses));
        }

        return this.ResumeIteratorAsync(ctx, batch, responses.ToDictionary(r => r.RequestId), ct);
    }

    private async IAsyncEnumerable<AgentEvent> ResumeIteratorAsync(
        Context ctx,
        PendingToolBatch batch,
        Dictionary<Guid, PermissionResponse> responseById,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Steps 3–6: execute/deny pended calls, merge, OnPostToolBatch, append messages, clear.
        ToolInvokedEvent[] resumedToolEvents =
            await this.ExecutePendingBatchAsync(ctx, batch, responseById, ct);

        // Yield ToolInvokedEvents for the resumed calls.
        foreach (ToolInvokedEvent evt in resumedToolEvents)
        {
            yield return evt;
        }

        // Step 7 (spec §6.3): continue the standard loop (may re-park).
        IReadOnlyList<ToolDefinition> toolDefs = ctx.Tools.Registry.ListDefinitions();
        await foreach (AgentEvent evt in this.RunIterationsAsync(ctx, toolDefs, ct))
        {
            yield return evt;
        }
    }

    /// <summary>
    /// Executes steps 3–6 of the resume algorithm (spec §6.3): record always-grants,
    /// execute or deny pended calls, merge results, fire <c>OnPostToolBatch</c>,
    /// append all result messages, and clear <see cref="Context.PendingToolBatch"/>.
    /// Returns the <see cref="ToolInvokedEvent"/>s for the newly-executed/denied calls only
    /// (completed siblings are already in <paramref name="batch"/>.<see cref="PendingToolBatch.SiblingToolEvents"/>).
    /// Called by both <see cref="ResumeIteratorAsync"/> (yields the events, then continues the loop)
    /// and the abandonment path in <see cref="ChatSession.SendAsync"/> (discards the events, then
    /// proceeds with the new user message).
    /// </summary>
    internal async Task<ToolInvokedEvent[]> ExecutePendingBatchAsync(
        Context ctx,
        PendingToolBatch batch,
        Dictionary<Guid, PermissionResponse> responseById,
        CancellationToken ct = default)
    {
        List<PendingToolCall> pending = batch.Pending;

        // Step 3 (spec §6.3): record AllowAlways / DenyAlways grants.
        if (this._permissions is not null)
        {
            foreach (PendingToolCall pendingCall in pending)
            {
                PermissionResponse resp = responseById[pendingCall.RequestId];
                if (resp.Kind is PermissionResponseKind.AllowAlways or PermissionResponseKind.DenyAlways)
                {
                    await this._permissions.RecordAlwaysAsync(
                        pendingCall.ProposedRule,
                        deny: resp.Kind == PermissionResponseKind.DenyAlways,
                        ct);
                }
            }
        }

        // Step 4 (spec §6.3): execute pended calls in parallel (OnPreToolUse NOT re-run).
        var resumedResults = new FunctionResultContent?[batch.Results.Length];
        var resumedEvents = new ToolInvokedEvent?[batch.Results.Length];

        var resumeTasks = pending.Select(async pendingCall =>
        {
            ct.ThrowIfCancellationRequested();
            PermissionResponse resp = responseById[pendingCall.RequestId];

            ToolInvokedEvent evt;
            FunctionResultContent resultContent;

            if (resp.Kind is PermissionResponseKind.AllowOnce or PermissionResponseKind.AllowAlways)
            {
                // Execute the tool (OnPreToolUse already ran pre-park — do NOT re-run).
                ToolResult result;
                this._logger.LogInformation(
                    "Invoking tool {Tool} (resume). Input={ToolInput}", pendingCall.ToolName, this.ForLog(pendingCall.Input));

                using var toolActivity = _activitySource.StartActivity("agent.tool.invoke", ActivityKind.Internal);
                toolActivity?.SetTag("agent.tool.name", pendingCall.ToolName);
                toolActivity?.SetTag("agent.model", this._model);
                toolActivity?.SetTag("agent.client_type", this._clientType);

                try
                {
                    result = await ctx.Tools.Registry.InvokeAsync(pendingCall.ToolName, pendingCall.Input, ct);
                    toolActivity?.SetStatus(result.IsError ? ActivityStatusCode.Error : ActivityStatusCode.Ok, result.IsError ? result.Content : null);

                    if (result.IsError)
                    {
                        this._logger.LogWarning(
                            "Tool {Tool} returned an error result (resume). Input={ToolInput}, Error={ToolError}",
                            pendingCall.ToolName, this.ForLog(pendingCall.Input), this.ForLog(result.Content));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this._logger.LogError(ex, "Tool {Tool} threw during invocation (resume). Input={ToolInput}", pendingCall.ToolName, this.ForLog(pendingCall.Input));
                    toolActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    result = new ToolResult($"Tool error: {ex.Message}", IsError: true);
                }

                // OnPostToolUse fires for resumed calls (spec §6.3 step 4).
                if (this._hooks.OnPostToolUse is { } onPostToolUse)
                {
                    await onPostToolUse(
                        new PostToolUseHookContext(pendingCall.ToolName, pendingCall.Input, result, ctx), ct);
                }

                _toolCallCounter.Add(1, new TagList
                {
                    { "agent.model", this._model },
                    { "agent.client_type", this._clientType },
                    { "agent.tool.name", pendingCall.ToolName },
                    { "agent.tool.error", result.IsError },
                });

                resultContent = result.IsError
                    ? new FunctionResultContent(pendingCall.CallId, $"[Error] {result.Content}")
                    : new FunctionResultContent(pendingCall.CallId, result.Content);

                evt = new ToolInvokedEvent(pendingCall.ToolName, pendingCall.Input, result);
            }
            else
            {
                // Deny: produce a [Blocked] result (spec §2.5).
                string reason = resp.Message is { Length: > 0 } msg
                    ? $"[Blocked] The user denied permission for this tool call: {msg}"
                    : "[Blocked] The user denied permission for this tool call.";
                var deniedResult = new ToolResult(reason, IsError: true);
                resultContent = new FunctionResultContent(pendingCall.CallId, reason);
                evt = new ToolInvokedEvent(pendingCall.ToolName, pendingCall.Input, deniedResult);
            }

            resumedResults[pendingCall.BatchIndex] = resultContent;
            resumedEvents[pendingCall.BatchIndex] = evt;
            return evt;
        });

        ToolInvokedEvent[] resumedToolEvents = await Task.WhenAll(resumeTasks);

        // Step 5 (spec §6.3): merge into Results by BatchIndex; fire OnPostToolBatch with FULL batch.
        FunctionResultContent?[] fullResults = batch.Results;
        for (int i = 0; i < resumedResults.Length; i++)
        {
            if (resumedResults[i] is not null)
            {
                fullResults[i] = resumedResults[i];
            }
        }

        // Reconstruct the full ToolInvokedEvent[] in batch order for OnPostToolBatch.
        var fullBatchEvents = new ToolInvokedEvent[fullResults.Length];
        for (int i = 0; i < fullResults.Length; i++)
        {
            fullBatchEvents[i] = batch.SiblingToolEvents[i] ?? resumedEvents[i]!;
        }

        if (this._hooks.OnPostToolBatch is { } onPostToolBatch)
        {
            await onPostToolBatch(fullBatchEvents, ctx, ct);
        }

        // Step 6 (spec §6.3): append ALL result messages in batch order; clear PendingToolBatch.
        foreach (FunctionResultContent? resultContent in fullResults)
        {
            ctx.Conversation.Append(new ChatMessage(ChatRole.Tool, [resultContent!]));
        }

        ctx.PendingToolBatch = null;

        return resumedToolEvents;
    }

    /// <summary>
    /// The core agent iteration loop: check stop conditions, call LLM, execute tools.
    /// Shared by <see cref="RunAsync"/> and <see cref="ResumeIteratorAsync"/> so the two
    /// paths cannot drift (spec §6.3 implementation note).
    /// </summary>
    private async IAsyncEnumerable<AgentEvent> RunIterationsAsync(
        Context ctx,
        IReadOnlyList<ToolDefinition> toolDefs,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            ctx.IterationCount++;

            // 1.5. OnPreIteration — fires before system prompt rebuild (Spec §6.5, D.4).
            if (this._hooks.OnPreIteration is { } onPreIteration)
            {
                await onPreIteration(ctx, ct);
            }

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

            // 4. Call the LLM.
            var llmSw = Stopwatch.StartNew();
            var response = await this._llm.GetResponseAsync(ctx.Conversation.Messages, options, ct);
            llmSw.Stop();
            var lastAssistant = response.Messages.LastOrDefault(static m => m.Role == ChatRole.Assistant)
                ?? new ChatMessage(ChatRole.Assistant, []);
            var turnUsage = response.Usage is { } u
                ? new LlmTokenUsage(u.InputTokenCount ?? 0, u.OutputTokenCount ?? 0)
                : new LlmTokenUsage(0, 0);

            ctx.TotalUsage = new(
                ctx.TotalUsage.InputTokens + turnUsage.InputTokens,
                ctx.TotalUsage.OutputTokens + turnUsage.OutputTokens);

            ctx.Conversation.Append(lastAssistant);
            yield return new AssistantTurnEvent(lastAssistant);

            if (this._hooks.OnAssistantTurn is { } onAssistantTurn)
            {
                await onAssistantTurn(new AssistantTurnHookContext(lastAssistant, ctx), ct);
            }

            yield return new IterationCompletedEvent(ctx.IterationCount, turnUsage, llmSw.Elapsed);

            // Detect truncated response — model hit its context/output token limit mid-generation.
            if (response.FinishReason == ChatFinishReason.Length)
            {
                this._logger.LogWarning(
                    "LLM response was truncated (finish_reason=length). Model={Model}, InputTokens={InputTokens}",
                    this._model, turnUsage.InputTokens);
                _errorCounter.Add(1, new TagList
                {
                    { "agent.model", this._model },
                    { "agent.client_type", this._clientType },
                    { "agent.error", "truncated" },
                });
                string windowHint = ctx.Environment.ContextWindowSize is { } windowSize
                    ? $" against a {windowSize:N0}-token context window"
                    : string.Empty;
                string truncationMessage =
                    $"Response truncated: the LLM hit its token limit mid-generation. " +
                    $"This turn consumed {ctx.TotalUsage.InputTokens:N0} input tokens{windowHint} — " +
                    "increase the model's context window or reduce the input size.";
                AgentResultEvent resultEvent = new AgentResultEvent(
                    AgentResultStatus.Error,
                    truncationMessage,
                    ctx.TotalUsage,
                    ctx.TotalCostUsd);
                if (this._hooks.OnStop is { } onStop)
                {
                    await onStop(new StopHookContext(resultEvent, ctx), ct);
                }
                yield return resultEvent;
                yield break;
            }

            // 5. Evaluate stop conditions.
            if (this._stop(ctx, lastAssistant))
            {
                AgentResultStatus status = DetermineStatus(ctx, lastAssistant);
                string? finalText = ExtractFinalText(lastAssistant);
                AgentResultEvent resultEvent = new AgentResultEvent(status, finalText, ctx.TotalUsage, ctx.TotalCostUsd);
                if (this._hooks.OnStop is { } onStop)
                {
                    await onStop(new StopHookContext(resultEvent, ctx), ct);
                }
                yield return resultEvent;
                yield break;
            }

            // 6. Execute tool calls in parallel.
            var toolCalls = lastAssistant.Contents.OfType<FunctionCallContent>().ToList();
            if (toolCalls.Count == 0)
            {
                // Defensive: stop predicate disagreed with reality — treat as success.
                AgentResultEvent resultEvent = new AgentResultEvent(
                    AgentResultStatus.Success, ExtractFinalText(lastAssistant),
                    ctx.TotalUsage, ctx.TotalCostUsd);
                if (this._hooks.OnStop is { } onStop)
                {
                    await onStop(new StopHookContext(resultEvent, ctx), ct);
                }
                yield return resultEvent;
                yield break;
            }

            var resultMessages = new FunctionResultContent?[toolCalls.Count];

            // Pended calls are collected into this pre-sized array by batch index so parallel
            // tasks can write their slot without a lock (each index is written by exactly one task).
            var pendingSlots = new PendingToolCall?[toolCalls.Count];

            var toolTasks = toolCalls.Select(async (call, index) =>
            {
                ct.ThrowIfCancellationRequested();
                var input = ToJsonElement(call.Arguments);
                ToolResult result;

                // PreToolUse hook
                bool hookAsk = false;
                string? hookReason = null;

                if (this._hooks.OnPreToolUse is { } onPreToolUse)
                {
                    PreToolUseDecision decision = await onPreToolUse(
                        new PreToolUseHookContext(call.Name, input, ctx), ct);

                    if (decision is PreToolUseDecision.Deny deny)
                    {
                        ToolResult blocked = new($"[Blocked] {deny.Reason}", IsError: true);
                        resultMessages[index] = new FunctionResultContent(call.CallId, blocked.Content);
                        return new ToolInvokedEvent(call.Name, input, blocked);
                    }

                    if (decision is PreToolUseDecision.Ask ask)
                    {
                        // Flag the call for the permission gate below — do NOT fail-close here.
                        // The gate applies the combined per-call order (spec §2.3):
                        //   deny rule > hook Ask > allow rule.
                        hookAsk = true;
                        hookReason = ask.Reason;
                    }
                    else if (decision is PreToolUseDecision.Rewrite rewrite)
                    {
                        input = rewrite.NewInput;
                    }
                }

                // ── Permission gate (spec §2.3, §2.4) ────────────────────────────
                // Evaluates post-Rewrite input. Combined per-call order:
                //   1. Hook Deny       — handled above (already returned).
                //   2. Rule Deny       — blocks even a hook-Ask-flagged call (deny always wins).
                //   3. Active-skill pre-approval — if the tool is in the active skill's
                //      allowed-tools list AND no deny rule fired, pre-approve and execute.
                //      Hook-Ask is still cleared by this path (the user explicitly loaded a
                //      skill that declared this tool as safe).
                //   4. Hook Ask        — pends (unless cleared by step 3).
                //   5. Rule Allow      — executes.
                //   6. Unresolved      — pends (or denies when OnUnresolved=Deny).
                //
                // Mechanism for hook-Ask + evaluator:
                //   Call Evaluate regardless of hookAsk. A Deny result (rule deny OR
                //   OnUnresolved=Deny) always blocks — the deny reason is used as-is (the
                //   evaluator/stub returns the §2.5 headless string when appropriate). An Allow
                //   or Ask result does NOT clear a hook Ask — the call still pends with
                //   Source=Hook. This keeps the evaluator pure and avoids adding surface to
                //   IPermissionEvaluator.
                if (hookAsk || this._permissions != null || ctx.ActiveSkillState.AllowedTools.Count > 0)
                {
                    if (this._permissions != null)
                    {
                        PermissionDecision permDecision = this._permissions.Evaluate(call.Name, input);

                        if (permDecision is PermissionDecision.Deny permDeny)
                        {
                            // Rule deny beats everything — no park, just block.
                            ToolResult blocked = new($"[Blocked] {permDeny.Reason}", IsError: true);
                            resultMessages[index] = new FunctionResultContent(call.CallId, blocked.Content);
                            return new ToolInvokedEvent(call.Name, input, blocked);
                        }

                        // Active-skill pre-approval: deny rules have been checked above; if the
                        // tool is in the active skill's allowed-tools list, execute it immediately.
                        // This clears a hook-Ask too — the skill declaration is the user's grant.
                        if (ctx.ActiveSkillState.IsAllowed(call.Name))
                        {
                            // Fall through to InvokeAsync.
                        }
                        else if (hookAsk)
                        {
                            // Allow or Ask from evaluator does NOT clear a hook Ask (spec §2.3 step 3).
                            // Pend with Source=Hook; use evaluator's Ask KeyValue/ProposedRule if it
                            // returned Ask, otherwise fall back to null KeyValue + bare tool name.
                            string? keyValue = permDecision is PermissionDecision.Ask askDecision ? askDecision.KeyValue : null;
                            string proposedRule = permDecision is PermissionDecision.Ask askDecision2 ? askDecision2.ProposedRule : call.Name;
                            pendingSlots[index] = new PendingToolCall(
                                Guid.NewGuid(), index, call.CallId, call.Name,
                                input, keyValue, proposedRule, PermissionRequestSource.Hook, hookReason);
                            return new ToolInvokedEvent(call.Name, input, new ToolResult(string.Empty));
                        }
                        else if (permDecision is PermissionDecision.Ask ruleAsk)
                        {
                            // Unresolved rule — pend with Source=UnresolvedRule.
                            pendingSlots[index] = new PendingToolCall(
                                Guid.NewGuid(), index, call.CallId, call.Name,
                                input, ruleAsk.KeyValue, ruleAsk.ProposedRule,
                                PermissionRequestSource.UnresolvedRule, null);
                            return new ToolInvokedEvent(call.Name, input, new ToolResult(string.Empty));
                        }

                        // Allow (or active-skill pre-approved) — fall through to InvokeAsync.
                    }
                    else
                    {
                        // No evaluator: check active-skill pre-approval first, then hook-Ask.
                        if (ctx.ActiveSkillState.IsAllowed(call.Name))
                        {
                            // Active-skill pre-approval — fall through to InvokeAsync.
                        }
                        else if (hookAsk)
                        {
                            // No evaluator: hook Ask still pends (spec §3.5 "Without an evaluator").
                            // KeyValue=null, ProposedRule=bare tool name.
                            pendingSlots[index] = new PendingToolCall(
                                Guid.NewGuid(), index, call.CallId, call.Name,
                                input, null, call.Name, PermissionRequestSource.Hook, hookReason);
                            return new ToolInvokedEvent(call.Name, input, new ToolResult(string.Empty));
                        }
                    }
                }

                this._logger.LogInformation(
                    "Invoking tool {Tool}. Input={ToolInput}", call.Name, this.ForLog(input));

                using var toolActivity = _activitySource.StartActivity("agent.tool.invoke", ActivityKind.Internal);
                toolActivity?.SetTag("agent.tool.name", call.Name);
                toolActivity?.SetTag("agent.model", this._model);
                toolActivity?.SetTag("agent.client_type", this._clientType);

                try
                {
                    result = await ctx.Tools.Registry.InvokeAsync(call.Name, input, ct);
                    toolActivity?.SetStatus(result.IsError ? ActivityStatusCode.Error : ActivityStatusCode.Ok, result.IsError ? result.Content : null);

                    // A tool can fail without throwing: the result simply carries IsError (e.g. an MCP
                    // server returning "An error occurred invoking 'recall'."). The catch below never
                    // sees this, so log it here or the failure is invisible in the logs.
                    if (result.IsError)
                    {
                        this._logger.LogWarning(
                            "Tool {Tool} returned an error result. Input={ToolInput}, Error={ToolError}",
                            call.Name, this.ForLog(input), this.ForLog(result.Content));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this._logger.LogError(ex, "Tool {Tool} threw during invocation. Input={ToolInput}", call.Name, this.ForLog(input));
                    toolActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    toolActivity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
                    {
                        { "exception.type", ex.GetType().FullName },
                        { "exception.message", ex.Message },
                    }));
                    result = new ToolResult($"Tool error: {ex.Message}", IsError: true);
                }

                _toolCallCounter.Add(1, new TagList
                {
                    { "agent.model", this._model },
                    { "agent.client_type", this._clientType },
                    { "agent.tool.name", call.Name },
                    { "agent.tool.error", result.IsError },
                });

                // Active-skill state: when the "skill" meta-tool succeeds, record the invoked
                // skill's allowed-tools so the permission gate pre-approves them for the rest
                // of this turn. On error, leave any prior active-skill state unchanged so a
                // failed re-invocation does not accidentally clear a valid prior grant.
                if (!result.IsError && call.Name == SkillTool.ToolName)
                {
                    string? skillName = null;
                    if (input.TryGetProperty("name", out JsonElement nameEl) &&
                        nameEl.ValueKind == JsonValueKind.String)
                    {
                        skillName = nameEl.GetString();
                    }

                    Skill? invokedSkill = skillName is not null ? ctx.Skills.Find(skillName) : null;
                    ctx.ActiveSkillState.Set(invokedSkill?.AllowedTools ?? []);
                }

                var resultContent = result.IsError
                    ? new FunctionResultContent(call.CallId, $"[Error] {result.Content}")
                    : new FunctionResultContent(call.CallId, result.Content);

                resultMessages[index] = resultContent;
                return new ToolInvokedEvent(call.Name, input, result);
            });

            ToolInvokedEvent[] toolEvents = await Task.WhenAll(toolTasks);

            // ── Post-batch: park if any calls are pended ──────────────────────────
            // Collect the pending calls in batch order (spec §6.1).
            List<PendingToolCall> pendingCalls = [];
            for (int i = 0; i < pendingSlots.Length; i++)
            {
                if (pendingSlots[i] is { } pending)
                {
                    pendingCalls.Add(pending);
                }
            }

            if (pendingCalls.Count > 0)
            {
                // Yield ToolInvokedEvents for completed siblings only (not for pended calls —
                // their placeholder events carry an empty result that should not surface).
                for (int i = 0; i < toolEvents.Length; i++)
                {
                    if (pendingSlots[i] is null)
                    {
                        yield return toolEvents[i];
                    }
                }

                // Yield one PermissionRequestedEvent per pended call, in batch order (spec §6.1).
                foreach (PendingToolCall pending in pendingCalls)
                {
                    yield return new PermissionRequestedEvent(
                        pending.RequestId,
                        pending.ToolName,
                        pending.Input,
                        pending.KeyValue,
                        pending.ProposedRule,
                        pending.Source,
                        pending.Reason);
                }

                // Park: store the batch on the context and emit AwaitingPermission (spec §6.1).
                // Do NOT append result messages and do NOT fire OnPostToolBatch — the batch is
                // incomplete. The conversation remains in the legal intermediate state the LLM
                // protocol uses between tool_use and tool_result.
                // SiblingToolEvents stores the completed siblings' events so ResumeAsync can
                // reconstruct the full batch for OnPostToolBatch (spec §6.3 step 5).
                var siblingEvents = new ToolInvokedEvent?[toolEvents.Length];
                for (int i = 0; i < toolEvents.Length; i++)
                {
                    if (pendingSlots[i] is null)
                    {
                        siblingEvents[i] = toolEvents[i];
                    }
                }

                ctx.PendingToolBatch = new PendingToolBatch
                {
                    Iteration = ctx.IterationCount,
                    Results = resultMessages,
                    Pending = pendingCalls,
                    SiblingToolEvents = siblingEvents,
                };

                yield return new AgentResultEvent(
                    AgentResultStatus.AwaitingPermission,
                    null,
                    ctx.TotalUsage,
                    ctx.TotalCostUsd);
                yield break;
            }

            // No pended calls — unchanged behavior.
            // OnPostToolUse fires per-call now that we know the batch is complete
            // (deferred from the task lambda so it never fires for completed siblings
            // in a parked batch — per spec, OnPostToolUse fires only for fully-settled calls).
            for (int i = 0; i < toolEvents.Length; i++)
            {
                yield return toolEvents[i];
                if (this._hooks.OnPostToolUse is { } onPostToolUse)
                {
                    await onPostToolUse(
                        new PostToolUseHookContext(
                            toolCalls[i].Name, toolEvents[i].Input, toolEvents[i].Result, ctx), ct);
                }
            }

            // OnPostToolBatch fires after all parallel tool calls settle (Spec §6.5, D.4).
            if (this._hooks.OnPostToolBatch is { } onPostToolBatch)
            {
                await onPostToolBatch(toolEvents, ctx, ct);
            }

            // Add one Tool-role message per result so each callId is paired correctly.
            foreach (var resultContent in resultMessages)
            {
                ctx.Conversation.Append(new ChatMessage(ChatRole.Tool, [resultContent!]));
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
