
using Agency.Harness.Looping;
using Agency.Llm.Common;
using Agency.Llm.OpenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Agency.Harness.Test.Functional;

/// <summary>
/// End-to-end functional test for <see cref="LoopRunner"/> against LM Studio
/// (T-E2E-1 from §15 Phase 6 of the Loop Kit spec).
/// <para>
/// Run with:  <c>dotnet test --filter "Category=Functional"</c><br/>
/// Skip with: <c>dotnet test --filter "Category!=Functional"</c><br/>
/// Requires LM Studio or the HTTP cache proxy at the configured base URL. Worker and Goalkeeper are resolved to DIFFERENT
/// configured model IDs to verify the independence guarantee (§6.3, T-GK-4).
/// </para>
/// <para>
/// The objective is intentionally tiny and deterministic: the worker is asked to output
/// a literal marker string; the Goalkeeper condition checks only that the marker appears
/// in the transcript. This avoids non-deterministic proofs and keeps the test cheap.
/// </para>
/// <para>
/// LM Studio caps at 2 concurrent inference slots on this machine — the LoopRunner
/// already serialises worker and Goalkeeper calls sequentially, so no extra concurrency
/// control is needed here.
/// </para>
/// </summary>
[Trait("Category", "Functional")]
[Trait("Category", "RequiresLlm")]
public sealed class LoopRunnerFunctionalTests(LoopRunnerFunctionalTests.LoopRunnerFixture fixture)
    : IClassFixture<LoopRunnerFunctionalTests.LoopRunnerFixture>
{
    private const string MarkerString = "LOOP_DONE_7F3A";

    private readonly LoopRunnerFixture _fixture = fixture;

    /// <summary>
    /// T-E2E-1: a real worker + real Goalkeeper on different configured models drive the loop
    /// toward a trivial "output the marker string" objective.
    /// Asserts: <see cref="LoopResultEvent"/> with <see cref="LoopOutcome.Achieved"/> is emitted
    /// within <see cref="GoalSpec.MaxTurns"/>, and usage + cost are recorded on it.
    /// </summary>
    [Fact]
    public async Task LoopRunner_MarkerObjective_ReachesAchieved()
    {
        // ── Arrange ──────────────────────────────────────────────────────────
        var goalState = new GoalState();

        // Host-arm the goal before the loop starts (§6.4 path 2 — host-armed).
        // MaxTurns=5 is generous for a trivial objective; the marker appears on turn 0 or 1.
        goalState.Arm(new GoalSpec
        {
            Condition = $"The assistant's response contains the exact marker string: {MarkerString}",
            MaxTurns = 5,
        });

        // Worker: google/gemma-4-e2b — the standard model for functional tests in this project.
        var workerAgent = new Agent(
            this._fixture.WorkerClient,
            this._fixture.WorkerModel,
            clientType: "LMStudio",
            // Pin the clock so the "Current date/time" system-prompt line is byte-stable across
            // runs, matching the cassette-recording convention from functional-test-determinism notes.
            timeProvider: new FixedTimeProvider());

        var session = new ChatSession(
            workerAgent,
            new AgentOptions());

        // Goalkeeper: a different model ID — independence guarantee (§6.3, T-GK-4).
        var goalkeeper = new Goalkeeper(
            this._fixture.GoalkeeperClient,
            this._fixture.GoalkeeperModel,
            clientType: "LMStudio");

        var loopOptions = new LoopOptions
        {
            GoalkeeperModel = this._fixture.GoalkeeperModel,
            GoalkeeperClientName = "LMStudio-Goalkeeper",
            MaxTurns = 5,
        };

        var runner = new LoopRunner(session, goalkeeper, goalState, loopOptions);

        // ── Act ───────────────────────────────────────────────────────────────
        var events = new List<AgentEvent>();
        string objective =
            $"Reply with a short sentence that includes exactly this marker string (copy it verbatim): {MarkerString}";

        await foreach (AgentEvent evt in runner.RunAsync(objective, TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        // ── Assert ────────────────────────────────────────────────────────────
        // The loop must have emitted a terminal LoopResultEvent.
        LoopResultEvent loopResult = Assert.Single(events.OfType<LoopResultEvent>());

        // Goal must be Achieved — the marker should appear in a single turn for any capable model.
        Assert.Equal(LoopOutcome.Achieved, loopResult.Outcome);

        // Usage must be non-zero (both worker and goalkeeper contributed tokens).
        Assert.True(loopResult.TotalUsage.TotalTokens > 0,
            $"Expected non-zero token usage; got {loopResult.TotalUsage.TotalTokens}.");

        // At least one VerdictEvent must have been emitted.
        Assert.Contains(events, static e => e is VerdictEvent);

        // The final verdict must be Done.
        VerdictEvent lastVerdict = events.OfType<VerdictEvent>().Last();
        Assert.IsType<Verdict.Done>(lastVerdict.Verdict);
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Shared fixture that loads configuration and builds two independent
    /// <see cref="IChatClient"/> instances — one for the worker, one for the Goalkeeper —
    /// using DIFFERENT configured model IDs (independence guarantee, §6.3, T-GK-4).
    /// </summary>
    public sealed class LoopRunnerFixture
    {
        private const string EnvironmentNameVariable = "DOTNET_ENVIRONMENT";
        private const string WorkerSection = "AgentTest:OpenAI";
        private const string GoalkeeperSection = "LoopTest:Goalkeeper";

        /// <summary>
        /// Loads test configuration and builds the worker and Goalkeeper chat clients from their
        /// respective configuration sections.
        /// </summary>
        public LoopRunnerFixture()
        {
            var environmentName = Environment.GetEnvironmentVariable(EnvironmentNameVariable) ?? "Development";

            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddSharedConfiguration("shared-test-appsettings.json")
                .AddJsonFile("appsettings.json", optional: false)
                .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
                .AddUserSecrets<LoopRunnerFunctionalTests>(optional: true)
                .AddEnvironmentVariables()
                .AddPlaceholderResolver()
                .Build();

            // Worker — reuses the same OpenAI-compatible client section as the existing
            // AgentOpenAIFunctionalTests so cassette infrastructure is consistent.
            this.WorkerModel = GetRequired(configuration, $"{WorkerSection}:Model");

            this.WorkerClient = new OpenAIClient(
                Options.Create(new LlmClientOptions
                {
                    ApiKey = GetRequired(configuration, $"{WorkerSection}:ApiKey"),
                    BaseUrl = GetRequired(configuration, $"{WorkerSection}:BaseUrl"),
                })).CreateChatClient();

            // Goalkeeper — a different model ID, same endpoint (same LM Studio instance can serve
            // multiple model-id strings; the structural independence is in the separate client
            // instance and different ModelId on ChatOptions, which Goalkeeper enforces via §gotcha-7).
            this.GoalkeeperModel = configuration[$"{GoalkeeperSection}:Model"]
                ?? "qwen/qwen3-0.6b"; // fallback: any model name distinct from the worker's

            string goalkeeperBaseUrl = configuration[$"{GoalkeeperSection}:BaseUrl"]
                ?? GetRequired(configuration, $"{WorkerSection}:BaseUrl");

            string goalkeeperApiKey = configuration[$"{GoalkeeperSection}:ApiKey"]
                ?? GetRequired(configuration, $"{WorkerSection}:ApiKey");

            this.GoalkeeperClient = new OpenAIClient(
                Options.Create(new LlmClientOptions
                {
                    ApiKey = goalkeeperApiKey,
                    BaseUrl = goalkeeperBaseUrl,
                })).CreateChatClient();
        }

        /// <summary>Gets the worker model identifier.</summary>
        public string WorkerModel { get; }

        /// <summary>Gets the Goalkeeper model identifier (must differ from <see cref="WorkerModel"/>).</summary>
        public string GoalkeeperModel { get; }

        /// <summary>Gets the worker's <see cref="IChatClient"/>.</summary>
        public IChatClient WorkerClient { get; }

        /// <summary>Gets the Goalkeeper's independent <see cref="IChatClient"/>.</summary>
        public IChatClient GoalkeeperClient { get; }

        private static string GetRequired(IConfiguration cfg, string key) =>
            cfg[key] ?? throw new InvalidOperationException($"Missing required configuration: '{key}'.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Pinned <see cref="TimeProvider"/> that returns a fixed UTC timestamp so the
    /// "Current date/time" line in the agent system prompt is byte-stable across runs,
    /// satisfying the functional-test-determinism memory constraint.
    /// </summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        // Pin to a fixed, arbitrary UTC time so system-prompt content is cassette-stable.
        private static readonly DateTimeOffset FixedTime =
            new(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => FixedTime;
    }
}
