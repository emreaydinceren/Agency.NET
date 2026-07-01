using Agency.Harness.Permissions;

namespace Agency.Harness.Test.Permissions;

/// <summary>
/// Verifies parse, round-trip, and match semantics of <see cref="PermissionRule"/>:
/// bare rules, parameterized rules, malformed input rejection, glob/wildcard matching,
/// path normalization, case-insensitivity, and null key-value fail-safe.
/// </summary>
public sealed class PermissionRuleTests
{
    // ── Parse: bare name ─────────────────────────────────────────────────────

    /// <summary>
    /// Parsing a bare tool name (no parenthesized pattern) must set
    /// <see cref="PermissionRule.ToolPattern"/> to the name and leave
    /// <see cref="PermissionRule.InputPattern"/> <see langword="null"/>.
    /// </summary>
    [Fact]
    public void Parse_BareName_SetsToolPatternAndNullInputPattern()
    {
        PermissionRule rule = PermissionRule.Parse("ReadFile");

        Assert.Equal("ReadFile", rule.ToolPattern);
        Assert.Null(rule.InputPattern);
    }

    /// <summary>
    /// Parsing a bare tool name must preserve the original text verbatim in
    /// <see cref="PermissionRule.Raw"/>.
    /// </summary>
    [Fact]
    public void Parse_BareName_PreservesRaw()
    {
        PermissionRule rule = PermissionRule.Parse("ReadFile");

        Assert.Equal("ReadFile", rule.Raw);
    }

    // ── Parse: parameterized ─────────────────────────────────────────────────

    /// <summary>
    /// Parsing a parameterized rule must split it into a
    /// <see cref="PermissionRule.ToolPattern"/> (text before the parenthesis) and an
    /// <see cref="PermissionRule.InputPattern"/> (text inside the parenthesis).
    /// </summary>
    [Fact]
    public void Parse_Parameterized_SetsToolPatternAndInputPattern()
    {
        PermissionRule rule = PermissionRule.Parse("ExecutePowershell(git status*)");

        Assert.Equal("ExecutePowershell", rule.ToolPattern);
        Assert.Equal("git status*", rule.InputPattern);
    }

    /// <summary>
    /// Parsing a parameterized rule must preserve the original text verbatim in
    /// <see cref="PermissionRule.Raw"/>.
    /// </summary>
    [Fact]
    public void Parse_Parameterized_PreservesRaw()
    {
        PermissionRule rule = PermissionRule.Parse("ExecutePowershell(git status*)");

        Assert.Equal("ExecutePowershell(git status*)", rule.Raw);
    }

    // ── Parse: malformed inputs → FormatException ────────────────────────────

    /// <summary>
    /// <see cref="PermissionRule.Parse"/> must throw <see cref="FormatException"/> for a rule
    /// with an unclosed parenthesis (e.g. <c>Tool(</c>).
    /// </summary>
    [Fact]
    public void Parse_UnclosedParen_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => PermissionRule.Parse("Tool("));
    }

    /// <summary>
    /// <see cref="PermissionRule.Parse"/> must throw <see cref="FormatException"/> for an
    /// empty string.
    /// </summary>
    [Fact]
    public void Parse_EmptyString_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => PermissionRule.Parse(""));
    }

    /// <summary>
    /// <see cref="PermissionRule.Parse"/> must throw <see cref="FormatException"/> for a rule
    /// consisting of only parentheses with no tool name (e.g. <c>()</c>).
    /// </summary>
    [Fact]
    public void Parse_OnlyParens_NoToolName_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => PermissionRule.Parse("()"));
    }

    // ── TryParse: malformed inputs → false, no throw, null out ───────────────

    /// <summary>
    /// <see cref="PermissionRule.TryParse"/> must return <see langword="false"/> with a
    /// <see langword="null"/> rule for an unclosed parenthesis, without throwing.
    /// </summary>
    [Fact]
    public void TryParse_UnclosedParen_ReturnsFalseAndNullRule()
    {
        bool result = PermissionRule.TryParse("Tool(", out PermissionRule? rule);

        Assert.False(result);
        Assert.Null(rule);
    }

    /// <summary>
    /// <see cref="PermissionRule.TryParse"/> must return <see langword="false"/> with a
    /// <see langword="null"/> rule for an empty string, without throwing.
    /// </summary>
    [Fact]
    public void TryParse_EmptyString_ReturnsFalseAndNullRule()
    {
        bool result = PermissionRule.TryParse("", out PermissionRule? rule);

        Assert.False(result);
        Assert.Null(rule);
    }

    /// <summary>
    /// <see cref="PermissionRule.TryParse"/> must return <see langword="false"/> with a
    /// <see langword="null"/> rule for a parentheses-only input with no tool name, without
    /// throwing.
    /// </summary>
    [Fact]
    public void TryParse_OnlyParens_ReturnsFalseAndNullRule()
    {
        bool result = PermissionRule.TryParse("()", out PermissionRule? rule);

        Assert.False(result);
        Assert.Null(rule);
    }

    // ── TryParse: valid input → true ─────────────────────────────────────────

    /// <summary>
    /// <see cref="PermissionRule.TryParse"/> must return <see langword="true"/> and populate
    /// the out rule for a valid bare tool name.
    /// </summary>
    [Fact]
    public void TryParse_ValidBareName_ReturnsTrueWithRule()
    {
        bool result = PermissionRule.TryParse("ReadFile", out PermissionRule? rule);

        Assert.True(result);
        Assert.NotNull(rule);
        Assert.Equal("ReadFile", rule.ToolPattern);
    }

    // ── Match: command prefix ─────────────────────────────────────────────────

    /// <summary>
    /// A parameterized rule with a trailing wildcard (e.g. <c>ExecutePowershell(git status*)</c>)
    /// must match a key value sharing that prefix.
    /// </summary>
    [Fact]
    public void Matches_CommandPrefix_MatchesMatchingKeyValue()
    {
        PermissionRule rule = PermissionRule.Parse("ExecutePowershell(git status*)");

        Assert.True(rule.Matches("ExecutePowershell", "git status --short"));
    }

    /// <summary>
    /// A parameterized rule with a trailing wildcard must reject a key value that does not
    /// share the required prefix.
    /// </summary>
    [Fact]
    public void Matches_CommandPrefix_RejectsNonMatchingKeyValue()
    {
        PermissionRule rule = PermissionRule.Parse("ExecutePowershell(git status*)");

        Assert.False(rule.Matches("ExecutePowershell", "git stash"));
    }

    // ── Match: path glob + backslash normalization ────────────────────────────

    /// <summary>
    /// <see cref="PermissionRule.Matches"/> must normalize backslashes to forward slashes in
    /// the key value before matching, so a Windows-style path matches a forward-slash glob rule.
    /// </summary>
    [Fact]
    public void Matches_PathGlob_NormalizesBackslashesToForwardSlashes()
    {
        PermissionRule rule = PermissionRule.Parse(@"WriteFile(E:/secrets/**)");

        Assert.True(rule.Matches("WriteFile", @"E:\secrets\key.txt"));
    }

    /// <summary>
    /// Path glob matching in <see cref="PermissionRule.Matches"/> must be case-insensitive.
    /// </summary>
    [Fact]
    public void Matches_PathGlob_IsCaseInsensitive()
    {
        PermissionRule rule = PermissionRule.Parse(@"WriteFile(E:/secrets/**)");

        Assert.True(rule.Matches("WriteFile", @"e:\SECRETS\key.txt"));
    }

    // ── Match: ** is cosmetically identical to * ──────────────────────────────
    // Spec §4.1 and §13 risk 2: ** and * both match across path separators.

    /// <summary>
    /// Per spec §4.1, <c>**</c> is cosmetically identical to <c>*</c>: both must match a
    /// multi-segment path, including across path separators.
    /// </summary>
    [Fact]
    public void Matches_DoubleStarAndSingleStar_BothMatchMultiSegmentPath()
    {
        PermissionRule doubleStar = PermissionRule.Parse(@"WriteFile(E:/secrets/**)");
        PermissionRule singleStar = PermissionRule.Parse(@"WriteFile(E:/secrets/*)");

        string multiSegmentPath = @"E:\secrets\sub\key.txt";

        bool doubleStarResult = doubleStar.Matches("WriteFile", multiSegmentPath);
        bool singleStarResult = singleStar.Matches("WriteFile", multiSegmentPath);

        Assert.True(doubleStarResult, "** should match a multi-segment path");
        Assert.True(singleStarResult, "* should match a multi-segment path (** == * per spec §4.1)");
    }

    // ── Match: tool wildcard in ToolPattern ───────────────────────────────────

    /// <summary>
    /// A wildcard in the tool-name pattern (e.g. <c>mcp__gitea__list_*</c>) must match any tool
    /// name fitting that pattern.
    /// </summary>
    [Fact]
    public void Matches_ToolWildcard_MatchesToolNameFittingPattern()
    {
        PermissionRule rule = PermissionRule.Parse("mcp__gitea__list_*");

        Assert.True(rule.Matches("mcp__gitea__list_branches", null));
    }

    /// <summary>
    /// A wildcard in the tool-name pattern must reject tool names that do not fit the pattern.
    /// </summary>
    [Fact]
    public void Matches_ToolWildcard_RejectsToolNameNotFittingPattern()
    {
        PermissionRule rule = PermissionRule.Parse("mcp__gitea__list_*");

        Assert.False(rule.Matches("mcp__gitea__create_branch", null));
    }

    // ── Match: bare rule matches any input including null ─────────────────────

    /// <summary>
    /// A bare rule (no input pattern) must match even when the key value is
    /// <see langword="null"/>.
    /// </summary>
    [Fact]
    public void Matches_BareRule_MatchesNullKeyValue()
    {
        PermissionRule rule = PermissionRule.Parse("ReadFile");

        Assert.True(rule.Matches("ReadFile", null));
    }

    /// <summary>
    /// A bare rule (no input pattern) must match any non-null key value as well.
    /// </summary>
    [Fact]
    public void Matches_BareRule_MatchesNonNullKeyValue()
    {
        PermissionRule rule = PermissionRule.Parse("ReadFile");

        Assert.True(rule.Matches("ReadFile", "anything"));
    }

    // ── Match: parameterized rule never matches null key value (spec §4.3 step 3) ──

    /// <summary>
    /// Per spec §4.3 step 3, a parameterized rule must fail-safe to
    /// <see langword="false"/> when the key value is <see langword="null"/>, since a null
    /// value cannot satisfy any value pattern.
    /// </summary>
    [Fact]
    public void Matches_ParameterizedRule_NullKeyValue_ReturnsFalse()
    {
        PermissionRule rule = PermissionRule.Parse("ExecutePowershell(git status*)");

        Assert.False(rule.Matches("ExecutePowershell", null));
    }
}
