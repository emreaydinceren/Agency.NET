using System.Diagnostics;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.Logging;

namespace Agency.Harness.Loop;

/// <summary>
/// Thin session-level driver that issues turns against a <see cref="ChatSession"/> until a
/// <see cref="GoalState"/>-armed done-condition is met, a hard cap trips, or the run is cancelled.
/// Implements the §8 core algorithm verbatim, plus OpenTelemetry instrumentation (§10).
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="LoopRunner"/> shares the <paramref name="goal"/> <see cref="GoalState"/> instance
/// with the <c>enable_goalkeeper</c> / <c>disable_goalkeeper</c> tools — it reads the goal after
/// each turn so model-driven arm/disarm is visible immediately (§8, E-15).
/// </para>
/// <para>
/// Single in-flight run mirrors <see cref="ChatSession"/>'s not-thread-safe contract.
/// The host must not call <see cref="RunAsync"/> concurrently.
/// </para>
/// </remarks>
internal sealed class LoopRunner(
    ChatSession session,
    IGoalkeeper goalkeeper,
    GoalState goal,
    LoopOptions caps,
    TimeProvider? time = null,
    ILogger<LoopRunner>? logger = null)
{
    /// <summary>
    /// The name of the <see cref="System.Diagnostics.ActivitySource"/> used by the Loop Kit.
    /// Exposed so tests and telemetry registration can reference it without a hard-coded string.
    /// </summary>
    public const string ActivitySourceName = LoopInstrumentation.ActivitySourceName;

    /// <summary>
    /// The name of the <see cref="System.Diagnostics.Metrics.Meter"/> used by the Loop Kit.
    /// Exposed so tests and telemetry registration can reference it without a hard-coded string.
    /// </summary>
    public const string MeterName = LoopInstrumentation.MeterName;

    private readonly ChatSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly IGoalkeeper _goalkeeper = goalkeeper ?? throw new ArgumentNullException(nameof(goalkeeper));
    private readonly GoalState _goal = goal ?? throw new ArgumentNullException(nameof(goal));
    private readonly LoopOptions _caps = caps ?? throw new ArgumentNullException(nameof(caps));
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ILogger<LoopRunner>? _logger = logger;

    /// <summary>
    /// Drives the session toward the armed goal (read from <see cref="GoalState"/>) until Done,
    /// a cap trips, the goal is disarmed, or the run is cancelled.
    /// When no goal is armed, runs exactly one pass-through turn — identical to a plain
    /// <see cref="ChatSession.SendAsync"/> call (T-LOOP-0).
    /// </summary>
    /// <param name="objective">The user's objective for this run. Also the first turn's directive.</param>
    /// <param name="ct">Propagated cancellation token.</param>
    public IAsyncEnumerable<AgentEvent> RunAsync(
        string objective,
        CancellationToken ct = default)
        => RunCoreAsync(objective, ct);

    private async IAsyncEnumerable<AgentEvent> RunCoreAsync(
        string objective,
        [EnumeratorCancellation] CancellationToken ct)
    {
        int turn = 0;
        string directive = objective;
        bool goalEverObserved = false;

        // ── Disarm lifecycle flag (§8, §6.4 rule 5) ──────────────────────────────
        // When set, the runner exited via AwaitingPermission (park) and must NOT clear
        // the goal — the goal must survive so re-entry after ResumeWithPermissionsAsync
        // continues toward the same goal (E-16).
        //
        // Every other exit (terminal outcome or cancellation/exception) clears the goal
        // via the `!parked` guard in the finally block.
        bool parked = false;

        // Wall-clock linked CTS — created lazily when a goal is first observed.
        CancellationTokenSource? wallClockCts = null;
        CancellationToken loopCt = ct;

        // ── Telemetry: root span + run-level stopwatch ────────────────────────
        string outcomeTag = "error"; // set to the correct value on each terminal branch
        long runStartTicks = this._time.GetTimestamp();
        using Activity? runActivity = LoopInstrumentation.ActivitySource.StartActivity(
            "Loop.RunAsync", ActivityKind.Internal);

        try
        {
            while (true)
            {
                loopCt.ThrowIfCancellationRequested();

                yield return new TurnStartedEvent(turn, directive);

                // ── Telemetry: per-turn span + stopwatch ──────────────────────
                long turnStartTicks = this._time.GetTimestamp();
                using Activity? turnActivity = LoopInstrumentation.ActivitySource.StartActivity(
                    "loop.turn", ActivityKind.Internal);
                turnActivity?.SetTag("loop.turn.index", turn);

                // ── one ordinary agent turn (Agent loop untouched) ────────────
                AgentResultEvent? result = null;
                long prevTotalTokens = this._session.TotalUsage.TotalTokens;
                long prevInputTokens = this._session.TotalUsage.InputTokens;
                long prevOutputTokens = this._session.TotalUsage.OutputTokens;

                await foreach (AgentEvent evt in this._session.SendAsync(directive, loopCt))
                {
                    yield return evt;
                    if (evt is AgentResultEvent r)
                    {
                        result = r;
                    }
                }

                // Emit worker token counters (delta from before this turn).
                long workerInputDelta = this._session.TotalUsage.InputTokens - prevInputTokens;
                long workerOutputDelta = this._session.TotalUsage.OutputTokens - prevOutputTokens;
                string workerModel = this._session.WorkerModel;
                string workerClientType = this._session.WorkerClientType;

                EmitWorkerTokens(workerInputDelta, workerOutputDelta, workerModel, workerClientType);
                LoopInstrumentation.TurnsCounter.Add(1);

                // Defensive guard: SendAsync must always emit an AgentResultEvent.
                // Without this, a future bug omitting it would produce an infinite loop.
                if (result is null)
                {
                    outcomeTag = "error";
                    RecordTurnDuration(turnStartTicks);
                    yield return new LoopResultEvent(
                        LoopOutcome.Error,
                        null,
                        this._session.TotalUsage,
                        this._session.TotalCostUsd);
                    yield break;
                }

                // ── park? return without Goalkeeper call ──────────────────────
                // §6.4 / E-16 / gotcha 2: AwaitingPermission does NOT fire OnStop and
                // must NOT trigger the Goalkeeper or clear the goal.
                if (result.Status == AgentResultStatus.AwaitingPermission)
                {
                    parked = true;
                    RecordTurnDuration(turnStartTicks);
                    yield break;
                }

                // ── read goal AFTER the turn ──────────────────────────────────
                // The model may have just armed or disarmed it via tools (§8, E-15).
                GoalSpec? activeGoal = this._goal.Active;

                if (activeGoal is null)
                {
                    // No goal (never armed, or disarmed mid-turn) → single pass-through.
                    outcomeTag = "achieved";
                    RecordTurnDuration(turnStartTicks);
                    yield return new LoopResultEvent(
                        LoopOutcome.Achieved,
                        result.FinalText,
                        this._session.TotalUsage,
                        this._session.TotalCostUsd);
                    yield break;
                }

                // ── first goal observation ────────────────────────────────────
                if (!goalEverObserved)
                {
                    goalEverObserved = true;

                    // E-4: warn when Goalkeeper model/client equals worker model/client (self-preference risk).
                    if (this._logger is not null)
                    {
                        string? goalkeeperModel = this._caps.GoalkeeperModel;
                        string? goalkeeperClient = this._caps.GoalkeeperClientName;
                        bool sameModel = goalkeeperModel is not null &&
                            goalkeeperModel.Equals(workerModel, StringComparison.OrdinalIgnoreCase);
                        bool sameClient = goalkeeperClient is not null &&
                            goalkeeperClient.Equals(workerClientType, StringComparison.OrdinalIgnoreCase);
                        if (sameModel || sameClient)
                        {
                            this._logger.LogWarning(
                                "Goalkeeper model/client matches the worker model/client — self-preference bias risk (E-4). " +
                                "WorkerModel={WorkerModel}, GoalkeeperModel={GoalkeeperModel}, " +
                                "WorkerClient={WorkerClient}, GoalkeeperClient={GoalkeeperClient}",
                                workerModel, goalkeeperModel, workerClientType, goalkeeperClient);
                        }
                    }

                    // Wire wall-clock timeout (goal spec overrides caps default).
                    int? wallClock = activeGoal.WallClockSeconds ?? this._caps.WallClockSeconds;
                    if (wallClock.HasValue)
                    {
                        wallClockCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        wallClockCts.CancelAfter(TimeSpan.FromSeconds(wallClock.Value));
                        loopCt = wallClockCts.Token;
                    }

                    yield return new GoalSetEvent(activeGoal);
                }

                // ── inner turn hard-failed ────────────────────────────────────
                if (result.Status == AgentResultStatus.Error)
                {
                    outcomeTag = "error";
                    RecordTurnDuration(turnStartTicks);
                    yield return new LoopResultEvent(
                        LoopOutcome.Error,
                        result.FinalText,
                        this._session.TotalUsage,
                        this._session.TotalCostUsd);
                    yield break;
                }

                // ── HARD GATE: Goalkeeper (deterministic, unskippable) ────────
                using Activity? goalkeeperActivity = LoopInstrumentation.ActivitySource.StartActivity(
                    "loop.goalkeeper", ActivityKind.Internal);
                goalkeeperActivity?.SetTag("loop.turn.index", turn);

                Verdict verdict = await this._goalkeeper.EvaluateAsync(
                    activeGoal.Condition,
                    this._session.PreviewContext().Conversation.Messages,
                    loopCt);

                // Record verdict as span event (on the goalkeeper span, and propagate to turn span).
                string verdictKind = verdict is Verdict.Done ? "done" : "continue";
                string verdictReason = verdict is Verdict.Done d ? d.Reason :
                    verdict is Verdict.Continue c ? c.Reason : string.Empty;

                goalkeeperActivity?.AddEvent(new ActivityEvent("loop.verdict",
                    tags: new ActivityTagsCollection
                    {
                        { "verdict", verdictKind },
                        { "reason", verdictReason },
                    }));

                // Attach verdict to the inner turn span (§5 / §10 OnStop rationale).
                turnActivity?.AddEvent(new ActivityEvent("loop.verdict",
                    tags: new ActivityTagsCollection
                    {
                        { "verdict", verdictKind },
                        { "reason", verdictReason },
                    }));

                // Emit verdict counter.
                LoopInstrumentation.VerdictsCounter.Add(1, new TagList
                {
                    { "verdict", verdictKind },
                });

                RecordTurnDuration(turnStartTicks);

                yield return new VerdictEvent(turn, verdict);

                if (verdict is Verdict.Done)
                {
                    outcomeTag = "achieved";
                    yield return new LoopResultEvent(
                        LoopOutcome.Achieved,
                        result.FinalText,
                        this._session.TotalUsage,
                        this._session.TotalCostUsd);
                    yield break;
                }

                // ── HARD CAP: bounded by construction (§6.6, §8) ─────────────
                // Turn counter increments here — strictly increases each iteration,
                // proving termination: loop exits when turn >= MaxTurns (T-LOOP-3).
                turn++;

                if (turn >= activeGoal.MaxTurns)
                {
                    outcomeTag = "cap_reached";
                    yield return new LoopResultEvent(
                        LoopOutcome.CapReached,
                        result.FinalText,
                        this._session.TotalUsage,
                        this._session.TotalCostUsd);
                    yield break;
                }

                decimal? budgetCap = activeGoal.Budget ?? this._caps.Budget;
                if (budgetCap.HasValue && this._session.TotalCostUsd >= budgetCap.Value)
                {
                    outcomeTag = "budget_exceeded";
                    yield return new LoopResultEvent(
                        LoopOutcome.BudgetExceeded,
                        result.FinalText,
                        this._session.TotalUsage,
                        this._session.TotalCostUsd);
                    yield break;
                }

                long? tokenCap = activeGoal.TokenBudget ?? this._caps.TokenBudget;
                if (tokenCap.HasValue && this._session.TotalUsage.TotalTokens >= tokenCap.Value)
                {
                    outcomeTag = "budget_exceeded";
                    yield return new LoopResultEvent(
                        LoopOutcome.BudgetExceeded,
                        result.FinalText,
                        this._session.TotalUsage,
                        this._session.TotalCostUsd);
                    yield break;
                }

                // ── Continue: Goalkeeper reason becomes next directive (§8) ────
                // verdict is Verdict.Continue here (Done was handled above).
                directive = ((Verdict.Continue)verdict).Reason;
            }
        }
        finally
        {
            wallClockCts?.Dispose();

            // Emit run-level metrics.
            double runDurationMs = GetElapsedMs(runStartTicks);
            var runTags = new TagList { { "outcome", outcomeTag } };
            LoopInstrumentation.RunsCounter.Add(1, runTags);
            LoopInstrumentation.RunDurationHistogram.Record(runDurationMs, runTags);
            runActivity?.SetTag("loop.outcome", outcomeTag);

            // Auto-clear on every exit EXCEPT a park (§6.4 rule 5):
            //   parked=true  → AwaitingPermission early-return → goal survives for re-entry
            //   parked=false → terminal outcome OR cancellation/exception → goal cleared
            if (!parked)
            {
                this._goal.Clear();
            }
        }
    }

    /// <summary>
    /// Host-side disarm — the <c>/goal clear</c> equivalent.
    /// Delegates to <see cref="GoalState.Clear"/> on the shared <see cref="GoalState"/> instance.
    /// </summary>
    public void ClearGoal() => this._goal.Clear();

    // ── Telemetry helpers ─────────────────────────────────────────────────────

    private static void EmitWorkerTokens(
        long inputDelta,
        long outputDelta,
        string model,
        string clientType)
    {
        if (inputDelta > 0)
        {
            LoopInstrumentation.TokensCounter.Add(inputDelta, new TagList
            {
                { "role", "worker" },
                { "model", model },
                { "client_type", clientType },
                { "token.type", "input" },
            });
        }

        if (outputDelta > 0)
        {
            LoopInstrumentation.TokensCounter.Add(outputDelta, new TagList
            {
                { "role", "worker" },
                { "model", model },
                { "client_type", clientType },
                { "token.type", "output" },
            });
        }
    }

    /// <summary>
    /// Emits goalkeeper token counters. Called by <see cref="Goalkeeper"/> via
    /// <see cref="LoopInstrumentation"/> after each <c>EvaluateAsync</c> call.
    /// </summary>
    internal static void EmitGoalkeeperTokens(
        long inputTokens,
        long outputTokens,
        string model,
        string clientType)
    {
        if (inputTokens > 0)
        {
            LoopInstrumentation.TokensCounter.Add(inputTokens, new TagList
            {
                { "role", "goalkeeper" },
                { "model", model },
                { "client_type", clientType },
                { "token.type", "input" },
            });
        }

        if (outputTokens > 0)
        {
            LoopInstrumentation.TokensCounter.Add(outputTokens, new TagList
            {
                { "role", "goalkeeper" },
                { "model", model },
                { "client_type", clientType },
                { "token.type", "output" },
            });
        }
    }

    private void RecordTurnDuration(long startTicks)
    {
        double durationMs = GetElapsedMs(startTicks);
        LoopInstrumentation.TurnDurationHistogram.Record(durationMs);
    }

    private double GetElapsedMs(long startTicks)
    {
        long elapsed = this._time.GetTimestamp() - startTicks;
        return (double)elapsed / this._time.TimestampFrequency * 1000.0;
    }
}
