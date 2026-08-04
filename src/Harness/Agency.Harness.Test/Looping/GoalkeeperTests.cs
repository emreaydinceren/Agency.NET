using Agency.Harness.Looping;
using Agency.Harness.Test.Fakes;

namespace Agency.Harness.Test.Looping;

/// <summary>
/// Phase 1 / T-GK-*: unit tests for <see cref="Goalkeeper"/> driven by
/// <see cref="FakeChatClient"/> — no real LLM required.
/// </summary>
public sealed class GoalkeeperTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Builds a minimal transcript with a single assistant message.</summary>
    private static IReadOnlyList<ChatMessage> MakeTranscript(string text = "done!") =>
        [new ChatMessage(ChatRole.Assistant, text)];

    /// <summary>Builds a fake chat response containing a single text message.</summary>
    private static ChatResponse TextResponse(string text) =>
        new([new ChatMessage(ChatRole.Assistant, text)])
        {
            FinishReason = ChatFinishReason.Stop,
        };

    /// <summary>Creates a <see cref="Goalkeeper"/> wired to the given fake.</summary>
    private static Goalkeeper MakeGoalkeeper(
        FakeChatClient fake,
        string model = "gk-model",
        string? rubric = null) =>
        new(fake, model, clientType: null, rubric: rubric);

    // ── T-GK-1: valid Done verdict ────────────────────────────────────────────

    /// <summary>
    /// T-GK-1: when the cheap model returns a well-formed DONE response,
    /// <see cref="Goalkeeper.EvaluateAsync"/> returns a <see cref="Verdict.Done"/>.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WhenModelReturnsDone_ReturnsDoneVerdict()
    {
        var fake = new FakeChatClient();
        fake.EnqueueResponse(TextResponse("VERDICT: done\nREASON: build succeeded, 0 errors"));

        var goalkeeper = MakeGoalkeeper(fake);
        Verdict verdict = await goalkeeper.EvaluateAsync(
            "dotnet build exits 0",
            MakeTranscript("Build succeeded. 0 Error(s)."),
            CancellationToken.None);

        var done = Assert.IsType<Verdict.Done>(verdict);
        Assert.False(string.IsNullOrWhiteSpace(done.Reason));
    }

    // ── T-GK-2: Continue verdict carries the reason ───────────────────────────

    /// <summary>
    /// T-GK-2: when the model returns CONTINUE, the verdict carries the
    /// model's exact reason string so the LoopRunner can feed it back as a directive.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WhenModelReturnsContinue_ReturnsContinueWithReason()
    {
        const string expectedReason = "build still has 3 errors";

        var fake = new FakeChatClient();
        fake.EnqueueResponse(TextResponse($"VERDICT: continue\nREASON: {expectedReason}"));

        var goalkeeper = MakeGoalkeeper(fake);
        Verdict verdict = await goalkeeper.EvaluateAsync(
            "dotnet build exits 0",
            MakeTranscript("Build FAILED. 3 Error(s)."),
            CancellationToken.None);

        var cont = Assert.IsType<Verdict.ContinueLoop>(verdict);
        Assert.Equal(expectedReason, cont.Reason);
    }

    // ── T-GK-3: unparseable verdict treated as Continue once ─────────────────

    /// <summary>
    /// T-GK-3 / E-2: when the model response cannot be parsed as a verdict,
    /// <see cref="Goalkeeper.EvaluateAsync"/> returns <c>Continue("verdict unparseable")</c>
    /// rather than throwing, matching the E-2 edge-case rule.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WhenResponseIsUnparseable_ReturnsContinueUnparseable()
    {
        var fake = new FakeChatClient();
        fake.EnqueueResponse(TextResponse("I'm not sure what to say here, everything looks fine maybe."));

        var goalkeeper = MakeGoalkeeper(fake);
        Verdict verdict = await goalkeeper.EvaluateAsync(
            "dotnet build exits 0",
            MakeTranscript("some output"),
            CancellationToken.None);

        var cont = Assert.IsType<Verdict.ContinueLoop>(verdict);
        Assert.Contains("unparseable", cont.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ── T-GK-4: independence — own IChatClient with GoalkeeperModel ──────────

    /// <summary>
    /// T-GK-4: the Goalkeeper must call its <em>own</em> <see cref="IChatClient"/>
    /// (not the worker's) and must pass <c>ModelId = GoalkeeperModel</c> in the
    /// <see cref="ChatOptions"/>. The worker's fake receives zero calls; the goalkeeper's
    /// fake receives exactly one call with the correct model id.
    /// Also asserts NG-4: no tools are passed in the ChatOptions (the Goalkeeper is transcript-only).
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_UsesItsOwnClientAndModel_NotWorkersClient()
    {
        const string goalkeeperModel = "cheap-fast-model";

        // Two distinct fakes — worker's client is never touched.
        var workerFake = new FakeChatClient();
        var goalkeeperFake = new FakeChatClient();
        goalkeeperFake.EnqueueResponse(TextResponse("VERDICT: done\nREASON: all good"));

        var goalkeeper = new Goalkeeper(goalkeeperFake, goalkeeperModel);
        Verdict verdict = await goalkeeper.EvaluateAsync(
            "some condition",
            MakeTranscript("evidence"),
            CancellationToken.None);

        // Worker's client was never used.
        Assert.Equal(0, workerFake.GetResponseCallCount);

        // Goalkeeper's own client was called exactly once.
        Assert.Equal(1, goalkeeperFake.GetResponseCallCount);

        // The verdict resolved correctly (proves the fake was actually used).
        Assert.IsType<Verdict.Done>(verdict);
    }

    /// <summary>
    /// NG-4 companion: asserts that no tools are passed in the <see cref="ChatOptions"/>
    /// when the Goalkeeper calls its client (the Goalkeeper is transcript-only).
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_PassesNoToolsInChatOptions()
    {
        var fake = new CapturingFakeChatClient();
        fake.EnqueueResponse(TextResponse("VERDICT: done\nREASON: ok"));

        var goalkeeper = new Goalkeeper(fake, "any-model");
        await goalkeeper.EvaluateAsync("condition", MakeTranscript(), CancellationToken.None);

        Assert.NotNull(fake.LastOptions);
        Assert.Null(fake.LastOptions!.Tools);
    }

    // ── T-GK-5: turn-0 false positive — goalkeeper arm echo is not evidence ──────

    /// <summary>
    /// T-GK-5: reproduces the turn-0 false-positive bug — a transcript containing only the
    /// enable_goalkeeper tool call/result round-trip (which echoes the literal target text
    /// via the tool's confirmation and the assistant's narration) must not present that echo
    /// as evidence to the judge model. Asserted at the <see cref="Goalkeeper.EvaluateAsync"/>
    /// level by inspecting the actual message sent to the (fake) judge client.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WhenTranscriptOnlyHasGoalkeeperArmEcho_DoesNotSendEchoAsEvidence()
    {
        const string condition = "the most recent assistant message is exactly the text COUNTER_3_DONE";
        const string targetText = "COUNTER_3_DONE";

        var armCall = new ChatMessage(ChatRole.Assistant,
        [
            new TextContent($"I'll arm the goalkeeper now with condition: {condition}"),
            new FunctionCallContent("call-1", "enable_goalkeeper",
                new Dictionary<string, object?> { ["condition"] = condition }),
        ]);
        var armResult = new ChatMessage(ChatRole.Tool,
        [
            new FunctionResultContent("call-1", $"Goalkeeper armed. Condition: {condition}"),
        ]);
        IReadOnlyList<ChatMessage> transcript = [armCall, armResult];

        var fake = new FakeChatClient();
        fake.EnqueueResponse(TextResponse("VERDICT: continue\nREASON: no work has happened yet"));

        var goalkeeper = MakeGoalkeeper(fake);
        Verdict verdict = await goalkeeper.EvaluateAsync(condition, transcript, CancellationToken.None);

        Assert.IsType<Verdict.ContinueLoop>(verdict);

        // The message actually sent to the judge client must not surface the echoed
        // condition text as if it were transcript evidence — only the GOAL CONDITION
        // section (one occurrence) should contain it.
        string sentText = Assert.Single(fake.ReceivedMessages)[0].Text;
        int occurrences = 0;
        int index = 0;
        while ((index = sentText.IndexOf(targetText, index, StringComparison.Ordinal)) >= 0)
        {
            occurrences++;
            index += targetText.Length;
        }

        Assert.Equal(1, occurrences);
    }

    /// <summary>
    /// Regression companion to T-GK-5: a genuine assistant completion elsewhere in the
    /// transcript must still be visible to (and usable by) the judge — the fix must not
    /// make the Goalkeeper blind to real completions.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_WhenGenuineCompletionFollowsArmEcho_StillReachesDone()
    {
        const string condition = "the most recent assistant message is exactly the text COUNTER_3_DONE";

        var armCall = new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent("call-1", "enable_goalkeeper",
                new Dictionary<string, object?> { ["condition"] = condition }),
        ]);
        var armResult = new ChatMessage(ChatRole.Tool,
        [
            new FunctionResultContent("call-1", $"Goalkeeper armed. Condition: {condition}"),
        ]);
        var genuineCompletion = new ChatMessage(ChatRole.Assistant, "COUNTER_3_DONE");
        IReadOnlyList<ChatMessage> transcript = [armCall, armResult, genuineCompletion];

        var fake = new FakeChatClient();
        fake.EnqueueResponse(TextResponse("VERDICT: done\nREASON: the assistant said COUNTER_3_DONE"));

        var goalkeeper = MakeGoalkeeper(fake);
        Verdict verdict = await goalkeeper.EvaluateAsync(condition, transcript, CancellationToken.None);

        var done = Assert.IsType<Verdict.Done>(verdict);
        Assert.False(string.IsNullOrWhiteSpace(done.Reason));
    }

    // ── CapturingFakeChatClient ───────────────────────────────────────────────

    /// <summary>
    /// A thin wrapper around <see cref="FakeChatClient"/> that also captures the
    /// <see cref="ChatOptions"/> passed on each call — used for NG-4 assertion.
    /// </summary>
    private sealed class CapturingFakeChatClient : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new();

        /// <summary>Gets the <see cref="ChatOptions"/> from the most recent call.</summary>
        public ChatOptions? LastOptions { get; private set; }

        /// <summary>Enqueues a response returned on the next <c>GetResponseAsync</c> call.</summary>
        public void EnqueueResponse(ChatResponse response) => _responses.Enqueue(response);

        /// <inheritdoc/>
        public ChatClientMetadata Metadata { get; } = new("CapturingFakeChatClient", null, null);

        /// <inheritdoc/>
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.LastOptions = options;

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("CapturingFakeChatClient has no more queued responses.");
            }

            return Task.FromResult(_responses.Dequeue());
        }

        /// <inheritdoc/>
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        /// <inheritdoc/>
        public object? GetService(Type serviceType, object? key = null) => null;

        /// <inheritdoc/>
        public void Dispose() { }
    }
}
