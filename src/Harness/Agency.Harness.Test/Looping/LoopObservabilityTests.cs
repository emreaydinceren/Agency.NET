using System.Diagnostics.Metrics;

using Agency.Harness.Looping;
using Agency.Harness.Test.Fakes;

namespace Agency.Harness.Test.Looping;

/// <summary>
/// T-OBS-1: verifies that <see cref="LoopRunner.RunAsync"/> emits the expected OpenTelemetry
/// metrics (§10 of the Loop Kit spec) using an in-test <see cref="MeterListener"/>.
/// No live LLM is used — all calls are driven by <see cref="FakeChatClient"/> and fake goalkeepers.
/// </summary>
public sealed class LoopObservabilityTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Builds a worker <see cref="ChatResponse"/> with a single text reply and token usage.</summary>
    private static ChatResponse WorkerResponse(
        string text = "Done.",
        int inputTokens = 10,
        int outputTokens = 5) =>
        new([new ChatMessage(ChatRole.Assistant, text)])
        {
            Usage = new UsageDetails
            {
                InputTokenCount = inputTokens,
                OutputTokenCount = outputTokens,
            },
            FinishReason = ChatFinishReason.Stop,
        };

    /// <summary>
    /// Builds a Goalkeeper <see cref="ChatResponse"/> returning a parseable Done verdict.
    /// </summary>
    private static ChatResponse GoalkeeperDoneResponse(
        int inputTokens = 5,
        int outputTokens = 3) =>
        new([new ChatMessage(ChatRole.Assistant, "VERDICT: done\nREASON: condition satisfied")])
        {
            Usage = new UsageDetails
            {
                InputTokenCount = inputTokens,
                OutputTokenCount = outputTokens,
            },
            FinishReason = ChatFinishReason.Stop,
        };

    /// <summary>
    /// Builds a Goalkeeper <see cref="ChatResponse"/> returning a parseable Continue verdict.
    /// </summary>
    private static ChatResponse GoalkeeperContinueResponse(
        int inputTokens = 5,
        int outputTokens = 3) =>
        new([new ChatMessage(ChatRole.Assistant, "VERDICT: continue\nREASON: not yet done")])
        {
            Usage = new UsageDetails
            {
                InputTokenCount = inputTokens,
                OutputTokenCount = outputTokens,
            },
            FinishReason = ChatFinishReason.Stop,
        };

    /// <summary>Collects all events from <see cref="LoopRunner.RunAsync"/> into a list.</summary>
    private static async Task<List<AgentEvent>> RunToCompletion(
        LoopRunner runner,
        string objective = "do something",
        CancellationToken ct = default)
    {
        var events = new List<AgentEvent>();
        await foreach (AgentEvent evt in runner.RunAsync(objective, ct))
        {
            events.Add(evt);
        }

        return events;
    }

    // ── T-OBS-1: core metrics emitted on a happy-path run ────────────────────

    /// <summary>
    /// T-OBS-1 (§10 / §15 Phase 5): a run that arms a goal, produces one worker turn, and
    /// reaches <see cref="LoopOutcome.Achieved"/> on the first Goalkeeper evaluation must emit:
    /// <list type="bullet">
    /// <item><c>loop.runs</c> tagged <c>outcome=achieved</c></item>
    /// <item><c>loop.turns</c></item>
    /// <item><c>loop.verdicts</c> tagged <c>verdict=done</c></item>
    /// <item><c>loop.tokens</c> tagged <c>role=worker</c> for the worker turn's tokens</item>
    /// <item><c>loop.tokens</c> tagged <c>role=goalkeeper</c> for the goalkeeper's tokens</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task HappyPath_EmitsAllExpectedMetrics()
    {
        // ── arrange metric capture ────────────────────────────────────────────
        var measurements = new Dictionary<string, List<(long Value, KeyValuePair<string, object?>[] Tags)>>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == LoopRunner.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (!measurements.TryGetValue(instrument.Name, out List<(long, KeyValuePair<string, object?>[])>? list))
            {
                list = [];
                measurements[instrument.Name] = list;
            }

            list.Add((measurement, tags.ToArray()));
        });
        listener.SetMeasurementEventCallback<double>((instrument, _, _, _) =>
        {
            // Histograms (double) — just ensure they fire; value checked via long callbacks above
            // for counters. Histograms don't need detailed tag assertions here.
        });
        listener.Start();

        // ── arrange fake worker + goalkeeper clients ───────────────────────────
        const string workerModel = "test-worker-model";
        const string goalkeeperModel = "test-goalkeeper-model";

        var workerFake = new FakeChatClient();
        workerFake.EnqueueResponse(WorkerResponse(inputTokens: 10, outputTokens: 5));

        var goalkeeperFake = new FakeChatClient();
        goalkeeperFake.EnqueueResponse(GoalkeeperDoneResponse(inputTokens: 5, outputTokens: 3));

        var workerAgent = new Agent(workerFake, workerModel, clientType: "FakeProvider");
        var session = new ChatSession(workerAgent, new AgentOptions());

        var goalkeeper = new Goalkeeper(goalkeeperFake, goalkeeperModel);

        var goalState = new GoalState();
        goalState.Arm(new GoalSpec { Condition = "test condition", MaxTurns = 5 });

        var runner = new LoopRunner(
            session,
            goalkeeper,
            goalState,
            new LoopOptions
            {
                GoalkeeperClientName = "fake-goalkeeper",
                GoalkeeperModel = goalkeeperModel,
            });

        // ── act ───────────────────────────────────────────────────────────────
        List<AgentEvent> events = await RunToCompletion(
            runner, "test objective", TestContext.Current.CancellationToken);

        // Flush any buffered measurements.
        listener.RecordObservableInstruments();

        // ── assert outcome ────────────────────────────────────────────────────
        LoopResultEvent loopResult = Assert.Single(events.OfType<LoopResultEvent>());
        Assert.Equal(LoopOutcome.Achieved, loopResult.Outcome);

        // ── assert loop.runs ─────────────────────────────────────────────────
        Assert.True(
            measurements.ContainsKey("loop.runs"),
            "Expected 'loop.runs' counter to be emitted");

        var runsEntries = measurements["loop.runs"];
        Assert.True(runsEntries.Count >= 1, "Expected at least one 'loop.runs' measurement");

        bool hasAchievedOutcome = runsEntries.Any(
            e => e.Tags.Any(t => t.Key == "outcome" && "achieved".Equals(t.Value?.ToString(), StringComparison.OrdinalIgnoreCase)));
        Assert.True(hasAchievedOutcome, "Expected loop.runs to carry outcome=achieved tag");

        // ── assert loop.turns ─────────────────────────────────────────────────
        Assert.True(
            measurements.ContainsKey("loop.turns"),
            "Expected 'loop.turns' counter to be emitted");
        Assert.True(measurements["loop.turns"].Count >= 1, "Expected at least one 'loop.turns' measurement");

        // ── assert loop.verdicts ──────────────────────────────────────────────
        Assert.True(
            measurements.ContainsKey("loop.verdicts"),
            "Expected 'loop.verdicts' counter to be emitted");

        var verdictsEntries = measurements["loop.verdicts"];
        bool hasDoneVerdict = verdictsEntries.Any(
            e => e.Tags.Any(t => t.Key == "verdict" && "done".Equals(t.Value?.ToString(), StringComparison.OrdinalIgnoreCase)));
        Assert.True(hasDoneVerdict, "Expected loop.verdicts to carry verdict=done tag");

        // ── assert loop.tokens — worker ───────────────────────────────────────
        Assert.True(
            measurements.ContainsKey("loop.tokens"),
            "Expected 'loop.tokens' counter to be emitted");

        var tokenEntries = measurements["loop.tokens"];

        bool hasWorkerTokens = tokenEntries.Any(
            e => e.Tags.Any(t => t.Key == "role" && "worker".Equals(t.Value?.ToString(), StringComparison.OrdinalIgnoreCase)));
        Assert.True(hasWorkerTokens, "Expected loop.tokens to carry role=worker tag");

        // ── assert loop.tokens — goalkeeper ───────────────────────────────────
        bool hasGoalkeeperTokens = tokenEntries.Any(
            e => e.Tags.Any(t => t.Key == "role" && "goalkeeper".Equals(t.Value?.ToString(), StringComparison.OrdinalIgnoreCase)));
        Assert.True(hasGoalkeeperTokens, "Expected loop.tokens to carry role=goalkeeper tag");

        // ── assert loop.tokens — model tag is present ─────────────────────────
        bool hasModelTag = tokenEntries.Any(e => e.Tags.Any(t => t.Key == "model"));
        Assert.True(hasModelTag, "Expected loop.tokens to carry a model tag");

        // ── assert loop.tokens — client_type tag is present ───────────────────
        bool hasClientTypeTag = tokenEntries.Any(e => e.Tags.Any(t => t.Key == "client_type"));
        Assert.True(hasClientTypeTag, "Expected loop.tokens to carry a client_type tag");

        // ── assert loop.tokens — token.type tag is present ────────────────────
        bool hasTokenTypeTag = tokenEntries.Any(e => e.Tags.Any(t => t.Key == "token.type"));
        Assert.True(hasTokenTypeTag, "Expected loop.tokens to carry a token.type tag");
    }

    // ── T-OBS-2: multiple turns emit the correct counts ──────────────────────

    /// <summary>
    /// T-OBS-2: a run with two worker turns (Continue then Done) must emit
    /// <c>loop.verdicts</c> for both <c>continue</c> and <c>done</c>, and
    /// <c>loop.turns</c> must be incremented twice.
    /// </summary>
    [Fact]
    public async Task TwoTurns_EmitsContinueAndDoneVerdicts_TurnCountTwo()
    {
        // ── arrange metric capture ────────────────────────────────────────────
        var verdictsEmitted = new List<string>();
        long turnsEmitted = 0;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == LoopRunner.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name == "loop.turns")
            {
                Interlocked.Add(ref turnsEmitted, measurement);
            }
            else if (instrument.Name == "loop.verdicts")
            {
                string? verdict = tags
                    .ToArray()
                    .FirstOrDefault(t => t.Key == "verdict")
                    .Value?.ToString();
                if (verdict is not null)
                {
                    verdictsEmitted.Add(verdict);
                }
            }
        });
        listener.Start();

        // ── arrange two worker turns, Continue then Done ──────────────────────
        var workerFake = new FakeChatClient();
        workerFake.EnqueueResponse(WorkerResponse("turn 0 result"));
        workerFake.EnqueueResponse(WorkerResponse("turn 1 result"));

        var goalkeeperFake = new FakeChatClient();
        goalkeeperFake.EnqueueResponse(GoalkeeperContinueResponse());
        goalkeeperFake.EnqueueResponse(GoalkeeperDoneResponse());

        var workerAgent = new Agent(workerFake, "worker-model", clientType: "FakeProvider");
        var session = new ChatSession(workerAgent, new AgentOptions());
        var goalkeeper = new Goalkeeper(goalkeeperFake, "goalkeeper-model");

        var goalState = new GoalState();
        goalState.Arm(new GoalSpec { Condition = "test condition", MaxTurns = 5 });

        var runner = new LoopRunner(session, goalkeeper, goalState, new LoopOptions());

        // ── act ───────────────────────────────────────────────────────────────
        await RunToCompletion(runner, "test objective", TestContext.Current.CancellationToken);

        listener.RecordObservableInstruments();

        // ── assert ────────────────────────────────────────────────────────────
        // Use >= 2 rather than == 2: when parallel tests also drive LoopRunner,
        // the global static meter may emit additional loop.turns measurements into
        // this listener. We verify at least the two turns from this run fired.
        Assert.True(turnsEmitted >= 2, $"Expected at least 2 loop.turns measurements, got {turnsEmitted}");

        Assert.Contains("continue", verdictsEmitted, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("done", verdictsEmitted, StringComparer.OrdinalIgnoreCase);
    }

    // ── T-OBS-3: CapReached outcome tag ──────────────────────────────────────

    /// <summary>
    /// T-OBS-3: when the loop exits with <see cref="LoopOutcome.CapReached"/>,
    /// <c>loop.runs</c> must carry <c>outcome=cap_reached</c>.
    /// </summary>
    [Fact]
    public async Task CapReached_EmitsOutcomeTagCapReached()
    {
        var runsOutcomes = new List<string>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == LoopRunner.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            if (instrument.Name == "loop.runs")
            {
                string? outcome = tags
                    .ToArray()
                    .FirstOrDefault(t => t.Key == "outcome")
                    .Value?.ToString();
                if (outcome is not null)
                {
                    runsOutcomes.Add(outcome);
                }
            }
        });
        listener.Start();

        // Three turns, all Continue (cap at MaxTurns=3).
        const int maxTurns = 3;
        var workerFake = new FakeChatClient();
        for (int i = 0; i < maxTurns; i++)
        {
            workerFake.EnqueueResponse(WorkerResponse($"turn {i}"));
        }

        var goalkeeperFake = new FakeChatClient();
        for (int i = 0; i < maxTurns; i++)
        {
            goalkeeperFake.EnqueueResponse(GoalkeeperContinueResponse());
        }

        var session = new ChatSession(
            new Agent(workerFake, "worker-model", clientType: "FakeProvider"),
            new AgentOptions());
        var goalkeeper = new Goalkeeper(goalkeeperFake, "goalkeeper-model");

        var goalState = new GoalState();
        goalState.Arm(new GoalSpec { Condition = "never", MaxTurns = maxTurns });

        var runner = new LoopRunner(session, goalkeeper, goalState, new LoopOptions());
        await RunToCompletion(runner, "cap test", TestContext.Current.CancellationToken);

        listener.RecordObservableInstruments();

        Assert.Contains(runsOutcomes, o => o.Contains("cap", StringComparison.OrdinalIgnoreCase));
    }
}
