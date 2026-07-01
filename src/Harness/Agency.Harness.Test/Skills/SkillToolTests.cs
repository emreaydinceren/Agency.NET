using System.Text.Json;
using Agency.Harness.Skills;

namespace Agency.Harness.Test.Skills;

/// <summary>
/// Tests for <see cref="SkillTool"/>.
/// </summary>
public sealed class SkillToolTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static Skill MakeSkill(
        string name,
        string body = "Skill body.",
        bool disableModelInvocation = false,
        IReadOnlyList<string>? arguments = null,
        string? context = null,
        string? agent = null) =>
        new()
        {
            Name = name,
            Description = "A test skill",
            Body = body,
            SkillDir = $"/skills/{name}",
            DisableModelInvocation = disableModelInvocation,
            Arguments = arguments ?? [],
            Context = context,
            Agent = agent,
        };

    private static SkillCatalog BuildCatalog(params Skill[] skills) => new(skills);

    private static SkillTool BuildTool(ISkillCatalog catalog, string sessionId = "session-abc") =>
        new(catalog, sessionId);

    /// <summary>
    /// A fake <see cref="SkillForkRunner"/> that records the last invocation and returns canned output.
    /// </summary>
    private sealed class FakeForkRunner
    {
        public string? LastPrompt { get; private set; }
        public string? LastAgentType { get; private set; }
        public int CallCount { get; private set; }

        public string CannedResult { get; set; } = "fork-result";

        public Task<string> RunAsync(string prompt, string? agentType, CancellationToken _)
        {
            this.LastPrompt = prompt;
            this.LastAgentType = agentType;
            this.CallCount++;
            return Task.FromResult(this.CannedResult);
        }
    }

    // ---------------------------------------------------------------------------
    // Definition
    // ---------------------------------------------------------------------------

    /// <summary>The tool's <c>Definition</c> exposes the name <c>skill</c>, a description mentioning skills, and an input schema requiring a string <c>name</c> with an optional string <c>arguments</c>.</summary>
    [Fact]
    public void Definition_HasExpectedNameAndSchema()
    {
        SkillTool tool = BuildTool(SkillCatalog.Empty);

        Assert.Equal("skill", tool.Definition.Name);
        Assert.Contains("skill", tool.Definition.Description);

        JsonElement schema = tool.Definition.InputSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal("string", schema.GetProperty("properties").GetProperty("name").GetProperty("type").GetString());
        Assert.Equal("string", schema.GetProperty("properties").GetProperty("arguments").GetProperty("type").GetString());
        Assert.Equal("name", schema.GetProperty("required")[0].GetString());
    }

    // ---------------------------------------------------------------------------
    // Happy path — arguments forwarded to renderer
    // ---------------------------------------------------------------------------

    /// <summary>Invoking with a known skill name and arguments returns the rendered body with the arguments substituted.</summary>
    [Fact]
    public async Task InvokeAsync_KnownSkill_ReturnsRenderedBody()
    {
        Skill skill = MakeSkill("greet", body: "Hello, $ARGUMENTS!");
        SkillTool tool = BuildTool(BuildCatalog(skill));

        ToolResult result = await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { name = "greet", arguments = "world" }),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("Hello, world!", result.Content);
    }

    /// <summary>Invoking with a known skill name and no arguments returns the rendered body unchanged.</summary>
    [Fact]
    public async Task InvokeAsync_KnownSkill_WithoutArguments_ReturnsRenderedBody()
    {
        Skill skill = MakeSkill("simple", body: "Just do it.");
        SkillTool tool = BuildTool(BuildCatalog(skill));

        ToolResult result = await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { name = "simple" }),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("Just do it.", result.Content);
    }

    /// <summary>Invoking a skill substitutes the tool's configured session id into the rendered body.</summary>
    [Fact]
    public async Task InvokeAsync_KnownSkill_SessionIdSubstituted()
    {
        Skill skill = MakeSkill("session-skill", body: "Session: ${CLAUDE_SESSION_ID}");
        SkillTool tool = BuildTool(BuildCatalog(skill), sessionId: "test-session-42");

        ToolResult result = await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { name = "session-skill" }),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("Session: test-session-42", result.Content);
    }

    // ---------------------------------------------------------------------------
    // Unknown name → error listing available skills
    // ---------------------------------------------------------------------------

    /// <summary>Invoking with an unknown skill name returns an error result whose content lists the requested name and every available skill name.</summary>
    [Fact]
    public async Task InvokeAsync_UnknownSkill_ReturnsErrorWithAvailableNames()
    {
        Skill alpha = MakeSkill("alpha");
        Skill beta = MakeSkill("beta");
        SkillTool tool = BuildTool(BuildCatalog(alpha, beta));

        ToolResult result = await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { name = "no-such-skill" }),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("no-such-skill", result.Content);
        Assert.Contains("alpha", result.Content);
        Assert.Contains("beta", result.Content);
    }

    /// <summary>The available-skills list in an unknown-skill error excludes skills with <see cref="Skill.DisableModelInvocation"/> set.</summary>
    [Fact]
    public async Task InvokeAsync_UnknownSkill_DisabledSkillsExcludedFromAvailableList()
    {
        Skill visible = MakeSkill("visible");
        Skill hidden = MakeSkill("hidden", disableModelInvocation: true);
        SkillTool tool = BuildTool(BuildCatalog(visible, hidden));

        ToolResult result = await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { name = "no-such-skill" }),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("visible", result.Content);
        Assert.DoesNotContain("hidden", result.Content);
    }

    // ---------------------------------------------------------------------------
    // DisableModelInvocation → refused
    // ---------------------------------------------------------------------------

    /// <summary>Invoking a skill with <see cref="Skill.DisableModelInvocation"/> set returns an error result mentioning the skill's name.</summary>
    [Fact]
    public async Task InvokeAsync_DisabledSkill_ReturnsError()
    {
        Skill skill = MakeSkill("restricted", disableModelInvocation: true);
        SkillTool tool = BuildTool(BuildCatalog(skill));

        ToolResult result = await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { name = "restricted" }),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("restricted", result.Content);
    }

    // ---------------------------------------------------------------------------
    // Missing / empty name parameter
    // ---------------------------------------------------------------------------

    /// <summary>Invoking with a JSON payload that omits the <c>name</c> property returns an error stating that <c>name</c> is required.</summary>
    [Fact]
    public async Task InvokeAsync_MissingNameProperty_ReturnsError()
    {
        SkillTool tool = BuildTool(SkillCatalog.Empty);

        ToolResult result = await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { }),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("'name' is required", result.Content);
    }

    /// <summary>Invoking with an empty <c>name</c> string returns an error stating that <c>name</c> is required.</summary>
    [Fact]
    public async Task InvokeAsync_EmptyNameString_ReturnsError()
    {
        SkillTool tool = BuildTool(SkillCatalog.Empty);

        ToolResult result = await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { name = "" }),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("'name' is required", result.Content);
    }

    // ---------------------------------------------------------------------------
    // context: fork — subagent delegation
    // ---------------------------------------------------------------------------

    /// <summary>
    /// When a skill declares <c>context: fork</c> and a fork runner is wired, the rendered
    /// body is passed as the prompt to the runner and the runner's output is returned.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ForkSkill_WithRunner_CallsRunnerWithRenderedBody()
    {
        var fakeRunner = new FakeForkRunner { CannedResult = "subagent output" };
        Skill skill = MakeSkill("fork-skill", body: "Do the thing with $ARGUMENTS.", context: "fork");
        SkillCatalog catalog = BuildCatalog(skill);
        var tool = new SkillTool(catalog, sessionId: "sid", forkRunner: fakeRunner.RunAsync);

        ToolResult result = await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { name = "fork-skill", arguments = "alpha" }),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("subagent output", result.Content);
        Assert.Equal("Do the thing with alpha.", fakeRunner.LastPrompt);
        Assert.Equal(1, fakeRunner.CallCount);
    }

    /// <summary>
    /// The <c>agent</c> frontmatter field is forwarded to the fork runner as the agent type.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ForkSkill_WithAgentType_ForwardsAgentTypeToRunner()
    {
        var fakeRunner = new FakeForkRunner { CannedResult = "typed-agent output" };
        Skill skill = MakeSkill("typed-fork", body: "Typed task.", context: "fork", agent: "code-reviewer");
        SkillCatalog catalog = BuildCatalog(skill);
        var tool = new SkillTool(catalog, forkRunner: fakeRunner.RunAsync);

        ToolResult result = await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { name = "typed-fork" }),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("typed-agent output", result.Content);
        Assert.Equal("code-reviewer", fakeRunner.LastAgentType);
    }

    /// <summary>
    /// When <c>agent</c> is absent, <see langword="null"/> is forwarded as the agent type.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ForkSkill_NoAgentType_ForwardsNullAgentType()
    {
        var fakeRunner = new FakeForkRunner();
        Skill skill = MakeSkill("fork-no-agent", body: "Task.", context: "fork");
        SkillCatalog catalog = BuildCatalog(skill);
        var tool = new SkillTool(catalog, forkRunner: fakeRunner.RunAsync);

        await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { name = "fork-no-agent" }),
            CancellationToken.None);

        Assert.Null(fakeRunner.LastAgentType);
    }

    /// <summary>
    /// A non-fork skill does not invoke the fork runner — the rendered body is returned inline.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_NonForkSkill_RunnerNotCalled_BodyReturnedInline()
    {
        var fakeRunner = new FakeForkRunner { CannedResult = "should not appear" };
        Skill skill = MakeSkill("inline-skill", body: "Inline body.");
        SkillCatalog catalog = BuildCatalog(skill);
        var tool = new SkillTool(catalog, forkRunner: fakeRunner.RunAsync);

        ToolResult result = await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { name = "inline-skill" }),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("Inline body.", result.Content);
        Assert.Equal(0, fakeRunner.CallCount);
    }

    /// <summary>
    /// When <c>context: fork</c> is declared but no fork runner is wired, the rendered body
    /// is returned inline as a safe fallback (no exception).
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ForkSkill_NoRunnerWired_FallsBackToInlineBody()
    {
        Skill skill = MakeSkill("fork-no-runner", body: "Fork body.", context: "fork");
        SkillCatalog catalog = BuildCatalog(skill);
        SkillTool tool = BuildTool(catalog); // no forkRunner supplied

        ToolResult result = await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { name = "fork-no-runner" }),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("Fork body.", result.Content);
    }

    /// <summary>
    /// The <c>context</c> comparison is case-insensitive — <c>Fork</c> and <c>FORK</c> both trigger delegation.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ForkSkill_ContextCaseInsensitive_DelegatesRunner()
    {
        var fakeRunner = new FakeForkRunner { CannedResult = "case ok" };
        Skill skill = MakeSkill("fork-case", body: "Case body.", context: "FORK");
        SkillCatalog catalog = BuildCatalog(skill);
        var tool = new SkillTool(catalog, forkRunner: fakeRunner.RunAsync);

        ToolResult result = await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { name = "fork-case" }),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("case ok", result.Content);
        Assert.Equal(1, fakeRunner.CallCount);
    }
}
