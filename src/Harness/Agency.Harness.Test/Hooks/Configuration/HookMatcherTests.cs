
using Agency.Harness.Hooks.Configuration;

namespace Agency.Harness.Test.Hooks.Configuration;

/// <summary>
/// Verifies the three routing modes of <see cref="HookMatcher"/>:
/// MatchAll, ExactSet (pipe-delimited), and Regex.
/// </summary>
public sealed class HookMatcherTests
{
    /// <summary>A matcher created from <see langword="null"/>, an empty string, or <c>"*"</c> matches any tool name, including the empty string.</summary>
    /// <param name="input">The matcher pattern to test; one of <see langword="null"/>, empty, or <c>"*"</c>.</param>
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

    /// <summary>A matcher created from a single exact tool name matches regardless of casing, but not a longer name that merely starts with it.</summary>
    [Fact]
    public void Matcher_ExactName_IsCaseInsensitive()
    {
        var matcher = HookMatcher.Create("Bash");
        Assert.True(matcher.IsMatch("bash"));
        Assert.True(matcher.IsMatch("BASH"));
        Assert.False(matcher.IsMatch("Bash2"));
    }

    /// <summary>A pipe-delimited matcher pattern matches any of its listed tool names and rejects names outside the list.</summary>
    [Fact]
    public void Matcher_PipeList_MatchesAnyMember()
    {
        var matcher = HookMatcher.Create("Bash|Edit");
        Assert.True(matcher.IsMatch("Bash"));
        Assert.True(matcher.IsMatch("Edit"));
        Assert.False(matcher.IsMatch("Write"));
    }

    /// <summary>A pattern that isn't a bare name or pipe-list is treated as a regular expression and matches/rejects accordingly.</summary>
    [Fact]
    public void Matcher_Regex_MatchesAndRejects()
    {
        var matcher = HookMatcher.Create("^mcp__.*");
        Assert.True(matcher.IsMatch("mcp__memory__recall"));
        Assert.False(matcher.IsMatch("Bash"));
    }

    /// <summary>An invalid regex pattern throws <see cref="ArgumentException"/> eagerly at <c>HookMatcher.Create</c> rather than later at match time.</summary>
    [Fact]
    public void Matcher_MalformedRegex_ThrowsAtCreate()
    {
        Assert.Throws<ArgumentException>(() => HookMatcher.Create("("));
    }

    /// <summary>A regex pattern prone to catastrophic backtracking times out and is treated as a non-match instead of throwing or hanging.</summary>
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
