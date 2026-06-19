using Agency.Harness.Skills;

namespace Agency.Harness.Test.Skills;

/// <summary>
/// Tests for <see cref="SkillRenderer.ExpandShellAsync"/> and the shell-expansion path in <see cref="SkillTool"/>.
/// All tests use a fake <see cref="ISkillShellRunner"/> — no real shell is invoked.
/// </summary>
public sealed class SkillShellExpansionTests
{
    // ---------------------------------------------------------------------------
    // Fake runner — records invocations, returns canned output
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Test double for <see cref="ISkillShellRunner"/> that records every command it was asked to run
    /// and returns a configurable response.
    /// </summary>
    private sealed class FakeShellRunner : ISkillShellRunner
    {
        private readonly Func<string, string> _respond;

        /// <summary>Initialises the fake with a per-command response function.</summary>
        /// <param name="respond">Maps a command string to the output it should produce.</param>
        internal FakeShellRunner(Func<string, string> respond)
        {
            this._respond = respond;
        }

        /// <summary>Commands that were passed to <see cref="RunAsync"/>.</summary>
        internal List<string> Invocations { get; } = [];

        /// <inheritdoc/>
        public Task<string> RunAsync(string command, CancellationToken ct = default)
        {
            this.Invocations.Add(command);
            return Task.FromResult(this._respond(command));
        }
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static Skill MakeSkill(string body, string? shell = null) =>
        new()
        {
            Name = "test-skill",
            Description = "A test skill",
            Body = body,
            SkillDir = "/skills/test-skill",
            Shell = shell,
        };

    // ---------------------------------------------------------------------------
    // Inline !`cmd` expansion
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ExpandShellAsync_InlineDirective_IsReplacedWithRunnerOutput()
    {
        FakeShellRunner runner = new(cmd => cmd == "Get-Date" ? "2026-06-18" : "unexpected");
        string input = "Today is !`Get-Date`.";

        string result = await SkillRenderer.ExpandShellAsync(input, runner, disabled: false);

        Assert.Equal("Today is 2026-06-18.", result);
        Assert.Single(runner.Invocations);
        Assert.Equal("Get-Date", runner.Invocations[0]);
    }

    [Fact]
    public async Task ExpandShellAsync_MultipleInlineDirectives_AllExpanded()
    {
        FakeShellRunner runner = new(cmd => cmd switch
        {
            "Get-Date" => "2026-06-18",
            "hostname" => "my-machine",
            _ => "?",
        });
        string input = "Date: !`Get-Date`, Host: !`hostname`.";

        string result = await SkillRenderer.ExpandShellAsync(input, runner, disabled: false);

        Assert.Equal("Date: 2026-06-18, Host: my-machine.", result);
        Assert.Equal(2, runner.Invocations.Count);
    }

    // ---------------------------------------------------------------------------
    // Fenced ```! block expansion
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ExpandShellAsync_FencedBlock_IsReplacedWithRunnerOutput()
    {
        FakeShellRunner runner = new(static _ => "line1\nline2");
        string input = "Before\n```!\ndir /b\n```\nAfter";

        string result = await SkillRenderer.ExpandShellAsync(input, runner, disabled: false);

        Assert.Equal("Before\nline1\nline2\nAfter", result);
        Assert.Single(runner.Invocations);
        Assert.Equal("dir /b", runner.Invocations[0]);
    }

    [Fact]
    public async Task ExpandShellAsync_FencedBlockAndInline_BothExpanded()
    {
        FakeShellRunner runner = new(cmd => cmd switch
        {
            "ls" => "file1.txt",
            "pwd" => "/home/user",
            _ => "?",
        });
        string input = "Files:\n```!\nls\n```\nDir: !`pwd`";

        string result = await SkillRenderer.ExpandShellAsync(input, runner, disabled: false);

        Assert.Contains("file1.txt", result);
        Assert.Contains("/home/user", result);
        Assert.Equal(2, runner.Invocations.Count);
    }

    // ---------------------------------------------------------------------------
    // One-pass only — substituted output is NOT re-scanned
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ExpandShellAsync_OutputContainingShellDirective_NotReExpanded()
    {
        // The runner returns a string that looks like a shell directive.
        // It must NOT be re-expanded.
        FakeShellRunner runner = new(static _ => "!`dangerous-command`");
        string input = "Result: !`safe-command`";

        string result = await SkillRenderer.ExpandShellAsync(input, runner, disabled: false);

        // The output of safe-command (which itself looks like a directive) must appear literally.
        Assert.Equal("Result: !`dangerous-command`", result);
        // Only the original directive was run — not the one in the output.
        Assert.Single(runner.Invocations);
        Assert.Equal("safe-command", runner.Invocations[0]);
    }

    // ---------------------------------------------------------------------------
    // Disabled gate — runner never called when disabled
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ExpandShellAsync_WhenDisabled_DirectivesLeftIntact_RunnerNeverCalled()
    {
        FakeShellRunner runner = new(static _ => "should-not-be-called");
        string input = "Value: !`Get-Date`";

        string result = await SkillRenderer.ExpandShellAsync(input, runner, disabled: true);

        // Directive left verbatim.
        Assert.Equal("Value: !`Get-Date`", result);
        // Runner was never invoked.
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task ExpandShellAsync_WhenRunnerIsNull_DirectivesLeftIntact()
    {
        string input = "Value: !`Get-Date`";

        string result = await SkillRenderer.ExpandShellAsync(input, runner: null, disabled: false);

        Assert.Equal("Value: !`Get-Date`", result);
    }

    // ---------------------------------------------------------------------------
    // Shell field on Skill — confirmed accessible (parser tests cover parsing;
    // here we verify the Skill record carries it through to SkillTool invocation)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SkillTool_WithShellRunner_ExpandsInlineDirective()
    {
        FakeShellRunner runner = new(static _ => "expanded-output");
        Skill skill = MakeSkill("Value: !`my-cmd`", shell: "powershell");
        SkillCatalog catalog = new([skill]);

        SkillTool tool = new(catalog, sessionId: "s1", shellRunner: runner, disableShellExecution: false);

        Agency.Llm.Common.Tools.ToolResult result = await tool.InvokeAsync(
            System.Text.Json.JsonSerializer.SerializeToElement(new { name = "test-skill" }),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("Value: expanded-output", result.Content);
        Assert.Single(runner.Invocations);
    }

    [Fact]
    public async Task SkillTool_WithDisableShellExecution_SkipsExpansion()
    {
        FakeShellRunner runner = new(static _ => "should-not-appear");
        Skill skill = MakeSkill("Value: !`my-cmd`");
        SkillCatalog catalog = new([skill]);

        SkillTool tool = new(catalog, sessionId: "s1", shellRunner: runner, disableShellExecution: true);

        Agency.Llm.Common.Tools.ToolResult result = await tool.InvokeAsync(
            System.Text.Json.JsonSerializer.SerializeToElement(new { name = "test-skill" }),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("Value: !`my-cmd`", result.Content);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task SkillTool_WithNoRunner_BehavesLikePhase1()
    {
        // No runner — must behave exactly like Phase-1 (pure render only).
        Skill skill = MakeSkill("Hello, $ARGUMENTS!");
        SkillCatalog catalog = new([skill]);

        SkillTool tool = new(catalog, sessionId: "s1");

        Agency.Llm.Common.Tools.ToolResult result = await tool.InvokeAsync(
            System.Text.Json.JsonSerializer.SerializeToElement(new { name = "test-skill", arguments = "world" }),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("Hello, world!", result.Content);
    }

    // ---------------------------------------------------------------------------
    // Skill.Shell field — parser integration (field exists on the record)
    // ---------------------------------------------------------------------------

    [Fact]
    public void Skill_ShellField_DefaultsToNull()
    {
        Skill skill = MakeSkill("body");

        Assert.Null(skill.Shell);
    }

    [Fact]
    public void Skill_ShellField_CanBeSetToPowershell()
    {
        Skill skill = MakeSkill("body", shell: "powershell");

        Assert.Equal("powershell", skill.Shell);
    }

    [Fact]
    public void SkillParser_ShellField_ParsedFromFrontmatter()
    {
        string text = """
            ---
            description: My skill
            shell: powershell
            ---
            Skill body here.
            """;

        Skill skill = SkillParser.Parse(text, "/skills/my-skill", "my-skill");

        Assert.Equal("powershell", skill.Shell);
    }

    [Fact]
    public void SkillParser_ShellField_AbsentWhenNotInFrontmatter()
    {
        string text = """
            ---
            description: My skill
            ---
            Skill body here.
            """;

        Skill skill = SkillParser.Parse(text, "/skills/my-skill", "my-skill");

        Assert.Null(skill.Shell);
    }
}
