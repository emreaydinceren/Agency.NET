using Agency.Harness.Console.Commands;
using Agency.Harness.Contexts;
using Agency.Harness.Tools;
using Agency.Llm.Common.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Moq;
using Spectre.Console;
using System.Text.Json;

namespace Agency.Harness.Console.Test.Commands;

/// <summary>
/// Unit tests for <see cref="McpCommand"/>'s pure helpers (<see cref="McpCommand.BuildStatuses"/>,
/// <see cref="McpCommand.ApplyToggle"/>, <see cref="McpCommand.ResolveServer"/>) and for
/// <see cref="McpCommand.ListAsync"/>/<see cref="McpCommand.ToggleAsync"/>'s graceful degradation when
/// no <see cref="McpClientPool"/> is registered. <see cref="McpClientPool"/> has a private constructor
/// and its <c>CreateAsync</c> requires live MCP server subprocesses, so it can never be constructed in a
/// test — the helpers exist precisely so the logic is testable against plain dictionaries instead.
/// </summary>
// See ProjectsCommandTests for why this class shares the "AnsiConsoleTests" collection.
[Collection("AnsiConsoleTests")]
public sealed class McpCommandTests
{
    // ---------------------------------------------------------------------------
    // Minimal test doubles (mirrors ProjectsCommandTests' private doubles)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// A trivial <see cref="IServiceProvider"/> resolving from a fixed set of pre-built instances.
    /// Passing no services means <c>GetService(typeof(McpClientPool))</c> returns <see langword="null"/> —
    /// the "MCP unconfigured/under Test" scenario <see cref="McpCommand.ListAsync"/> and
    /// <see cref="McpCommand.ToggleAsync"/> must degrade gracefully against.
    /// </summary>
    private sealed class FakeServiceProvider(params (Type Type, object Instance)[] services) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            services.FirstOrDefault(s => s.Type == serviceType).Instance;
    }

    /// <summary>
    /// A minimal named <see cref="ITool"/> stub for seeding a real <see cref="ToolRegistry"/> — only the
    /// name matters to <see cref="McpCommand.BuildStatuses"/> and <see cref="McpCommand.ApplyToggle"/>,
    /// which never invoke it.
    /// </summary>
    private sealed class StubTool(string name) : ITool
    {
        public ToolDefinition Definition { get; } = new(name, $"Stub tool: {name}", default);

        public Task<ToolResult> InvokeAsync(JsonElement input, CancellationToken ct) =>
            Task.FromResult(new ToolResult("ok"));
    }

    /// <summary>No-op <see cref="IChatOutput"/> so <c>AnsiConsole.MarkupLine</c> calls inside the command
    /// bodies don't need a real console output sink to be observed.</summary>
    private sealed class NoOpChatOutput : IChatOutput
    {
        public void WriteLine() { }
        public void WriteLine(string? colorName, string text) { }
        public void WriteLine(string text) { }
        public void Write(string? colorName, string text) { }
        public void Write(string text) { }
        public void WriteLineMarkdown(string text) { }
        public void WriteMarkup(string text) { }
        public void WriteLineMarkup(string text) { }
        public void StartSpinner(string markup = "[yellow]Thinking...[/]") { }
        public void StopSpinner() { }
        public void WriteMarkdownInBorderedPanel(string header, string text) { }
    }

    /// <summary>
    /// Builds a real <see cref="ConsoleChatSession"/> — <see cref="McpCommand.ListAsync"/>/
    /// <see cref="McpCommand.ToggleAsync"/> only ever touch its <c>ServiceProvider</c> and
    /// <c>Tools.Registry</c>, but the parameter type is the concrete session class, not an interface, so
    /// a minimal-but-real instance is constructed rather than faked. The <see cref="IChatClient"/> behind
    /// <see cref="Agent"/> is never invoked because these tests never start a turn.
    /// </summary>
    /// <param name="serviceProvider">The fake service provider to resolve <see cref="McpClientPool"/> from.</param>
    private static ConsoleChatSession CreateSession(IServiceProvider serviceProvider)
    {
        var chatClientMock = new Mock<IChatClient>();
        var agent = new Agent(chatClientMock.Object, "test-model");

        return new ConsoleChatSession(
            serviceProvider,
            agent,
            Options.Create(new AgentOptions()),
            ToolContext.Empty,
            new NoOpChatOutput(),
            SkillContext.Empty);
    }

    /// <summary>
    /// Runs <paramref name="action"/> with the static <see cref="AnsiConsole.Console"/> swapped for a
    /// string-backed instance, so plain text written via <c>AnsiConsole.MarkupLine</c> can be asserted on
    /// directly without Spectre output polluting the test console.
    /// </summary>
    private static async Task<string> RunCapturingOutputAsync(Func<Task> action)
    {
        var writer = new StringWriter();
        IAnsiConsole captured = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });
        captured.Profile.Width = 200;

        IAnsiConsole original = AnsiConsole.Console;
        AnsiConsole.Console = captured;
        try
        {
            await action();
        }
        finally
        {
            AnsiConsole.Console = original;
        }

        return writer.ToString().TrimEnd('\r', '\n');
    }

    // ---------------------------------------------------------------------------
    // BuildStatuses
    // ---------------------------------------------------------------------------

    /// <summary>
    /// A server whose tools are all enabled reports the full count and <c>On == true</c>.
    /// </summary>
    [Fact]
    public void BuildStatuses_AllEnabledServer_ReportsFullCount_AndOn()
    {
        var registry = new ToolRegistry([new StubTool("tool_a"), new StubTool("tool_b")]);
        var toolNamesByServer = new Dictionary<string, IReadOnlyList<string>>
        {
            ["alpha"] = new List<string> { "tool_a", "tool_b" },
        };

        IReadOnlyList<McpCommand.ServerStatus> statuses =
            McpCommand.BuildStatuses(toolNamesByServer, new Dictionary<string, string>(), [], registry);

        McpCommand.ServerStatus status = Assert.Single(statuses);
        Assert.Equal("alpha", status.Name);
        Assert.Equal(2, status.EnabledTools);
        Assert.Equal(2, status.TotalTools);
        Assert.True(status.On);
        Assert.False(status.Failed);
    }

    /// <summary>
    /// A server whose tools are all user-disabled reports zero enabled and <c>On == false</c>.
    /// </summary>
    [Fact]
    public void BuildStatuses_FullyDisabledServer_ReportsZeroEnabled_AndOff()
    {
        var registry = new ToolRegistry([new StubTool("tool_a"), new StubTool("tool_b")]);
        registry.DisableToolByUser("tool_a");
        registry.DisableToolByUser("tool_b");
        var toolNamesByServer = new Dictionary<string, IReadOnlyList<string>>
        {
            ["alpha"] = new List<string> { "tool_a", "tool_b" },
        };

        IReadOnlyList<McpCommand.ServerStatus> statuses =
            McpCommand.BuildStatuses(toolNamesByServer, new Dictionary<string, string>(), [], registry);

        McpCommand.ServerStatus status = Assert.Single(statuses);
        Assert.Equal(0, status.EnabledTools);
        Assert.Equal(2, status.TotalTools);
        Assert.False(status.On);
    }

    /// <summary>
    /// A server with only some of its tools user-disabled reports the partial count while still
    /// counting as <c>On == true</c> — surfacing the partial state without a fourth status.
    /// </summary>
    [Fact]
    public void BuildStatuses_PartiallyDisabledServer_ReportsPartialCount_AndOn()
    {
        var registry = new ToolRegistry([new StubTool("tool_a"), new StubTool("tool_b")]);
        registry.DisableToolByUser("tool_a");
        var toolNamesByServer = new Dictionary<string, IReadOnlyList<string>>
        {
            ["alpha"] = new List<string> { "tool_a", "tool_b" },
        };

        IReadOnlyList<McpCommand.ServerStatus> statuses =
            McpCommand.BuildStatuses(toolNamesByServer, new Dictionary<string, string>(), [], registry);

        McpCommand.ServerStatus status = Assert.Single(statuses);
        Assert.Equal(1, status.EnabledTools);
        Assert.Equal(2, status.TotalTools);
        Assert.True(status.On);
    }

    /// <summary>
    /// A server that failed to connect contributes no tools, so it is absent from
    /// <c>toolNamesByServer</c> — this is the case that regresses most easily, since a naive
    /// implementation that only iterates <c>toolNamesByServer</c> would silently drop it from the
    /// listing entirely instead of surfacing the failure.
    /// </summary>
    [Fact]
    public void BuildStatuses_FailedServer_AppearsWithError_DespiteAbsenceFromToolNamesByServer()
    {
        var registry = new ToolRegistry();
        var failedServers = new Dictionary<string, string> { ["beta"] = "Connection refused" };

        IReadOnlyList<McpCommand.ServerStatus> statuses =
            McpCommand.BuildStatuses(new Dictionary<string, IReadOnlyList<string>>(), failedServers, [], registry);

        McpCommand.ServerStatus status = Assert.Single(statuses);
        Assert.Equal("beta", status.Name);
        Assert.True(status.Failed);
        Assert.False(status.Disabled);
        Assert.Equal("Connection refused", status.Error);
        Assert.Equal(0, status.EnabledTools);
        Assert.Equal(0, status.TotalTools);
    }

    /// <summary>
    /// A server disabled in config is never attempted, so it appears in neither
    /// <c>toolNamesByServer</c> nor <c>failedServers</c> — only in <c>disabledServers</c>. It is
    /// reported as connectionless with no error, distinguishing it from a failed connection.
    /// </summary>
    [Fact]
    public void BuildStatuses_DisabledServer_AppearsAsDisabled_WithNoError()
    {
        var registry = new ToolRegistry();

        IReadOnlyList<McpCommand.ServerStatus> statuses = McpCommand.BuildStatuses(
            new Dictionary<string, IReadOnlyList<string>>(),
            new Dictionary<string, string>(),
            ["delta"],
            registry);

        McpCommand.ServerStatus status = Assert.Single(statuses);
        Assert.Equal("delta", status.Name);
        Assert.False(status.Connected);
        Assert.Null(status.Error);
        Assert.True(status.Disabled);
        Assert.False(status.Failed);
        Assert.False(status.On);
        Assert.Equal(0, status.EnabledTools);
        Assert.Equal(0, status.TotalTools);
    }

    /// <summary>
    /// A server present in both dictionaries (a stale failure entry alongside a live connection) is
    /// reported once, from <c>toolNamesByServer</c> — the connected entry wins and the stale error is
    /// dropped, rather than the server appearing twice or as failed.
    /// </summary>
    [Fact]
    public void BuildStatuses_ServerInBothDictionaries_ReportsConnectedEntry_NotFailed()
    {
        var registry = new ToolRegistry([new StubTool("tool_a")]);
        var toolNamesByServer = new Dictionary<string, IReadOnlyList<string>>
        {
            ["gamma"] = new List<string> { "tool_a" },
        };
        var failedServers = new Dictionary<string, string> { ["gamma"] = "stale error" };

        IReadOnlyList<McpCommand.ServerStatus> statuses =
            McpCommand.BuildStatuses(toolNamesByServer, failedServers, [], registry);

        McpCommand.ServerStatus status = Assert.Single(statuses);
        Assert.Equal("gamma", status.Name);
        Assert.False(status.Failed);
        Assert.Equal(1, status.TotalTools);
    }

    /// <summary>
    /// Statuses are built in <c>toolNamesByServer</c>'s configured order, then failed-only servers,
    /// then disabled servers — so an operator scanning the listing sees live servers before the ones
    /// that need attention (a stuck failure) before the ones that were deliberately turned off.
    /// </summary>
    [Fact]
    public void BuildStatuses_PreservesConfiguredOrder_ThenFailedOnly_ThenDisabled()
    {
        var registry = new ToolRegistry();
        var toolNamesByServer = new Dictionary<string, IReadOnlyList<string>>
        {
            ["zeta"] = new List<string>(),
            ["alpha"] = new List<string>(),
        };
        var failedServers = new Dictionary<string, string> { ["omega"] = "boom" };
        List<string> disabledServers = ["theta"];

        IReadOnlyList<McpCommand.ServerStatus> statuses =
            McpCommand.BuildStatuses(toolNamesByServer, failedServers, disabledServers, registry);

        List<string> expectedOrder = ["zeta", "alpha", "omega", "theta"];
        Assert.Equal(expectedOrder, statuses.Select(s => s.Name));
    }

    // ---------------------------------------------------------------------------
    // ResolveServer
    // ---------------------------------------------------------------------------

    /// <summary>
    /// A case-insensitive match returns the canonical spelling as stored in <c>knownServers</c>, not
    /// the operator's typed casing.
    /// </summary>
    [Fact]
    public void ResolveServer_CaseInsensitiveMatch_ReturnsCanonicalStoredName()
    {
        string? resolved = McpCommand.ResolveServer("GitHub", ["memory", "github"]);

        Assert.Equal("github", resolved);
    }

    /// <summary>
    /// A name matching nothing in <c>knownServers</c> resolves to <see langword="null"/>.
    /// </summary>
    [Fact]
    public void ResolveServer_UnknownName_ReturnsNull()
    {
        string? resolved = McpCommand.ResolveServer("nosuch", ["memory", "github"]);

        Assert.Null(resolved);
    }

    /// <summary>
    /// A server disabled in config is resolvable by name from <see cref="McpCommand.BuildStatuses"/>'s
    /// full name list — this is what lets an operator re-enable it via <c>/mcp-toggle</c>, since a
    /// disabled server contributes to neither <c>toolNamesByServer</c> nor <c>failedServers</c>.
    /// </summary>
    [Fact]
    public void ResolveServer_DisabledServerName_ResolvesFromBuildStatusesOutput()
    {
        var registry = new ToolRegistry();
        IReadOnlyList<McpCommand.ServerStatus> statuses = McpCommand.BuildStatuses(
            new Dictionary<string, IReadOnlyList<string>>(),
            new Dictionary<string, string>(),
            ["github"],
            registry);

        string? resolved = McpCommand.ResolveServer("GitHub", statuses.Select(status => status.Name));

        Assert.Equal("github", resolved);
    }

    // ---------------------------------------------------------------------------
    // ApplyToggle
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Disabling a set of tool names then re-enabling the same set round-trips them back into
    /// <see cref="IToolRegistry.ListDefinitions"/>.
    /// </summary>
    [Fact]
    public void ApplyToggle_DisableThenEnable_RoundTrips()
    {
        var registry = new ToolRegistry([new StubTool("tool_a"), new StubTool("tool_b")]);
        IReadOnlyList<string> toolNames = ["tool_a", "tool_b"];

        McpCommand.ApplyToggle(registry, toolNames, enable: false);
        Assert.DoesNotContain(registry.ListDefinitions(), d => d.Name == "tool_a");
        Assert.DoesNotContain(registry.ListDefinitions(), d => d.Name == "tool_b");

        McpCommand.ApplyToggle(registry, toolNames, enable: true);
        Assert.Contains(registry.ListDefinitions(), d => d.Name == "tool_a");
        Assert.Contains(registry.ListDefinitions(), d => d.Name == "tool_b");
    }

    // ---------------------------------------------------------------------------
    // Argument extraction — mirrors ProjectsCommand_ArgumentExtraction_UsesRegisteredCommandLength
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Pins <c>/mcp-toggle</c>'s argument-slicing formula (<c>input[ToggleCommandText.Length..].Trim()</c>)
    /// against the registered command text constant itself, exactly as
    /// <c>ProjectsCommand_ArgumentExtraction_UsesRegisteredCommandLength</c> pins
    /// <c>ProjectsCommand</c>'s literals — that test exists because a hard-coded command-text length
    /// literal once drifted from the registered command name. <see cref="McpCommand.ToggleAsync"/> cannot
    /// be driven end-to-end here (it short-circuits on the null <see cref="McpClientPool"/> before ever
    /// reaching the slice), so the formula is exercised directly using the same constant the production
    /// code slices with. Includes both the space-delimited form an operator actually types and a
    /// no-space form: only the no-space form would expose an off-by-one in the slice length.
    /// </summary>
    [Theory]
    [InlineData("/mcp-toggle github", "github")]
    [InlineData("/mcp-togglegithub", "github")]
    public void ToggleCommandText_ArgumentExtraction_UsesRegisteredCommandLength(string input, string expected)
    {
        string extracted = input[McpCommand.ToggleCommandText.Length..].Trim();

        Assert.Equal(expected, extracted);
    }

    // ---------------------------------------------------------------------------
    // Graceful degradation with no McpClientPool registered
    // ---------------------------------------------------------------------------

    /// <summary>
    /// <see cref="McpCommand.ListAsync"/> prints a friendly message and returns
    /// <see cref="CommandContinuation.Continue"/> without throwing when no <see cref="McpClientPool"/>
    /// is registered in the service provider (MCP unconfigured, or under the <c>Test</c> environment).
    /// </summary>
    [Fact]
    public async Task ListAsync_NoPoolRegistered_PrintsMessage_AndDoesNotThrow()
    {
        ConsoleChatSession session = CreateSession(new FakeServiceProvider());

        CommandContinuation? continuation = null;
        string output = await RunCapturingOutputAsync(async () => continuation = await McpCommand.ListAsync(session));

        Assert.Equal("No MCP servers configured.", output);
        Assert.Equal(CommandContinuation.Continue, continuation);
    }

    /// <summary>
    /// <see cref="McpCommand.ToggleAsync"/> prints a friendly message and returns
    /// <see cref="CommandContinuation.Continue"/> without throwing when no <see cref="McpClientPool"/>
    /// is registered in the service provider.
    /// </summary>
    [Fact]
    public async Task ToggleAsync_NoPoolRegistered_PrintsMessage_AndDoesNotThrow()
    {
        ConsoleChatSession session = CreateSession(new FakeServiceProvider());

        CommandContinuation? continuation = null;
        string output = await RunCapturingOutputAsync(
            async () => continuation = await McpCommand.ToggleAsync("/mcp-toggle github", session));

        Assert.Equal("No MCP servers configured.", output);
        Assert.Equal(CommandContinuation.Continue, continuation);
    }
}
