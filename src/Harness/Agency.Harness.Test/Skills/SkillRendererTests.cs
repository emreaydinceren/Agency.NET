using Agency.Harness.Skills;

namespace Agency.Harness.Test.Skills;

/// <summary>
/// Tests for <see cref="SkillRenderer"/>.
/// </summary>
public sealed class SkillRendererTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static Skill MakeSkill(
        string body,
        string skillDir = "/skills/my-skill",
        IReadOnlyList<string>? arguments = null) =>
        new()
        {
            Name = "my-skill",
            Description = "A test skill",
            Body = body,
            SkillDir = skillDir,
            Arguments = arguments ?? [],
        };

    private static string Render(Skill skill, string? arguments = null, string sessionId = "session-123") =>
        SkillRenderer.Render(skill, arguments, sessionId);

    // ---------------------------------------------------------------------------
    // $ARGUMENTS — full argument string
    // ---------------------------------------------------------------------------

    [Fact]
    public void Render_ArgumentsPlaceholder_ReplacedWithFullArgumentString()
    {
        Skill skill = MakeSkill("Do this: $ARGUMENTS");

        string result = Render(skill, "hello world");

        Assert.Equal("Do this: hello world", result);
    }

    [Fact]
    public void Render_ArgumentsPlaceholder_NullArguments_ReplacedWithEmpty()
    {
        Skill skill = MakeSkill("Do this: $ARGUMENTS");

        string result = Render(skill, null);

        Assert.Equal("Do this: ", result);
    }

    // ---------------------------------------------------------------------------
    // $ARGUMENTS[N] — indexed positional (1-based)
    // ---------------------------------------------------------------------------

    [Fact]
    public void Render_ArgumentsIndexed_FirstToken()
    {
        Skill skill = MakeSkill("First: $ARGUMENTS[1]");

        string result = Render(skill, "alpha beta gamma");

        Assert.Equal("First: alpha", result);
    }

    [Fact]
    public void Render_ArgumentsIndexed_SecondToken()
    {
        Skill skill = MakeSkill("Second: $ARGUMENTS[2]");

        string result = Render(skill, "alpha beta gamma");

        Assert.Equal("Second: beta", result);
    }

    [Fact]
    public void Render_ArgumentsIndexed_Zero_ReturnsFull()
    {
        Skill skill = MakeSkill("All: $ARGUMENTS[0]");

        string result = Render(skill, "alpha beta");

        Assert.Equal("All: alpha beta", result);
    }

    [Fact]
    public void Render_ArgumentsIndexed_OutOfRange_ReplacedWithEmpty()
    {
        Skill skill = MakeSkill("Missing: $ARGUMENTS[9]");

        string result = Render(skill, "alpha");

        Assert.Equal("Missing: ", result);
    }

    // ---------------------------------------------------------------------------
    // $N — numeric shorthand (1-based, same semantics as $ARGUMENTS[N])
    // ---------------------------------------------------------------------------

    [Fact]
    public void Render_NumericShorthand_FirstToken()
    {
        Skill skill = MakeSkill("Token: $1");

        string result = Render(skill, "hello world");

        Assert.Equal("Token: hello", result);
    }

    [Fact]
    public void Render_NumericShorthand_SecondToken()
    {
        Skill skill = MakeSkill("Token: $2");

        string result = Render(skill, "hello world");

        Assert.Equal("Token: world", result);
    }

    [Fact]
    public void Render_NumericShorthand_OutOfRange_ReplacedWithEmpty()
    {
        Skill skill = MakeSkill("Token: $5");

        string result = Render(skill, "only one");

        Assert.Equal("Token: ", result);
    }

    // ---------------------------------------------------------------------------
    // $name — named argument substitution from skill.Arguments list
    // ---------------------------------------------------------------------------

    [Fact]
    public void Render_NamedArgument_MapsFirstNameToFirstToken()
    {
        Skill skill = MakeSkill("Target: $target", arguments: ["target", "source"]);

        string result = Render(skill, "prod staging");

        Assert.Equal("Target: prod", result);
    }

    [Fact]
    public void Render_NamedArgument_MapsSecondNameToSecondToken()
    {
        Skill skill = MakeSkill("Source: $source", arguments: ["target", "source"]);

        string result = Render(skill, "prod staging");

        Assert.Equal("Source: staging", result);
    }

    [Fact]
    public void Render_NamedArgument_MultipleNamedArgs_AllSubstituted()
    {
        Skill skill = MakeSkill("Deploy $app to $env", arguments: ["app", "env"]);

        string result = Render(skill, "my-service production");

        Assert.Equal("Deploy my-service to production", result);
    }

    [Fact]
    public void Render_NamedArgument_UnknownName_LeftAsIs()
    {
        Skill skill = MakeSkill("Value: $unknown");

        string result = Render(skill, "something");

        Assert.Equal("Value: $unknown", result);
    }

    // ---------------------------------------------------------------------------
    // ${CLAUDE_SKILL_DIR} — skill directory path
    // ---------------------------------------------------------------------------

    [Fact]
    public void Render_SkillDir_IsSubstituted()
    {
        Skill skill = MakeSkill("Dir: ${CLAUDE_SKILL_DIR}", skillDir: "/absolute/path/to/skill");

        string result = Render(skill);

        Assert.Equal("Dir: /absolute/path/to/skill", result);
    }

    // ---------------------------------------------------------------------------
    // ${CLAUDE_SESSION_ID} — session identifier
    // ---------------------------------------------------------------------------

    [Fact]
    public void Render_SessionId_IsSubstituted()
    {
        Skill skill = MakeSkill("Session: ${CLAUDE_SESSION_ID}");

        string result = Render(skill, sessionId: "abc-123-xyz");

        Assert.Equal("Session: abc-123-xyz", result);
    }

    // ---------------------------------------------------------------------------
    // Backslash-escaping of $
    // ---------------------------------------------------------------------------

    [Fact]
    public void Render_EscapedDollar_RendersAsLiteralDollar()
    {
        Skill skill = MakeSkill(@"Cost: \$100");

        string result = Render(skill, null);

        Assert.Equal("Cost: $100", result);
    }

    [Fact]
    public void Render_EscapedDollarBeforeArguments_NotSubstituted()
    {
        Skill skill = MakeSkill(@"Literal: \$ARGUMENTS");

        string result = Render(skill, null);

        Assert.Equal("Literal: $ARGUMENTS", result);
    }

    [Fact]
    public void Render_EscapedDollarBeforeArguments_WithArgs_AppendsOnlyFallback()
    {
        // \$ARGUMENTS is an escaped dollar — it is NOT a placeholder, so the
        // fallback ARGUMENTS: append fires when args are supplied.
        Skill skill = MakeSkill(@"Cost: \$ARGUMENTS");

        string result = Render(skill, "value");

        Assert.StartsWith("Cost: $ARGUMENTS", result);
        Assert.Contains("ARGUMENTS: value", result);
    }

    // ---------------------------------------------------------------------------
    // Quoted multi-word indexed args — shell-style tokenization
    // ---------------------------------------------------------------------------

    [Fact]
    public void Render_DoubleQuotedMultiWord_TreatedAsSingleToken()
    {
        Skill skill = MakeSkill("First: $1, Second: $2");

        string result = Render(skill, "\"foo bar\" baz");

        Assert.Equal("First: foo bar, Second: baz", result);
    }

    [Fact]
    public void Render_SingleQuotedMultiWord_TreatedAsSingleToken()
    {
        Skill skill = MakeSkill("First: $1, Second: $2");

        string result = Render(skill, "'hello world' next");

        Assert.Equal("First: hello world, Second: next", result);
    }

    [Fact]
    public void Render_QuotedArgViaArgumentsIndexed_WorksCorrectly()
    {
        Skill skill = MakeSkill("Token: $ARGUMENTS[1]");

        string result = Render(skill, "\"multi word\" second");

        Assert.Equal("Token: multi word", result);
    }

    // ---------------------------------------------------------------------------
    // No-placeholder fallback — append "ARGUMENTS: <value>"
    // ---------------------------------------------------------------------------

    [Fact]
    public void Render_NoArgumentsPlaceholder_WithArguments_AppendsLine()
    {
        Skill skill = MakeSkill("Do the thing.");

        string result = Render(skill, "extra args");

        Assert.Contains("Do the thing.", result);
        Assert.Contains("ARGUMENTS: extra args", result);
    }

    [Fact]
    public void Render_NoArgumentsPlaceholder_EmptyArguments_DoesNotAppend()
    {
        Skill skill = MakeSkill("Do the thing.");

        string result = Render(skill, null);

        Assert.Equal("Do the thing.", result);
        Assert.DoesNotContain("ARGUMENTS:", result);
    }

    [Fact]
    public void Render_HasArgumentsPlaceholder_WithArguments_DoesNotAlsoAppend()
    {
        Skill skill = MakeSkill("Args: $ARGUMENTS. Done.");

        string result = Render(skill, "some value");

        Assert.Equal("Args: some value. Done.", result);
        Assert.DoesNotContain("ARGUMENTS:", result.Replace("Args: some value. Done.", ""));
    }

    // ---------------------------------------------------------------------------
    // Multiple placeholders in one body
    // ---------------------------------------------------------------------------

    [Fact]
    public void Render_MultiplePlaceholdersOfDifferentTypes_AllSubstituted()
    {
        Skill skill = MakeSkill(
            "Session: ${CLAUDE_SESSION_ID}\nDir: ${CLAUDE_SKILL_DIR}\nAll: $ARGUMENTS\nFirst: $1",
            skillDir: "/skills/demo",
            arguments: ["item"]);

        string result = Render(skill, "alpha beta", sessionId: "sid-42");

        Assert.Contains("Session: sid-42", result);
        Assert.Contains("Dir: /skills/demo", result);
        Assert.Contains("All: alpha beta", result);
        Assert.Contains("First: alpha", result);
    }

    // ---------------------------------------------------------------------------
    // Substituted output is not re-scanned
    // ---------------------------------------------------------------------------

    [Fact]
    public void Render_SubstitutedValueContainingDollarSign_NotReExpanded()
    {
        // arguments value itself contains "$ARGUMENTS" — must not be re-expanded.
        Skill skill = MakeSkill("Value: $ARGUMENTS");

        string result = Render(skill, "$ARGUMENTS");

        // Should be literal "$ARGUMENTS" from the substituted value, not re-expanded.
        Assert.Equal("Value: $ARGUMENTS", result);
    }
}
