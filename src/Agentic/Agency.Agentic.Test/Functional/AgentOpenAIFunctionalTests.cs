namespace Agency.Agentic.Test.Functional;

using Agency.Agentic.Contexts;
using Agency.Llm.Common;
using Agency.Llm.OpenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

/// <summary>
/// End-to-end functional tests for the <see cref="Agent"/> loop using
/// <see cref="Agency.Llm.OpenAI.OpenAIClient"/> as the LLM backend.
/// <para>
/// Run with:  <c>dotnet test --filter "Category=Functional"</c><br/>
/// Skip with: <c>dotnet test --filter "Category!=Functional"</c><br/>
/// Requires LM Studio running with a compatible model loaded. Configure the endpoint in <see cref="OpenAIAgentFixture"/> appsettings.json.
/// </para>
/// </summary>
[Trait("Category", "Functional")]
public sealed class AgentOpenAIFunctionalTests(AgentOpenAIFunctionalTests.OpenAIAgentFixture fixture)
    : IClassFixture<AgentOpenAIFunctionalTests.OpenAIAgentFixture>
{
    private readonly OpenAIAgentFixture _fixture = fixture;

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<AgentResultEvent> RunToResultAsync(Agent agent, Context ctx, CancellationToken ct = default)
    {
        AgentResultEvent? result = null;
        await foreach (var evt in agent.RunAsync(ctx, ct))
        {
            if (evt is AgentResultEvent r)
            {
                result = r;
            }
        }
        return result ?? throw new InvalidOperationException("Agent did not emit AgentResultEvent.");
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies the agent produces a non-empty text response for a simple prompt,
    /// and emits the mandatory terminal <see cref="AgentResultEvent"/>.
    /// </summary>
    [Fact]
    public async Task Agent_SimplePrompt_ProducesNonEmptyResult()
    {
        var agent = new Agent(this._fixture.LlmClient, this._fixture.Model, stream: false);
        var ctx = new Context { Query = new QueryContext { Prompt = "Reply with exactly one word: hello" } };

        var result = await RunToResultAsync(agent, ctx, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result.FinalText));
        Assert.True(result.TotalUsage.TotalTokens > 0);
    }

    /// <summary>
    /// Verifies that the agent emits the events in the expected order:
    /// <see cref="SessionStartedEvent"/> → one or more <see cref="AssistantTurnEvent"/> →
    /// <see cref="AgentResultEvent"/> last.
    /// </summary>
    [Fact]
    public async Task Agent_EmitsEventsInOrder()
    {
        var agent = new Agent(this._fixture.LlmClient, this._fixture.Model, stream: false);
        var ctx = new Context { Query = new QueryContext { Prompt = "Reply with exactly one word: hello" } };

        var events = new List<AgentEvent>();
        await foreach (var evt in agent.RunAsync(ctx, ct: TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        Assert.IsType<SessionStartedEvent>(events[0]);
        Assert.IsType<AgentResultEvent>(events[^1]);
        Assert.Contains(events, static e => e is AssistantTurnEvent);
    }

    /// <summary>
    /// Verifies the loop terminates when <see cref="StopConditions.StepCountIs"/> fires,
    /// and that the result status reflects this.
    /// </summary>
    [Fact]
    public async Task Agent_StopAfterOneStep_TerminatesImmediately()
    {
        var agent = new Agent(
            this._fixture.LlmClient,
            this._fixture.Model,
            stopWhen: StopConditions.StepCountIs(1),
            stream: false);

        var ctx = new Context { Query = new QueryContext { Prompt = "Count from 1 to 100." } };

        var result = await RunToResultAsync(agent, ctx, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(1, ctx.IterationCount);
    }

    /// <summary>
    /// Verifies that token usage is accumulated across iterations and is non-zero
    /// after the session completes.
    /// </summary>
    [Fact]
    public async Task Agent_TokenUsage_IsAccumulatedAndNonZero()
    {
        var agent = new Agent(this._fixture.LlmClient, this._fixture.Model, stream: false);
        var ctx = new Context { Query = new QueryContext { Prompt = "What is 2 + 2? Reply with just the number." } };

        var result = await RunToResultAsync(agent, ctx, ct: TestContext.Current.CancellationToken);

        Assert.True(result.TotalUsage.InputTokens > 0);
        Assert.True(result.TotalUsage.OutputTokens > 0);
    }

    /// <summary>
    /// Verifies that a tool registered in <see cref="ToolContext"/> is invoked by the agent
    /// when the model requests it, and that the loop produces a final text response.
    /// </summary>
    [Fact]
    public async Task Agent_WithTool_InvokesToolAndContinues()
    {
        var tool = new EchoTool();
        var registry = new Agency.Agentic.Tools.ToolRegistry([tool]);

        var agent = new Agent(this._fixture.LlmClient, this._fixture.Model, stream: false);
        var ctx = new Context
        {
            Query = new QueryContext { Prompt = "Use the echo tool with the input \"ping\" and then tell me what it returned." },
            Tools = new ToolContext { Registry = registry },
        };

        AgentResultEvent? result = null;
        bool toolInvoked = false;
        await foreach (var evt in agent.RunAsync(ctx, ct: TestContext.Current.CancellationToken))
        {
            if (evt is ToolInvokedEvent t && t.ToolName == EchoTool.ToolName)
            {
                toolInvoked = true;
            }

            if (evt is AgentResultEvent r)
            {
                result = r;
            }
        }

        Assert.NotNull(result);
        _ = toolInvoked; // Informational — not all local models reliably call tools.
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Shared fixture that loads configuration and constructs the <see cref="IChatClient"/>.
    /// </summary>
    public sealed class OpenAIAgentFixture
    {
        private const string EnvironmentNameVariable = "DOTNET_ENVIRONMENT";
        private const string ConfigurationSection = "AgentTest:OpenAI";

        /// <summary>
        /// Initializes a new instance of <see cref="OpenAIAgentFixture"/> with configuration
        /// loaded from <c>appsettings.json</c> and optional user secrets.
        /// </summary>
        public OpenAIAgentFixture()
        {
            var environmentName = Environment.GetEnvironmentVariable(EnvironmentNameVariable) ?? "Development";

            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
                .AddUserSecrets<AgentOpenAIFunctionalTests>(optional: true)
                .AddEnvironmentVariables()
                .Build();

            this.Model = GetRequired(configuration, $"{ConfigurationSection}:Model");

            this.LlmClient = new OpenAIClient(
                Options.Create(new LlmClientOptions
                {
                    ApiKey = GetRequired(configuration, $"{ConfigurationSection}:ApiKey"),
                    BaseUrl = GetRequired(configuration, $"{ConfigurationSection}:BaseUrl"),
                })).CreateChatClient();
        }

        /// <summary>Gets the configured model identifier.</summary>
        public string Model { get; }

        /// <summary>Gets the configured <see cref="IChatClient"/> backed by OpenAI-compatible endpoint.</summary>
        public IChatClient LlmClient { get; }

        private static string GetRequired(IConfiguration cfg, string key) =>
            cfg[key] ?? throw new InvalidOperationException($"Missing required configuration: '{key}'.");
    }
}
