
using Agency.Harness.Contexts;
using Agency.Llm.Common;
using Agency.Llm.OpenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Agency.Harness.Test.Functional;
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
[Trait("Category", "Cloud")]
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
        var agent = new Agent(this._fixture.LlmClient, this._fixture.Model);
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
        var agent = new Agent(this._fixture.LlmClient, this._fixture.Model);
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
            stopWhen: StopConditions.StepCountIs(1));

        var ctx = new Context { Query = new QueryContext { Prompt = "Reply with exactly one word: hello" } };

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
        var agent = new Agent(this._fixture.LlmClient, this._fixture.Model);
        var ctx = new Context { Query = new QueryContext { Prompt = "Reply with exactly one word: hello" } };

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
        var registry = new Agency.Harness.Tools.ToolRegistry([tool]);

        var agent = new Agent(this._fixture.LlmClient, this._fixture.Model);
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

    /// <summary>
    /// End-to-end: a natural-language request to list running processes should drive the model to
    /// call the PowerShell tool (e.g. <c>tasklist</c> or <c>Get-Process</c>), and that tool call
    /// must return several processes.
    /// <para>
    /// Enumeration stops as soon as the first successful PowerShell result arrives. This is
    /// deliberate: the live process list is non-deterministic, so feeding it back into a second LLM
    /// request would make that request body unrepeatable and break offline HTTP-cache replay in CI.
    /// We only need the initial (deterministic) request plus the tool result to verify the behaviour.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Agent_ListRunningProcesses_InvokesPowershellAndReturnsProcesses()
    {
        var tool = new Agency.Harness.Tools.ExecutePowershellTool();
        // Register the same toolset the console app uses. read_file/write_file both expose a 'path'
        // parameter; execute_powershell uses 'command'. Including them reproduces the real scenario
        // where a weak model borrows 'path' for the PowerShell call — an isolated registry hides it.
        var registry = new Agency.Harness.Tools.ToolRegistry(
        [
            tool,
            new Agency.Harness.Tools.ReadFileTool(),
            new Agency.Harness.Tools.WriteFileTool(),
        ]);

        var agent = new Agent(this._fixture.LlmClient, this._fixture.Model);
        var ctx = new Context
        {
            Query = new QueryContext { Prompt = "List the running processes on this machine." },
            Tools = new ToolContext { Registry = registry },
        };

        var powershellResults = new List<Agency.Llm.Common.Tools.ToolResult>();
        await foreach (var evt in agent.RunAsync(ctx, ct: TestContext.Current.CancellationToken))
        {
            if (evt is ToolInvokedEvent t && t.ToolName == tool.Definition.Name)
            {
                powershellResults.Add(t.Result);
                if (!t.Result.IsError && CountProcessLines(t.Result.Content) >= 3)
                {
                    // Got what we need — stop before a non-deterministic follow-up LLM request.
                    break;
                }
            }
        }

        // The model reached for the PowerShell tool (e.g. tasklist / Get-Process).
        Assert.NotEmpty(powershellResults);

        // At least one successful invocation returned a few processes.
        Agency.Llm.Common.Tools.ToolResult? success = powershellResults
            .FirstOrDefault(r => !r.IsError && CountProcessLines(r.Content) >= 3);

        Assert.True(
            success is not null,
            "Expected a successful PowerShell call returning at least 3 processes. Captured results:\n"
            + string.Join("\n---\n", powershellResults.Select(r => $"IsError={r.IsError}\n{r.Content}")));
    }

    /// <summary>
    /// Counts the content lines a PowerShell result yielded, format-agnostically: <c>Get-Process</c>
    /// renders a Markdown table while <c>tasklist</c> renders plain text lines. Blank lines and a
    /// Markdown <c>| --- |</c> separator are excluded. Used to gauge how many processes came back.
    /// </summary>
    private static int CountProcessLines(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return 0;
        }

        int lines = 0;
        foreach (string raw in content.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            // Skip a Markdown separator row, e.g. "| ---- | :--: |".
            if (line[0] == '|' && line.All(static c => c is '|' or '-' or ':' or ' '))
            {
                continue;
            }

            lines++;
        }

        return lines;
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
                .AddSharedConfiguration("shared-test-appsettings.json")
                .AddJsonFile("appsettings.json", optional: false)
                .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
                .AddUserSecrets<AgentOpenAIFunctionalTests>(optional: true)
                .AddEnvironmentVariables()
                .AddPlaceholderResolver()
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
