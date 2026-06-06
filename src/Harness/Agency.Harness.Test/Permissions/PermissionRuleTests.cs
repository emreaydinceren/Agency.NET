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

    [Fact]
    public void Parse_BareName_SetsToolPatternAndNullInputPattern()
    {
        PermissionRule rule = PermissionRule.Parse("ReadFile");

        Assert.Equal("ReadFile", rule.ToolPattern);
        Assert.Null(rule.InputPattern);
    }

    [Fact]
    public void Parse_BareName_PreservesRaw()
    {
        PermissionRule rule = PermissionRule.Parse("ReadFile");

        Assert.Equal("ReadFile", rule.Raw);
    }

    // ── Parse: parameterized ─────────────────────────────────────────────────

    [Fact]
    public void Parse_Parameterized_SetsToolPatternAndInputPattern()
    {
        PermissionRule rule = PermissionRule.Parse("ExecutePowershell(git status*)");

        Assert.Equal("ExecutePowershell", rule.ToolPattern);
        Assert.Equal("git status*", rule.InputPattern);
    }

    [Fact]
    public void Parse_Parameterized_PreservesRaw()
    {
        PermissionRule rule = PermissionRule.Parse("ExecutePowershell(git status*)");

        Assert.Equal("ExecutePowershell(git status*)", rule.Raw);
    }

    // ── Parse: malformed inputs → FormatException ────────────────────────────

    [Fact]
    public void Parse_UnclosedParen_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => PermissionRule.Parse("Tool("));
    }

    [Fact]
    public void Parse_EmptyString_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => PermissionRule.Parse(""));
    }

    [Fact]
    public void Parse_OnlyParens_NoToolName_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => PermissionRule.Parse("()"));
    }

    // ── TryParse: malformed inputs → false, no throw, null out ───────────────

    [Fact]
    public void TryParse_UnclosedParen_ReturnsFalseAndNullRule()
    {
        bool result = PermissionRule.TryParse("Tool(", out PermissionRule? rule);

        Assert.False(result);
        Assert.Null(rule);
    }

    [Fact]
    public void TryParse_EmptyString_ReturnsFalseAndNullRule()
    {
        bool result = PermissionRule.TryParse("", out PermissionRule? rule);

        Assert.False(result);
        Assert.Null(rule);
    }

    [Fact]
    public void TryParse_OnlyParens_ReturnsFalseAndNullRule()
    {
        bool result = PermissionRule.TryParse("()", out PermissionRule? rule);

        Assert.False(result);
        Assert.Null(rule);
    }

    // ── TryParse: valid input → true ─────────────────────────────────────────

    [Fact]
    public void TryParse_ValidBareName_ReturnsTrueWithRule()
    {
        bool result = PermissionRule.TryParse("ReadFile", out PermissionRule? rule);

        Assert.True(result);
        Assert.NotNull(rule);
        Assert.Equal("ReadFile", rule.ToolPattern);
    }

    // ── Match: command prefix ─────────────────────────────────────────────────

    [Fact]
    public void Matches_CommandPrefix_MatchesMatchingKeyValue()
    {
        PermissionRule rule = PermissionRule.Parse("ExecutePowershell(git status*)");

        Assert.True(rule.Matches("ExecutePowershell", "git status --short"));
    }

    [Fact]
    public void Matches_CommandPrefix_RejectsNonMatchingKeyValue()
    {
        PermissionRule rule = PermissionRule.Parse("ExecutePowershell(git status*)");

        Assert.False(rule.Matches("ExecutePowershell", "git stash"));
    }

    // ── Match: path glob + backslash normalization ────────────────────────────

    [Fact]
    public void Matches_PathGlob_NormalizesBackslashesToForwardSlashes()
    {
        PermissionRule rule = PermissionRule.Parse(@"WriteFile(E:/secrets/**)");

        Assert.True(rule.Matches("WriteFile", @"E:\secrets\key.txt"));
    }

    [Fact]
    public void Matches_PathGlob_IsCaseInsensitive()
    {
        PermissionRule rule = PermissionRule.Parse(@"WriteFile(E:/secrets/**)");

        Assert.True(rule.Matches("WriteFile", @"e:\SECRETS\key.txt"));
    }

    // ── Match: ** is cosmetically identical to * ──────────────────────────────
    // Spec §4.1 and §13 risk 2: ** and * both match across path separators.

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

    [Fact]
    public void Matches_ToolWildcard_MatchesToolNameFittingPattern()
    {
        PermissionRule rule = PermissionRule.Parse("mcp__gitea__list_*");

        Assert.True(rule.Matches("mcp__gitea__list_branches", null));
    }

    [Fact]
    public void Matches_ToolWildcard_RejectsToolNameNotFittingPattern()
    {
        PermissionRule rule = PermissionRule.Parse("mcp__gitea__list_*");

        Assert.False(rule.Matches("mcp__gitea__create_branch", null));
    }

    // ── Match: bare rule matches any input including null ─────────────────────

    [Fact]
    public void Matches_BareRule_MatchesNullKeyValue()
    {
        PermissionRule rule = PermissionRule.Parse("ReadFile");

        Assert.True(rule.Matches("ReadFile", null));
    }

    [Fact]
    public void Matches_BareRule_MatchesNonNullKeyValue()
    {
        PermissionRule rule = PermissionRule.Parse("ReadFile");

        Assert.True(rule.Matches("ReadFile", "anything"));
    }

    // ── Match: parameterized rule never matches null key value (spec §4.3 step 3) ──

    [Fact]
    public void Matches_ParameterizedRule_NullKeyValue_ReturnsFalse()
    {
        PermissionRule rule = PermissionRule.Parse("ExecutePowershell(git status*)");

        Assert.False(rule.Matches("ExecutePowershell", null));
    }
}
