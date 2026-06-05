
using Agency.Harness.Hooks.Configuration;

namespace Agency.Harness.Test.Hooks.Configuration;

/// <summary>
/// Verifies the three routing modes of <see cref="HookMatcher"/>:
/// MatchAll, ExactSet (pipe-delimited), and Regex.
/// </summary>
public sealed class HookMatcherTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    public void Matcher_Null_Empty_Star_MatchAll(string? input)
    {
        var matcher = HookMatcher.Create(input);
        Assert.True(matcher.IsMatch("Anything"));
        Assert.True(matcher.IsMatch(""));
    }

    [Fact]
    public void Matcher_ExactName_IsCaseInsensitive()
    {
        var matcher = HookMatcher.Create("Bash");
        Assert.True(matcher.IsMatch("bash"));
        Assert.True(matcher.IsMatch("BASH"));
        Assert.False(matcher.IsMatch("Bash2"));
    }

    [Fact]
    public void Matcher_PipeList_MatchesAnyMember()
    {
        var matcher = HookMatcher.Create("Bash|Edit");
        Assert.True(matcher.IsMatch("Bash"));
        Assert.True(matcher.IsMatch("Edit"));
        Assert.False(matcher.IsMatch("Write"));
    }

    [Fact]
    public void Matcher_Regex_MatchesAndRejects()
    {
        var matcher = HookMatcher.Create("^mcp__.*");
        Assert.True(matcher.IsMatch("mcp__memory__recall"));
        Assert.False(matcher.IsMatch("Bash"));
    }

    [Fact]
    public void Matcher_MalformedRegex_ThrowsAtCreate()
    {
        Assert.Throws<ArgumentException>(() => HookMatcher.Create("("));
    }

    [Fact]
    public void Matcher_PathologicalPattern_TimesOut_NoMatch()
    {
        // Catastrophic backtracking pattern
        var matcher = HookMatcher.Create("(a+)+$");
        string longInput = new string('a', 30) + "!";
        // Should return false (timeout treated as no-match), not throw
        bool result = matcher.IsMatch(longInput);
        Assert.False(result);
    }
}
