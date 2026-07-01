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

    /// <summary><c>$ARGUMENTS</c> is replaced with the full, unmodified argument string.</summary>
    [Fact]
    public void Render_ArgumentsPlaceholder_ReplacedWithFullArgumentString()
    {
        Skill skill = MakeSkill("Do this: $ARGUMENTS");

        string result = Render(skill, "hello world");

        Assert.Equal("Do this: hello world", result);
    }

    /// <summary><c>$ARGUMENTS</c> is replaced with an empty string when no arguments are supplied.</summary>
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

    /// <summary><c>$ARGUMENTS[1]</c> resolves to the first whitespace-separated token of the argument string.</summary>
    [Fact]
    public void Render_ArgumentsIndexed_FirstToken()
    {
        Skill skill = MakeSkill("First: $ARGUMENTS[1]");

        string result = Render(skill, "alpha beta gamma");

        Assert.Equal("First: alpha", result);
    }

    /// <summary><c>$ARGUMENTS[2]</c> resolves to the second whitespace-separated token of the argument string.</summary>
    [Fact]
    public void Render_ArgumentsIndexed_SecondToken()
    {
        Skill skill = MakeSkill("Second: $ARGUMENTS[2]");

        string result = Render(skill, "alpha beta gamma");

        Assert.Equal("Second: beta", result);
    }

    /// <summary><c>$ARGUMENTS[0]</c> resolves to the full, unmodified argument string.</summary>
    [Fact]
    public void Render_ArgumentsIndexed_Zero_ReturnsFull()
    {
        Skill skill = MakeSkill("All: $ARGUMENTS[0]");

        string result = Render(skill, "alpha beta");

        Assert.Equal("All: alpha beta", result);
    }

    /// <summary><c>$ARGUMENTS[N]</c> resolves to an empty string when <c>N</c> exceeds the number of supplied tokens.</summary>
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

    /// <summary><c>$1</c> is shorthand for <c>$ARGUMENTS[1]</c> and resolves to the first token.</summary>
    [Fact]
    public void Render_NumericShorthand_FirstToken()
    {
        Skill skill = MakeSkill("Token: $1");

        string result = Render(skill, "hello world");

        Assert.Equal("Token: hello", result);
    }

    /// <summary><c>$2</c> is shorthand for <c>$ARGUMENTS[2]</c> and resolves to the second token.</summary>
    [Fact]
    public void Render_NumericShorthand_SecondToken()
    {
        Skill skill = MakeSkill("Token: $2");

        string result = Render(skill, "hello world");

        Assert.Equal("Token: world", result);
    }

    /// <summary>Numeric shorthand <c>$N</c> resolves to an empty string when <c>N</c> exceeds the number of supplied tokens.</summary>
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

    /// <summary>A placeholder named after the first entry in <see cref="Skill.Arguments"/> resolves to the first supplied token.</summary>
    [Fact]
    public void Render_NamedArgument_MapsFirstNameToFirstToken()
    {
        Skill skill = MakeSkill("Target: $target", arguments: ["target", "source"]);

        string result = Render(skill, "prod staging");

        Assert.Equal("Target: prod", result);
    }

    /// <summary>A placeholder named after the second entry in <see cref="Skill.Arguments"/> resolves to the second supplied token.</summary>
    [Fact]
    public void Render_NamedArgument_MapsSecondNameToSecondToken()
    {
        Skill skill = MakeSkill("Source: $source", arguments: ["target", "source"]);

        string result = Render(skill, "prod staging");

        Assert.Equal("Source: staging", result);
    }

    /// <summary>Multiple named-argument placeholders in the same body are all substituted with their corresponding tokens.</summary>
    [Fact]
    public void Render_NamedArgument_MultipleNamedArgs_AllSubstituted()
    {
        Skill skill = MakeSkill("Deploy $app to $env", arguments: ["app", "env"]);

        string result = Render(skill, "my-service production");

        Assert.Equal("Deploy my-service to production", result);
    }

    /// <summary>A <c>$name</c> placeholder that does not match any entry in <see cref="Skill.Arguments"/> is left in the output unchanged.</summary>
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

    /// <summary><c>${CLAUDE_SKILL_DIR}</c> is replaced with the skill's <see cref="Skill.SkillDir"/> path.</summary>
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

    /// <summary><c>${CLAUDE_SESSION_ID}</c> is replaced with the session id passed to <c>Render</c>.</summary>
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

    /// <summary>A backslash-escaped dollar sign (<c>\$</c>) renders as a literal <c>$</c> rather than starting a placeholder.</summary>
    [Fact]
    public void Render_EscapedDollar_RendersAsLiteralDollar()
    {
        Skill skill = MakeSkill(@"Cost: \$100");

        string result = Render(skill, null);

        Assert.Equal("Cost: $100", result);
    }

    /// <summary>A backslash-escaped <c>\$ARGUMENTS</c> is treated as a literal string, not the <c>$ARGUMENTS</c> placeholder.</summary>
    [Fact]
    public void Render_EscapedDollarBeforeArguments_NotSubstituted()
    {
        Skill skill = MakeSkill(@"Literal: \$ARGUMENTS");

        string result = Render(skill, null);

        Assert.Equal("Literal: $ARGUMENTS", result);
    }

    /// <summary>When the only <c>$ARGUMENTS</c> occurrence in the body is escaped and arguments are supplied, the escaped text renders literally and the no-placeholder fallback line is still appended.</summary>
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

    /// <summary>A double-quoted, multi-word argument is tokenized as a single argument for indexed placeholder purposes.</summary>
    [Fact]
    public void Render_DoubleQuotedMultiWord_TreatedAsSingleToken()
    {
        Skill skill = MakeSkill("First: $1, Second: $2");

        string result = Render(skill, "\"foo bar\" baz");

        Assert.Equal("First: foo bar, Second: baz", result);
    }

    /// <summary>A single-quoted, multi-word argument is tokenized as a single argument for indexed placeholder purposes.</summary>
    [Fact]
    public void Render_SingleQuotedMultiWord_TreatedAsSingleToken()
    {
        Skill skill = MakeSkill("First: $1, Second: $2");

        string result = Render(skill, "'hello world' next");

        Assert.Equal("First: hello world, Second: next", result);
    }

    /// <summary>A quoted, multi-word argument is correctly resolved through the <c>$ARGUMENTS[N]</c> indexed placeholder syntax.</summary>
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

    /// <summary>When the body has no argument placeholder but arguments are supplied, an <c>ARGUMENTS: &lt;value&gt;</c> line is appended so the arguments are not silently dropped.</summary>
    [Fact]
    public void Render_NoArgumentsPlaceholder_WithArguments_AppendsLine()
    {
        Skill skill = MakeSkill("Do the thing.");

        string result = Render(skill, "extra args");

        Assert.Contains("Do the thing.", result);
        Assert.Contains("ARGUMENTS: extra args", result);
    }

    /// <summary>When the body has no argument placeholder and no arguments are supplied, no fallback <c>ARGUMENTS:</c> line is appended.</summary>
    [Fact]
    public void Render_NoArgumentsPlaceholder_EmptyArguments_DoesNotAppend()
    {
        Skill skill = MakeSkill("Do the thing.");

        string result = Render(skill, null);

        Assert.Equal("Do the thing.", result);
        Assert.DoesNotContain("ARGUMENTS:", result);
    }

    /// <summary>When the body already contains a <c>$ARGUMENTS</c> placeholder, arguments are substituted in place and the fallback <c>ARGUMENTS:</c> line is not additionally appended.</summary>
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

    /// <summary>A body containing session id, skill dir, full-arguments, and indexed placeholders in combination has all of them substituted correctly.</summary>
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

    /// <summary>When a substituted argument value itself contains placeholder-like text (e.g. <c>$ARGUMENTS</c>), the rendered output is not re-scanned or re-expanded.</summary>
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
