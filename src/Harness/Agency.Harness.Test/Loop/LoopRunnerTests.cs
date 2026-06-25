using Agency.Harness.Contexts;
using Agency.Harness.Hooks;
using Agency.Harness.Loop;
using Agency.Harness.Test.Fakes;

namespace Agency.Harness.Test.Loop;

/// <summary>
/// Phase 3 / T-LOOP-*: unit tests for <see cref="LoopRunner"/> driven by a fake
/// <see cref="IGoalkeeper"/> and <see cref="FakeChatClient"/>-backed <see cref="ChatSession"/>.
/// </summary>
public sealed class LoopRunnerTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Builds a <see cref="ChatResponse"/> with a single text message and optional usage.</summary>
    private static ChatResponse TextResponse(
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

    /// <summary>Creates a <see cref="ChatSession"/> backed by the given fake client.</summary>
    private static ChatSession MakeSession(FakeChatClient client) =>
        new(new Agent(client, "worker-model"), new AgentOptions());

    /// <summary>Creates a fake goalkeeper that always returns a <see cref="Verdict.Continue"/>.</summary>
    private static FakeGoalkeeper ContinueGoalkeeper(string reason = "not done yet") =>
        new(new Verdict.Continue(reason));

    /// <summary>Creates a fake goalkeeper that always returns a <see cref="Verdict.Done"/>.</summary>
    private static FakeGoalkeeper DoneGoalkeeper(string reason = "goal achieved") =>
        new(new Verdict.Done(reason));

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

    // ── T-LOOP-0: no goal = pass-through ─────────────────────────────────────

    /// <summary>
    /// T-LOOP-0: when <see cref="GoalState"/> is empty (no goal armed), the runner issues
    /// exactly one turn, never calls the Goalkeeper, and emits <see cref="LoopResultEvent"/>
    /// with <see cref="LoopOutcome.Achieved"/> — behaviourally identical to plain
    /// <see cref="ChatSession"/>.
    /// </summary>
    [Fact]
    public async Task NoGoal_RunsExactlyOneTurn_NoGoalkeeperCall_EmitsAchieved()
    {
        var workerFake = new FakeChatClient();
        workerFake.EnqueueResponse(TextResponse("hello"));

        var goalState = new GoalState(); // unarmed
        var goalkeeper = new CountingFakeGoalkeeper(); // tracks call count
        var runner = new LoopRunner(
            MakeSession(workerFake),
            goalkeeper,
            goalState,
            new LoopOptions());

        List<AgentEvent> events = await RunToCompletion(runner, ct: TestContext.Current.CancellationToken);

        // Exactly one worker call.
        Assert.Equal(1, workerFake.GetResponseCallCount);

        // Goalkeeper was never called.
        Assert.Equal(0, goalkeeper.EvaluateCallCount);

        // Terminal LoopResult is Achieved.
        LoopResultEvent loopResult = Assert.Single(events.OfType<LoopResultEvent>());
        Assert.Equal(LoopOutcome.Achieved, loopResult.Outcome);
    }

    // ── T-LOOP-1: gate runs every turn ───────────────────────────────────────

    /// <summary>
    /// T-LOOP-1: goal armed, worker "stops" but Goalkeeper returns <see cref="Verdict.Continue"/>
    /// → a second turn is issued; the Goalkeeper must fire after turn 0 and then again after turn 1.
    /// </summary>
    [Fact]
    public async Task GoalArmed_GoalkeeperContinueThenDone_IssuesTwoTurns()
    {
        var workerFake = new FakeChatClient();
        workerFake.EnqueueResponse(TextResponse("first result"));
        workerFake.EnqueueResponse(TextResponse("second result"));

        var goalState = new GoalState();
        goalState.Arm(new GoalSpec { Condition = "something finished", MaxTurns = 5 });

        // First evaluation → Continue, second → Done.
        var goalkeeper = new SequencedFakeGoalkeeper(
            new Verdict.Continue("keep going"),
            new Verdict.Done("done now"));

        var runner = new LoopRunner(
            MakeSession(workerFake),
            goalkeeper,
            goalState,
            new LoopOptions());

        List<AgentEvent> events = await RunToCompletion(runner, ct: TestContext.Current.CancellationToken);

        // Two worker calls.
        Assert.Equal(2, workerFake.GetResponseCallCount);

        // Goalkeeper fired twice (after turn 0 and turn 1).
        Assert.Equal(2, goalkeeper.EvaluateCallCount);

        // Terminal result: Achieved.
        LoopResultEvent loopResult = Assert.Single(events.OfType<LoopResultEvent>());
        Assert.Equal(LoopOutcome.Achieved, loopResult.Outcome);
    }

    // ── T-LOOP-2: happy path — event order ────────────────────────────────────

    /// <summary>
    /// T-LOOP-2: goal armed, turn 0 → Goalkeeper returns <see cref="Verdict.Done"/>.
    /// Assert event order: GoalSet, TurnStarted, AgentResult, Verdict, LoopResult(Achieved).
    /// Also asserts <see cref="GoalState.Clear"/> was called (auto-disarm, E-13).
    /// </summary>
    [Fact]
    public async Task HappyPath_GoalkeeperDoneOnTurn0_EmitsCorrectEventOrder_GoalCleared()
    {
        var workerFake = new FakeChatClient();
        workerFake.EnqueueResponse(TextResponse("work done"));

        var goalState = new GoalState();
        var spec = new GoalSpec { Condition = "work done", MaxTurns = 5 };
        goalState.Arm(spec);

        var goalkeeper = DoneGoalkeeper("condition satisfied");

        var runner = new LoopRunner(
            MakeSession(workerFake),
            goalkeeper,
            goalState,
            new LoopOptions());

        List<AgentEvent> events = await RunToCompletion(runner, ct: TestContext.Current.CancellationToken);

        // Verify event order per §8 algorithm (goal is read AFTER the turn):
        //   TurnStarted(0) → inner events → AgentResult → GoalSet → Verdict → LoopResult
        // (GoalSet emits on first goal observation, which is after the first turn completes.)
        int turnStartedIdx = IndexOf<TurnStartedEvent>(events);
        int agentResultIdx = IndexOf<AgentResultEvent>(events);
        int goalSetIdx = IndexOf<GoalSetEvent>(events);
        int verdictIdx = IndexOf<VerdictEvent>(events);
        int loopResultIdx = IndexOf<LoopResultEvent>(events);

        Assert.True(turnStartedIdx >= 0, "TurnStartedEvent must be emitted");
        Assert.True(agentResultIdx >= 0, "AgentResultEvent must be emitted");
        Assert.True(goalSetIdx >= 0, "GoalSetEvent must be emitted");
        Assert.True(verdictIdx >= 0, "VerdictEvent must be emitted");
        Assert.True(loopResultIdx >= 0, "LoopResultEvent must be emitted");

        Assert.True(turnStartedIdx < agentResultIdx, "TurnStarted must come before AgentResult");
        Assert.True(agentResultIdx < goalSetIdx, "AgentResult must come before GoalSet");
        Assert.True(goalSetIdx < verdictIdx, "GoalSet must come before Verdict");
        Assert.True(verdictIdx < loopResultIdx, "Verdict must come before LoopResult");
        Assert.True(loopResultIdx == events.Count - 1, "LoopResult must be the last event");

        // Terminal outcome: Achieved.
        LoopResultEvent loopResult = (LoopResultEvent)events[loopResultIdx];
        Assert.Equal(LoopOutcome.Achieved, loopResult.Outcome);

        // Goal auto-cleared after terminal outcome (E-13).
        Assert.False(goalState.IsArmed, "GoalState must be cleared after Achieved");
    }

    // ── T-LOOP-3: termination — core property ─────────────────────────────────

    /// <summary>
    /// T-LOOP-3: Goalkeeper always returns <see cref="Verdict.Continue"/> → the loop exits
    /// at <see cref="GoalSpec.MaxTurns"/> with <see cref="LoopOutcome.CapReached"/>.
    /// Also asserts <see cref="GoalState"/> is cleared afterward (E-14 — bounded by construction).
    /// </summary>
    [Fact]
    public async Task GoalkeeperAlwaysContinues_ExitsAtMaxTurns_CapReached_GoalCleared()
    {
        const int maxTurns = 3;

        var workerFake = new FakeChatClient();
        for (int i = 0; i < maxTurns; i++)
        {
            workerFake.EnqueueResponse(TextResponse($"turn {i} result"));
        }

        var goalState = new GoalState();
        goalState.Arm(new GoalSpec { Condition = "never satisfied", MaxTurns = maxTurns });

        var goalkeeper = ContinueGoalkeeper("still not done");

        var runner = new LoopRunner(
            MakeSession(workerFake),
            goalkeeper,
            goalState,
            new LoopOptions());

        List<AgentEvent> events = await RunToCompletion(runner, ct: TestContext.Current.CancellationToken);

        // Exactly maxTurns worker calls.
        Assert.Equal(maxTurns, workerFake.GetResponseCallCount);

        // Terminal outcome: CapReached.
        LoopResultEvent loopResult = Assert.Single(events.OfType<LoopResultEvent>());
        Assert.Equal(LoopOutcome.CapReached, loopResult.Outcome);

        // Goal cleared after cap hit (E-14).
        Assert.False(goalState.IsArmed, "GoalState must be cleared after CapReached");
    }

    // ── T-LOOP-4: budget exceeded ─────────────────────────────────────────────

    /// <summary>
    /// T-LOOP-4: <see cref="GoalSpec.TokenBudget"/> exceeded after the first turn →
    /// <see cref="LoopOutcome.BudgetExceeded"/> with best-effort text; goal cleared.
    /// </summary>
    [Fact]
    public async Task TokenBudgetExceeded_EmitsBudgetExceeded_GoalCleared()
    {
        var workerFake = new FakeChatClient();
        // Each response gives 100 tokens; budget is 50 → exceeded after first turn.
        workerFake.EnqueueResponse(TextResponse("result", inputTokens: 60, outputTokens: 60));

        var goalState = new GoalState();
        goalState.Arm(new GoalSpec
        {
            Condition = "never satisfied",
            MaxTurns = 10,
            TokenBudget = 50L,  // 50 total tokens; first turn uses 120
        });

        var goalkeeper = ContinueGoalkeeper();

        var runner = new LoopRunner(
            MakeSession(workerFake),
            goalkeeper,
            goalState,
            new LoopOptions());

        List<AgentEvent> events = await RunToCompletion(runner, ct: TestContext.Current.CancellationToken);

        // Terminal outcome: BudgetExceeded.
        LoopResultEvent loopResult = Assert.Single(events.OfType<LoopResultEvent>());
        Assert.Equal(LoopOutcome.BudgetExceeded, loopResult.Outcome);

        // Goal cleared.
        Assert.False(goalState.IsArmed, "GoalState must be cleared after BudgetExceeded");
    }

    // ── T-LOOP-5: feedback — Continue.Reason becomes next directive ───────────

    /// <summary>
    /// T-LOOP-5: when Goalkeeper returns <see cref="Verdict.Continue"/>, its
    /// <see cref="Verdict.Continue.Reason"/> must become the next turn's directive.
    /// Assert the worker's second call received the reason as the user message.
    /// </summary>
    [Fact]
    public async Task ContinueReason_BecomesNextDirective()
    {
        const string feedbackReason = "fix the build errors on line 42";

        var workerFake = new FakeChatClient();
        workerFake.EnqueueResponse(TextResponse("attempt 1"));
        workerFake.EnqueueResponse(TextResponse("attempt 2"));

        var goalState = new GoalState();
        goalState.Arm(new GoalSpec { Condition = "build passes", MaxTurns = 5 });

        // First → Continue(reason=feedbackReason), second → Done.
        var goalkeeper = new SequencedFakeGoalkeeper(
            new Verdict.Continue(feedbackReason),
            new Verdict.Done("done"));

        var runner = new LoopRunner(
            MakeSession(workerFake),
            goalkeeper,
            goalState,
            new LoopOptions());

        List<AgentEvent> events = await RunToCompletion(runner, ct: TestContext.Current.CancellationToken);

        // Second worker call must have received the reason as a user message in its conversation.
        Assert.True(workerFake.ReceivedMessages.Count >= 2, "Worker must have been called at least twice");

        // The second call's messages must contain the feedback reason as a user message.
        IReadOnlyList<ChatMessage> secondCallMessages = workerFake.ReceivedMessages[1];
        bool reasonSentAsUserMessage = secondCallMessages.Any(
            m => m.Role == ChatRole.User && (m.Text?.Contains(feedbackReason) ?? false));

        Assert.True(
            reasonSentAsUserMessage,
            $"Expected '{feedbackReason}' to appear as a user message in the second worker call.");

        // Terminal: Achieved.
        LoopResultEvent loopResult = Assert.Single(events.OfType<LoopResultEvent>());
        Assert.Equal(LoopOutcome.Achieved, loopResult.Outcome);
    }

    // ── T-LOOP-6: park preserves goal ─────────────────────────────────────────

    /// <summary>
    /// T-LOOP-6 / E-16: when an inner turn returns <see cref="AgentResultStatus.AwaitingPermission"/>,
    /// the runner returns WITHOUT running the Goalkeeper and WITHOUT clearing <see cref="GoalState"/>
    /// — the goal survives so re-entry can continue toward the same goal.
    /// </summary>
    [Fact]
    public async Task ParkOnAwaitingPermission_GoalkeeperNotCalled_GoalPreserved()
    {
        // A turn that parks: the worker calls a tool that needs permission.
        // We set up a FakeChatClient that simulates AwaitingPermission.
        // The simplest approach: register a tool that requires permission (Ask decision),
        // then the agent parks. We drive this via ChatSession with a permission evaluator.
        // Actually for simplicity, we directly control what AgentResultStatus comes back
        // by using a hook that makes the turn park.
        // The cleanest path: use a real Agent + FakeChatClient that returns a tool call,
        // register the tool via a permission evaluator that says "Ask". But that's complex.
        // Instead: use the park path already tested in AgentPermissionParkTests.
        // We use a hook-based Ask to force AwaitingPermission.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        var toolResponse = new ChatResponse(
            [new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "some_tool")])])
        {
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 },
            FinishReason = ChatFinishReason.ToolCalls,
        };

        var workerFake = new FakeChatClient();
        workerFake.EnqueueResponse(toolResponse);

        // Hook that forces Ask on every tool use.
        var hooks = new AgentHooks
        {
            OnPreToolUse = (_, _) =>
                Task.FromResult<PreToolUseDecision>(new PreToolUseDecision.Ask("needs permission")),
        };

        var tool = new FakeTool("some_tool", _ => new ToolResult("ok"));
        var toolContext = new ToolContext { Registry = new ToolRegistry([tool]) };

        var agent = new Agent(workerFake, "worker-model", hooks: hooks);
        var session = new ChatSession(agent, new AgentOptions(), toolContext: toolContext);

        var goalState = new GoalState();
        goalState.Arm(new GoalSpec { Condition = "anything", MaxTurns = 5 });

        var goalkeeper = new CountingFakeGoalkeeper();

        var runner = new LoopRunner(session, goalkeeper, goalState, new LoopOptions());

        List<AgentEvent> events = await RunToCompletion(
            runner, "do the thing", cts.Token);

        // The runner returned WITHOUT emitting a LoopResultEvent (parked).
        Assert.Empty(events.OfType<LoopResultEvent>());

        // Goalkeeper was NOT called (park is not a judge point).
        Assert.Equal(0, goalkeeper.EvaluateCallCount);

        // GoalState is still armed — the goal survived the park.
        Assert.True(goalState.IsArmed, "GoalState must remain armed after park");
    }

    // ── T-LOOP-7: cancel clears goal ─────────────────────────────────────────

    /// <summary>
    /// T-LOOP-7 / E-14: cancelling mid-loop propagates <see cref="OperationCanceledException"/>
    /// and <see cref="GoalState"/> is cleared in the <c>finally</c>.
    /// </summary>
    [Fact]
    public async Task Cancellation_ThrowsOperationCancelled_GoalCleared()
    {
        using var cts = new CancellationTokenSource();

        var goalState = new GoalState();
        goalState.Arm(new GoalSpec { Condition = "never satisfied", MaxTurns = 5 });

        var workerFake = new FakeChatClient();
        // Enqueue one response, but cancel before/after it.
        workerFake.EnqueueResponse(TextResponse("partial work"));

        var goalkeeper = new CountingFakeGoalkeeper();

        // Use a goalkeeper that cancels when called.
        var cancellingGoalkeeper = new CancellingFakeGoalkeeper(cts);

        var runner = new LoopRunner(
            MakeSession(workerFake),
            cancellingGoalkeeper,
            goalState,
            new LoopOptions());

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await RunToCompletion(runner, "do something", cts.Token));

        // Goal must be cleared in the finally (E-14).
        Assert.False(goalState.IsArmed, "GoalState must be cleared after cancellation");
    }

    // ── T-LOOP-8: disarm mid-run ──────────────────────────────────────────────

    /// <summary>
    /// T-LOOP-8 / E-15: when Goalkeeper returns <see cref="Verdict.Continue"/>, but
    /// <see cref="GoalState.Clear"/> happens before the next turn (model called
    /// <c>disable_goalkeeper</c>), the loop ends as a plain turn with no further Goalkeeper calls.
    /// </summary>
    [Fact]
    public async Task DisarmMidRun_AfterContinue_LoopEndsAsPlainTurn_NoFurtherGoalkeeperCalls()
    {
        var workerFake = new FakeChatClient();
        workerFake.EnqueueResponse(TextResponse("turn 0"));
        workerFake.EnqueueResponse(TextResponse("turn 1 after disarm"));

        var goalState = new GoalState();
        goalState.Arm(new GoalSpec { Condition = "never satisfied", MaxTurns = 5 });

        // Goalkeeper: first call → Continue, then clears the goal (simulating disable_goalkeeper),
        // second call should NEVER happen.
        var disarmingGoalkeeper = new DisarmingFakeGoalkeeper(goalState, new Verdict.Continue("keep going"));

        var runner = new LoopRunner(
            MakeSession(workerFake),
            disarmingGoalkeeper,
            goalState,
            new LoopOptions());

        List<AgentEvent> events = await RunToCompletion(runner, ct: TestContext.Current.CancellationToken);

        // Goalkeeper called exactly once (after turn 0), not after the disarmed turn 1.
        Assert.Equal(1, disarmingGoalkeeper.EvaluateCallCount);

        // Terminal LoopResult: Achieved (plain pass-through after disarm).
        LoopResultEvent loopResult = Assert.Single(events.OfType<LoopResultEvent>());
        Assert.Equal(LoopOutcome.Achieved, loopResult.Outcome);
    }

    // ── Index helpers ─────────────────────────────────────────────────────────

    private static int IndexOf<T>(List<AgentEvent> events) where T : AgentEvent
    {
        for (int i = 0; i < events.Count; i++)
        {
            if (events[i] is T)
            {
                return i;
            }
        }

        return -1;
    }

    // ── Fake goalkeepers ──────────────────────────────────────────────────────

    /// <summary>Fake goalkeeper that counts calls but always returns Continue.</summary>
    private sealed class CountingFakeGoalkeeper : IGoalkeeper
    {
        public int EvaluateCallCount { get; private set; }

        public Task<Verdict> EvaluateAsync(
            string condition,
            IReadOnlyList<ChatMessage> transcript,
            CancellationToken ct)
        {
            this.EvaluateCallCount++;
            return Task.FromResult<Verdict>(new Verdict.Continue("not done"));
        }
    }

    /// <summary>Fake goalkeeper that always returns a fixed verdict.</summary>
    private sealed class FakeGoalkeeper(Verdict verdict) : IGoalkeeper
    {
        public Task<Verdict> EvaluateAsync(
            string condition,
            IReadOnlyList<ChatMessage> transcript,
            CancellationToken ct)
            => Task.FromResult(verdict);
    }

    /// <summary>Fake goalkeeper that returns verdicts from a pre-defined sequence.</summary>
    private sealed class SequencedFakeGoalkeeper : IGoalkeeper
    {
        private readonly Queue<Verdict> _verdicts;

        public int EvaluateCallCount { get; private set; }

        public SequencedFakeGoalkeeper(params Verdict[] verdicts)
        {
            this._verdicts = new Queue<Verdict>(verdicts);
        }

        public Task<Verdict> EvaluateAsync(
            string condition,
            IReadOnlyList<ChatMessage> transcript,
            CancellationToken ct)
        {
            this.EvaluateCallCount++;

            if (this._verdicts.Count == 0)
            {
                throw new InvalidOperationException("SequencedFakeGoalkeeper ran out of verdicts.");
            }

            return Task.FromResult(this._verdicts.Dequeue());
        }
    }

    /// <summary>
    /// Fake goalkeeper that cancels the provided <see cref="CancellationTokenSource"/> on first call,
    /// then throws <see cref="OperationCanceledException"/> to simulate cancellation mid-loop.
    /// </summary>
    private sealed class CancellingFakeGoalkeeper(CancellationTokenSource cts) : IGoalkeeper
    {
        public Task<Verdict> EvaluateAsync(
            string condition,
            IReadOnlyList<ChatMessage> transcript,
            CancellationToken ct)
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<Verdict>(new Verdict.Continue("cancelled"));
        }
    }

    /// <summary>
    /// Fake goalkeeper that calls <see cref="GoalState.Clear"/> on first evaluation (simulating
    /// the model calling <c>disable_goalkeeper</c>) and returns Continue. Tracks call count.
    /// </summary>
    private sealed class DisarmingFakeGoalkeeper(GoalState goalState, Verdict firstVerdict) : IGoalkeeper
    {
        public int EvaluateCallCount { get; private set; }

        public Task<Verdict> EvaluateAsync(
            string condition,
            IReadOnlyList<ChatMessage> transcript,
            CancellationToken ct)
        {
            this.EvaluateCallCount++;
            // Simulate the model calling disable_goalkeeper during the turn.
            goalState.Clear();
            return Task.FromResult(firstVerdict);
        }
    }
}
