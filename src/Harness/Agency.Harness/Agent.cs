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
public sealed partial class Agent
{
    /// <summary>Name of the <see cref="ActivitySource"/> used for agent tracing spans.</summary>
    public const string ActivitySourceName = "Agency.Harness.Agent";

    /// <summary>Name of the <see cref="Meter"/> used for agent metrics.</summary>
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
    internal const string InstructionsMessageMarkerKey = "Agency.Harness.InstructionsBlock";
    internal const string MemoryMessageMarkerKey = "Agency.Harness.MemoryBlock";

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

    /// <summary>Gets the model identifier passed to the constructor and forwarded to the provider on every call.</summary>
    public string Model => this._model;

    /// <summary>Gets the provider display name used in telemetry tags and UI (e.g. "Claude").</summary>
    public string ClientType => this._clientType;

    /// <summary>Gets the clock used for temporal grounding; <see cref="TimeProvider.System"/> unless overridden.</summary>
    internal TimeProvider TimeProvider => this._timeProvider;

    /// <summary>
    /// Fires the <see cref="AgentHooks.OnSessionEnd"/> hook for the given context, if one is set.
    /// Called by <see cref="ChatSession.DisposeAsync"/> at the end of a session.
    /// </summary>
    public Task RaiseSessionEndAsync(Context ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        return this._hooks.OnSessionEnd is { } onSessionEnd
            ? onSessionEnd(new SessionEndedHookContext(ctx.Session.Id ?? string.Empty, ctx), ct)
            : Task.CompletedTask;
    }

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
    /// <param name="instructionsBlock">Optional resolved instruction files to inject as a separate message before the prompt.</param>
    // RS0027 asks that the overload carrying optional parameters be the widest one. The
    // PersonaIdentity spec (§14.2) requires the opposite here: this signature is kept
    // byte-identical so PublicAPI.Unshipped.txt records no *REMOVED* entry — which would be
    // binary-breaking for the published AgencyDotNet.Harness package — and the wider,
    // identity-aware overload is added alongside it instead.
#pragma warning disable RS0027
    public static Context CreateContext(
        string initialPrompt,
        ToolContext? tools = null,
        EnvironmentalContext? environment = null,
        UserSpecificContext? user = null,
        TimeProvider? timeProvider = null,
        SkillContext? skills = null,
        SessionContext? session = null,
        string? instructionsBlock = null) =>
        CreateContext(initialPrompt, tools, environment, user, timeProvider, skills, session, instructionsBlock, identityPrompt: null);
#pragma warning restore RS0027

    /// <summary>
    /// Creates a new <see cref="Context"/> for a multi-turn conversation session,
    /// pre-populated with temporal context, the initial user prompt, and a Persona identity.
    /// </summary>
    /// <param name="initialPrompt">The first user message that seeds the conversation.</param>
    /// <param name="tools">Tool context; pass <see langword="null"/> for <see cref="ToolContext.Empty"/>.</param>
    /// <param name="environment">Environmental context; pass <see langword="null"/> for <see cref="EnvironmentalContext.Empty"/>.</param>
    /// <param name="user">Caller identity; pass <see langword="null"/> for <see cref="UserSpecificContext.Empty"/>.</param>
    /// <param name="timeProvider">Clock for temporal grounding; pass <see langword="null"/> for <see cref="TimeProvider.System"/>.</param>
    /// <param name="skills">Skill context; pass <see langword="null"/> for <see cref="SkillContext.Empty"/>.</param>
    /// <param name="session">Pre-seeded session context; pass <see langword="null"/> for <see cref="SessionContext.Empty"/>.</param>
    /// <param name="instructionsBlock">Resolved instruction files to inject as a separate message before the prompt, or <see langword="null"/> for none.</param>
    /// <param name="identityPrompt">
    /// The Persona-supplied identity that replaces the opening line of the system prompt
    /// (spec §6.9 D-3), or <see langword="null"/> to keep the runtime's default identity line.
    /// </param>
    /// <returns>The newly created <see cref="Context"/>.</returns>
    public static Context CreateContext(
        string initialPrompt,
        ToolContext? tools,
        EnvironmentalContext? environment,
        UserSpecificContext? user,
        TimeProvider? timeProvider,
        SkillContext? skills,
        SessionContext? session,
        string? instructionsBlock,
        string? identityPrompt) =>
        new()
        {
            Query = new QueryContext { Prompt = initialPrompt, InstructionsBlock = instructionsBlock, IdentityPrompt = identityPrompt },
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
    public IAsyncEnumerable<AgentEvent> ChatAsync(
        string userMessage,
        Context ctx,
        AgentOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        return this.ChatIteratorAsync(userMessage, ctx, options, ct);
    }

    /// <summary>Iterator implementation backing <see cref="ChatAsync"/>; drives the turn and yields its events.</summary>
    private async IAsyncEnumerable<AgentEvent> ChatIteratorAsync(
        string userMessage,
        Context ctx,
        AgentOptions? options,
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

        this.LogStartingChatTurn(this._model, this._clientType);

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

        int? timeout = options?.TurnTimeoutSeconds;

        // A separate CTS for the timeout (rather than CancelAfter on the caller-linked CTS) is what
        // makes "the model hung" distinguishable from "the human hit Stop" below: unwinding, we can
        // ask which one actually fired. CancellationTokenSource has no way to report *why* a linked
        // token was cancelled otherwise. The TimeProvider-accepting constructor (added in .NET 8) is
        // required so tests can advance a FakeTimeProvider instead of sleeping in real time.
        using CancellationTokenSource? timeoutCts = timeout is > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(timeout.Value), this._timeProvider)
            : null;
        if (timeoutCts is not null)
        {
            activity?.SetTag("agent.turn.timeout_seconds", timeout!.Value);
        }

        using CancellationTokenSource turnCts = timeoutCts is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(ct);

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
                if (turnError is OperationCanceledException
                    && timeoutCts is not null
                    && timeoutCts.IsCancellationRequested
                    && !ct.IsCancellationRequested)
                {
                    turnError = new TimeoutException(
                        $"Agent turn exceeded the configured timeout of {timeout!.Value} second(s).", turnError);
                }

                RepairIncompleteToolCalls(ctx);

                _errorCounter.Add(1, tags);
                activity?.SetStatus(ActivityStatusCode.Error, turnError.Message);
                this.LogChatTurnFailed(turnError, this._model, this._clientType);
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

                this.LogChatTurnCompleted(this._model, deltaIn, deltaOut, sw.Elapsed.TotalMilliseconds);
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

        // 1. Seed conversation with project instructions (if any) and the user prompt.
        if (ctx.Conversation.Messages.Count == 0)
        {
            if (!string.IsNullOrEmpty(ctx.Query.InstructionsBlock))
            {
                ctx.Conversation.Append(new ChatMessage(ChatRole.User, ctx.Query.InstructionsBlock)
                {
                    AdditionalProperties = new() { [InstructionsMessageMarkerKey] = true }
                });
            }

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
                if (this._logger.IsEnabled(LogLevel.Information))
                {
                    // CA1873 false positive: ForLog() is only reached once IsEnabled has already
                    // gated it above, so this is not an unconditional expensive evaluation.
#pragma warning disable CA1873
                    this.LogInvokingToolResume(pendingCall.ToolName, this.ForLog(pendingCall.Input));
#pragma warning restore CA1873
                }

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
                        this.LogToolErrorResultResume(pendingCall.ToolName, this.ForLog(pendingCall.Input), this.ForLog(result.Content));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this.LogToolThrewResume(ex, pendingCall.ToolName, this.ForLog(pendingCall.Input));
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

                evt = new ToolInvokedEvent(pendingCall.ToolName, pendingCall.Input, result) { CallId = pendingCall.CallId };
            }
            else
            {
                // Deny: produce a [Blocked] result (spec §2.5). When a key value (e.g. a file
                // path) is available, restate it and note the denial's scope explicitly — models
                // otherwise tend to over-generalize a single denial into a blanket refusal for
                // unrelated inputs to the same tool.
                string pathSuffix = pendingCall.KeyValue is { Length: > 0 } keyValue ? $" for '{keyValue}'" : string.Empty;
                string scopeNote = pendingCall.KeyValue is { Length: > 0 } keyValue2
                    ? $" This denial applies only to '{keyValue2}' — other files or inputs are not affected."
                    : " This denial applies only to this specific call — other inputs are not affected.";
                string reason = resp.Message is { Length: > 0 } msg
                    ? $"[Blocked] The user denied permission for this tool call{pathSuffix}: {msg}.{scopeNote}"
                    : $"[Blocked] The user denied permission for this tool call{pathSuffix}.{scopeNote}";
                var deniedResult = new ToolResult(reason, IsError: true);
                resultContent = new FunctionResultContent(pendingCall.CallId, reason);
                evt = new ToolInvokedEvent(pendingCall.ToolName, pendingCall.Input, deniedResult) { CallId = pendingCall.CallId };
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
    /// Returns the message list for one request: the conversation, plus the <c>&lt;memory&gt;</c>
    /// block when anything was recalled.
    /// </summary>
    /// <param name="ctx">The current session context.</param>
    /// <returns>
    /// The conversation messages unchanged when there is no memory block, otherwise a new list
    /// with the block inserted.
    /// </returns>
    /// <remarks>
    /// The block is composed per request and never appended to <see cref="Context.Conversation"/>:
    /// it is the result of a vector search over the current message, so it differs every turn and
    /// persisting it would leave a trail of stale recall in the transcript.
    /// </remarks>
    private static IReadOnlyList<ChatMessage> ComposeRequestMessages(Context ctx)
    {
        string memoryBlock = Memory.MemoryRenderer.Build(ctx);
        if (memoryBlock.Length == 0)
        {
            return ctx.Conversation.Messages;
        }

        var composed = new List<ChatMessage>(ctx.Conversation.Messages);
        var memoryMessage = new ChatMessage(ChatRole.User, memoryBlock)
        {
            AdditionalProperties = new() { [MemoryMessageMarkerKey] = true },
        };

        // Sit immediately before the current question, where the project-instructions block also
        // lands. Appending after it would be more recent still, but mid-tool-loop the tail is an
        // assistant tool call and its results, and a user message wedged in there reads as the
        // user interrupting.
        int lastUserIndex = composed.FindLastIndex(static m => m.Role == ChatRole.User);
        composed.Insert(lastUserIndex < 0 ? composed.Count : lastUserIndex, memoryMessage);
        return composed;
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

            // 2.5. Compose the messages for this request. Recall is injected here rather than into
            // ctx.Conversation because it is rebuilt from a vector search over the current message
            // every iteration - appending it to the transcript would accumulate stale blocks.
            IReadOnlyList<ChatMessage> requestMessages = ComposeRequestMessages(ctx);

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

            // 3.5. Record the assembled request so hosts can show exactly what was submitted -
            // including anything OnPreIteration hooks (e.g. memory retrieval) just wrote into the
            // system prompt, which no pre-flight projection of the next turn could see. Captured
            // outside the retry loops below so one logical request yields one capture.
            ctx.LastLlmRequest = LlmRequestSnapshotCodec.Serialize(new LlmRequestSnapshot
            {
                CapturedAt = this._timeProvider.GetUtcNow(),
                Iteration = ctx.IterationCount,
                ModelId = this._model,
                ClientType = this._clientType,
                MaxOutputTokens = options.MaxOutputTokens,
                SystemPrompt = systemPrompt,
                Messages = requestMessages,
                Tools = toolDefs,
            });

            // 4. Call the LLM, retrying on a malformed or degenerate response - known flakiness
            // patterns for some local OpenAI-compatible backends (e.g. LM Studio).
            var llmSw = Stopwatch.StartNew();
            ChatResponse response;
            ChatMessage lastAssistant;
            const int maxEmptyChoicesAttempts = 3;
            const int maxDegenerateAttempts = 3;
            int emptyChoicesAttempt = 0;
            int degenerateAttempt = 0;
            LlmTokenUsage iterationUsage = new(0, 0);
            while (true)
            {
                // Streaming call. Finding (D2 spec): `yield return` cannot appear inside a
                // try/catch with a catch clause (CS1626), so the empty-choices retry below cannot
                // wrap the whole streaming call the way the old GetResponseAsync retry did. Instead,
                // only `MoveNextAsync()` is wrapped in try/catch; delta events are yielded outside
                // it, matching the manual-enumerator shape already used in ChatIteratorAsync.
                //
                // Retry semantics: once at least one delta has been yielded to the caller for this
                // attempt, a subsequent stream fault can no longer be retried cleanly - the caller
                // has already seen partial output, and reissuing the call would duplicate those
                // deltas. So a fault is only retried when it occurs before any delta was emitted;
                // otherwise it is surfaced immediately as the same exhausted-retries error, rather
                // than silently duplicating output.
                while (true)
                {
                    emptyChoicesAttempt++;
                    var streamedUpdates = new List<ChatResponseUpdate>();
                    bool anyDeltaYielded = false;
                    Exception? streamError = null;

                    IAsyncEnumerator<ChatResponseUpdate> enumerator =
                        this._llm.GetStreamingResponseAsync(requestMessages, options, ct).GetAsyncEnumerator(ct);
                    try
                    {
                        while (true)
                        {
                            bool moved;
                            try
                            {
                                moved = await enumerator.MoveNextAsync();
                            }
                            catch (ArgumentOutOfRangeException ex) when (ex.ParamName == "index")
                            {
                                streamError = ex;
                                break;
                            }

                            if (!moved)
                            {
                                break;
                            }

                            ChatResponseUpdate update = enumerator.Current;
                            streamedUpdates.Add(update);

                            foreach (AIContent content in update.Contents)
                            {
                                switch (content)
                                {
                                    case TextContent { Text.Length: > 0 } textContent:
                                        anyDeltaYielded = true;
                                        yield return new AssistantTextDeltaEvent(textContent.Text);
                                        break;
                                    case TextReasoningContent { Text.Length: > 0 } reasoningContent:
                                        anyDeltaYielded = true;
                                        yield return new AssistantThoughtDeltaEvent(reasoningContent.Text);
                                        break;
                                }
                            }
                        }
                    }
                    finally
                    {
                        await enumerator.DisposeAsync();
                    }

                    if (streamError is null)
                    {
                        response = await AsAsyncEnumerable(streamedUpdates).ToChatResponseAsync(ct);
                        break;
                    }

                    // Known upstream issue: some OpenAI-compatible backends (e.g. LM Studio) occasionally
                    // return a 200 response with an empty `choices` array - typically when grammar-constrained
                    // tool-call generation fails, or the backend is mid-swap between models under VRAM
                    // pressure. The OpenAI SDK's ChatCompletion.Role getter indexes into that empty array and
                    // throws instead of the backend surfacing a proper error. Back off briefly before retrying
                    // so a transient backend hiccup has time to clear instead of hitting it again instantly.
                    if (anyDeltaYielded || emptyChoicesAttempt >= maxEmptyChoicesAttempts)
                    {
                        this.LogEmptyChoicesExhausted(this._model, this._clientType, maxEmptyChoicesAttempts);
                        throw new InvalidOperationException(
                            $"The LLM backend for model '{this._model}' ({this._clientType}) returned " +
                            $"{maxEmptyChoicesAttempts} consecutive malformed responses with no completion choices, " +
                            "instead of a normal reply or an error. This is a known compatibility issue with some " +
                            "OpenAI-compatible local servers (e.g. LM Studio) - the backend is reachable and " +
                            "returning HTTP 200, but failing to actually generate a response for this request " +
                            "(often for tool-calling requests). Check that the backend server is running and " +
                            "responsive, and consider restarting it; if the problem persists, it may not be fixable " +
                            "from this client.",
                            streamError);
                    }

                    this.LogEmptyChoicesRetry(this._model, this._clientType, emptyChoicesAttempt, maxEmptyChoicesAttempts);
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * emptyChoicesAttempt), this._timeProvider, ct);
                }

                lastAssistant = response.Messages.LastOrDefault(static m => m.Role == ChatRole.Assistant)
                    ?? new ChatMessage(ChatRole.Assistant, []);
                LlmTokenUsage attemptUsage = response.Usage is { } u
                    ? new LlmTokenUsage(u.InputTokenCount ?? 0, u.OutputTokenCount ?? 0)
                    : new LlmTokenUsage(0, 0);
                iterationUsage = new(
                    iterationUsage.InputTokens + attemptUsage.InputTokens,
                    iterationUsage.OutputTokens + attemptUsage.OutputTokens);

                // A response with neither a tool call nor any text content wastes the turn - the
                // model spent its output budget on something (e.g. reasoning-only content) that
                // never surfaces as an answer. Retry before giving up, same rationale as above.
                bool isDegenerate = response.FinishReason != ChatFinishReason.Length
                    && !lastAssistant.Contents.OfType<FunctionCallContent>().Any()
                    && ExtractFinalText(lastAssistant) is null;
                if (!isDegenerate)
                {
                    break;
                }

                string contentTypes = string.Join(
                    ",", lastAssistant.Contents.Select(static c => c.GetType().Name));
                degenerateAttempt++;
                if (degenerateAttempt >= maxDegenerateAttempts)
                {
                    this.LogDegenerateResponseExhausted(
                        this._model, this._clientType, maxDegenerateAttempts, contentTypes);
                    break;
                }

                this.LogDegenerateResponseRetry(
                    this._model, this._clientType, degenerateAttempt, maxDegenerateAttempts, contentTypes);
            }
            llmSw.Stop();
            var turnUsage = iterationUsage;

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
                this.LogResponseTruncated(this._model, turnUsage.InputTokens);
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
                    AgentResultStatus.Truncated,
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
                string? finalText = ExtractFinalText(lastAssistant);
                AgentResultStatus status = DetermineStatus(ctx, lastAssistant, finalText);
                AgentResultEvent resultEvent = new AgentResultEvent(
                    status, finalText ?? NoUsableOutputMessage, ctx.TotalUsage, ctx.TotalCostUsd);
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
                // Defensive: stop predicate disagreed with reality — treat as success, unless the
                // model also produced no usable text (see DetermineStatus).
                string? finalText = ExtractFinalText(lastAssistant);
                AgentResultEvent resultEvent = new AgentResultEvent(
                    DetermineStatus(ctx, lastAssistant, finalText), finalText ?? NoUsableOutputMessage,
                    ctx.TotalUsage, ctx.TotalCostUsd);
                if (this._hooks.OnStop is { } onStop)
                {
                    await onStop(new StopHookContext(resultEvent, ctx), ct);
                }
                yield return resultEvent;
                yield break;
            }

            // Announce each call, correlated by the provider's own CallId, before dispatch begins -
            // this is a plain yield (no enclosing try/catch), so Finding 1's CS1626 restriction
            // does not apply here.
            foreach (FunctionCallContent startingCall in toolCalls)
            {
                yield return new ToolStartedEvent(
                    startingCall.CallId, startingCall.Name, ToJsonElement(startingCall.Arguments));
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
                        return new ToolInvokedEvent(call.Name, input, blocked) { CallId = call.CallId };
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
                            return new ToolInvokedEvent(call.Name, input, blocked) { CallId = call.CallId };
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
                            return new ToolInvokedEvent(call.Name, input, new ToolResult(string.Empty)) { CallId = call.CallId };
                        }
                        else if (permDecision is PermissionDecision.Ask ruleAsk)
                        {
                            // Unresolved rule — pend with Source=UnresolvedRule.
                            pendingSlots[index] = new PendingToolCall(
                                Guid.NewGuid(), index, call.CallId, call.Name,
                                input, ruleAsk.KeyValue, ruleAsk.ProposedRule,
                                PermissionRequestSource.UnresolvedRule, null);
                            return new ToolInvokedEvent(call.Name, input, new ToolResult(string.Empty)) { CallId = call.CallId };
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
                            return new ToolInvokedEvent(call.Name, input, new ToolResult(string.Empty)) { CallId = call.CallId };
                        }
                    }
                }

                if (this._logger.IsEnabled(LogLevel.Information))
                {
                    // CA1873 false positive: ForLog() is only reached once IsEnabled has already
                    // gated it above, so this is not an unconditional expensive evaluation.
#pragma warning disable CA1873
                    this.LogInvokingTool(call.Name, this.ForLog(input));
#pragma warning restore CA1873
                }

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
                        this.LogToolErrorResult(call.Name, this.ForLog(input), this.ForLog(result.Content));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this.LogToolThrew(ex, call.Name, this.ForLog(input));
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
                return new ToolInvokedEvent(call.Name, input, result) { CallId = call.CallId };
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

    /// <summary>Reported as <see cref="AgentResultEvent.FinalText"/> when the LLM's response has
    /// neither a tool call nor any text content — a known flakiness pattern for some backends,
    /// where tokens are spent on non-text content (e.g. reasoning) but no answer is produced.</summary>
    internal const string NoUsableOutputMessage =
        "The LLM finished the turn without requesting a tool call or producing any text output.";

    internal static AgentResultStatus DetermineStatus(Context _, ChatMessage last, string? finalText)
    {
        if (last.Contents.OfType<FunctionCallContent>().Any())
        {
            return AgentResultStatus.MaxStepsReached;
        }

        return finalText is null ? AgentResultStatus.Error : AgentResultStatus.Success;
    }

    internal static string? ExtractFinalText(ChatMessage msg)
    {
        string? text = string.Concat(msg.Contents.OfType<TextContent>().Select(static t => t.Text));
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>Content of the synthetic tool result appended for a <see cref="FunctionCallContent"/>
    /// left dangling by a cancelled or failed turn (see <see cref="RepairIncompleteToolCalls"/>).</summary>
    internal const string CancelledToolResultMessage =
        "[Cancelled] The user cancelled this turn before the tool returned.";

    /// <summary>
    /// Appends a synthetic <see cref="FunctionResultContent"/> for every <see cref="FunctionCallContent"/>
    /// in the conversation that lacks a matching result — the state a cancelled or failed turn can leave
    /// behind when it unwinds between the assistant message being appended and the tool results being
    /// appended. Left unrepaired, the dangling call makes the transcript invalid for the next request.
    /// </summary>
    private static void RepairIncompleteToolCalls(Context ctx)
    {
        IReadOnlyList<ChatMessage> messages = ctx.Conversation.Messages;

        var resultCallIds = new HashSet<string>(
            messages
                .Where(static m => m.Role == ChatRole.Tool)
                .SelectMany(static m => m.Contents.OfType<FunctionResultContent>())
                .Select(static r => r.CallId));

        List<string> missingCallIds = messages
            .Where(static m => m.Role == ChatRole.Assistant)
            .SelectMany(static m => m.Contents.OfType<FunctionCallContent>())
            .Select(static c => c.CallId)
            .Where(id => !resultCallIds.Contains(id))
            .Distinct()
            .ToList();

        foreach (string callId in missingCallIds)
        {
            ctx.Conversation.Append(new ChatMessage(
                ChatRole.Tool, [new FunctionResultContent(callId, CancelledToolResultMessage)]));
        }
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

    /// <summary>
    /// Wraps an already-materialized list of updates as an <see cref="IAsyncEnumerable{T}"/> so it
    /// can be fed to <c>ChatResponseExtensions.ToChatResponseAsync</c>, which only accepts that
    /// shape. The list was collected by manually enumerating the original stream (see the
    /// streaming retry loop above), so no further async work happens here.
    /// </summary>
    private static async IAsyncEnumerable<ChatResponseUpdate> AsAsyncEnumerable(List<ChatResponseUpdate> updates)
    {
        foreach (ChatResponseUpdate update in updates)
        {
            yield return update;
        }
    }

    /// <summary>Logs that an agent chat turn is starting.</summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "Starting agent chat turn. Model={Model}, ClientType={ClientType}")]
    private partial void LogStartingChatTurn(string model, string clientType);

    /// <summary>Logs that an agent chat turn failed.</summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "Agent chat turn failed. Model={Model}, ClientType={ClientType}")]
    private partial void LogChatTurnFailed(Exception ex, string model, string clientType);

    /// <summary>Logs that the LLM returned an empty <c>choices</c> array and the call is being retried.</summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "LLM response had no choices (likely a malformed backend response). Retrying (attempt {Attempt}/{MaxAttempts}). Model={Model}, ClientType={ClientType}")]
    private partial void LogEmptyChoicesRetry(string model, string clientType, int attempt, int maxAttempts);

    /// <summary>Logs that every empty-<c>choices</c> retry attempt was exhausted and the turn is failing.</summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "LLM returned no completion choices on all {MaxAttempts} attempts - giving up. Model={Model}, ClientType={ClientType}")]
    private partial void LogEmptyChoicesExhausted(string model, string clientType, int maxAttempts);

    /// <summary>Logs that the LLM returned neither a tool call nor any text content and the call is being retried.</summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "LLM response had no tool call and no text content (contents: {ContentTypes}). Retrying (attempt {Attempt}/{MaxAttempts}). Model={Model}, ClientType={ClientType}")]
    private partial void LogDegenerateResponseRetry(string model, string clientType, int attempt, int maxAttempts, string contentTypes);

    /// <summary>Logs that every degenerate-response retry attempt was exhausted and the turn is failing.</summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "LLM returned neither a tool call nor any text content on all {MaxAttempts} attempts (contents: {ContentTypes}) - giving up. Model={Model}, ClientType={ClientType}")]
    private partial void LogDegenerateResponseExhausted(string model, string clientType, int maxAttempts, string contentTypes);

    /// <summary>Logs that an agent chat turn completed.</summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "Agent chat turn completed. Model={Model}, InputTokens={InputTokens}, OutputTokens={OutputTokens}, DurationMs={DurationMs}")]
    private partial void LogChatTurnCompleted(string model, long inputTokens, long outputTokens, double durationMs);

    /// <summary>Logs that a resumed tool call is being invoked.</summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "Invoking tool {Tool} (resume). Input={ToolInput}")]
    private partial void LogInvokingToolResume(string tool, string toolInput);

    /// <summary>Logs that a resumed tool call returned an error result.</summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool {Tool} returned an error result (resume). Input={ToolInput}, Error={ToolError}")]
    private partial void LogToolErrorResultResume(string tool, string toolInput, string toolError);

    /// <summary>Logs that a resumed tool call threw during invocation.</summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "Tool {Tool} threw during invocation (resume). Input={ToolInput}")]
    private partial void LogToolThrewResume(Exception ex, string tool, string toolInput);

    /// <summary>Logs that the LLM response was truncated because it hit its token limit.</summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "LLM response was truncated (finish_reason=length). Model={Model}, InputTokens={InputTokens}")]
    private partial void LogResponseTruncated(string model, long inputTokens);

    /// <summary>Logs that a tool call is being invoked.</summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "Invoking tool {Tool}. Input={ToolInput}")]
    private partial void LogInvokingTool(string tool, string toolInput);

    /// <summary>Logs that a tool call returned an error result.</summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool {Tool} returned an error result. Input={ToolInput}, Error={ToolError}")]
    private partial void LogToolErrorResult(string tool, string toolInput, string toolError);

    /// <summary>Logs that a tool call threw during invocation.</summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "Tool {Tool} threw during invocation. Input={ToolInput}")]
    private partial void LogToolThrew(Exception ex, string tool, string toolInput);
}
