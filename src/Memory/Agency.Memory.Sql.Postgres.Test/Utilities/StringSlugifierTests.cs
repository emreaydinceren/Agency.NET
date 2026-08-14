using System.Text.RegularExpressions;
using Agency.Memory.Sql.Postgres.Utilities;

namespace Agency.Memory.Sql.Postgres.Test.Utilities;

/// <summary>Tests for <see cref="StringSlugifier.Slugify"/>.</summary>
public sealed partial class StringSlugifierTests
{
    /// <summary>Verifies the documented examples from the design spec produce the expected slug.</summary>
    [Theory]
    [InlineData("Python 3.10+ async startup", "python-310-async-startup")]
    [InlineData("Race   condition", "race-condition")]
    [InlineData("  leading spaces  ", "leading-spaces")]
    [InlineData("UPPERCASE", "uppercase")]
    [InlineData("PostgreSQL connection pool", "postgresql-connection-pool")]
    public void Slugify_ProducesCorrectSlug(string input, string expected)
    {
        string result = StringSlugifier.Slugify(input);
        Assert.Equal(expected, result);
    }

    /// <summary>Verifies that the same input always produces the same slug.</summary>
    [Fact]
    public void Slugify_IsDeterministic()
    {
        const string input = "Test String 123";
        string result1 = StringSlugifier.Slugify(input);
        string result2 = StringSlugifier.Slugify(input);
        Assert.Equal(result1, result2);
    }

    /// <summary>Verifies that an empty string throws.</summary>
    [Fact]
    public void Slugify_EmptyString_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => StringSlugifier.Slugify(""));
    }

    /// <summary>Verifies that a null string throws <see cref="ArgumentException"/> (the implementation uses a manual <c>string.IsNullOrWhiteSpace</c> guard, not <c>ArgumentNullException.ThrowIfNull</c>, so null and whitespace share the same exception type).</summary>
    [Fact]
    public void Slugify_NullString_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => StringSlugifier.Slugify(null!));
    }

    /// <summary>Verifies that a whitespace-only string throws.</summary>
    [Fact]
    public void Slugify_OnlyWhitespace_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => StringSlugifier.Slugify("   "));
    }

    /// <summary>Verifies that special characters are stripped from the output.</summary>
    [Fact]
    public void Slugify_SpecialCharacters_AreStripped()
    {
        string result = StringSlugifier.Slugify("Hello@World#123!");
        Assert.Equal("helloworld123", result);
        Assert.DoesNotContain("@", result);
        Assert.DoesNotContain("#", result);
        Assert.DoesNotContain("!", result);
    }

    /// <summary>Verifies that consecutive hyphens (from literal hyphens plus whitespace runs) are deduplicated to one.</summary>
    [Fact]
    public void Slugify_ConsecutiveHyphens_AreDeduped()
    {
        string result = StringSlugifier.Slugify("Hello  --  World");
        Assert.Equal("hello-world", result);
        Assert.DoesNotContain("--", result);
    }

    /// <summary>Verifies that underscores are treated the same as whitespace (collapsed to a single hyphen).</summary>
    [Fact]
    public void Slugify_Underscores_AreTreatedAsWhitespace()
    {
        string result = StringSlugifier.Slugify("snake_case_name");
        Assert.Equal("snake-case-name", result);
    }

    /// <summary>Verifies that non-ASCII (accented) characters are stripped, since the slug charset is <c>[a-z0-9-]</c> only.</summary>
    [Fact]
    public void Slugify_UnicodeAccentedCharacters_AreStripped()
    {
        string result = StringSlugifier.Slugify("café");
        Assert.Equal("caf", result);
    }

    /// <summary>Verifies that emoji (surrogate-pair code units) are stripped without leaving broken/duplicate hyphens behind.</summary>
    [Fact]
    public void Slugify_Emoji_AreStrippedAndHyphensStayDeduped()
    {
        string result = StringSlugifier.Slugify("Hello 👍 World");
        Assert.Equal("hello-world", result);
    }

    /// <summary>Verifies that an input consisting entirely of characters outside the slug charset (but not itself whitespace) produces an empty string rather than throwing.</summary>
    [Fact]
    public void Slugify_AllNonSlugCharacters_ReturnsEmptyString()
    {
        string result = StringSlugifier.Slugify("!!!@@@###");
        Assert.Equal(string.Empty, result);
    }

    /// <summary>Verifies that a very long input does not throw and produces a slug confined to the <c>[a-z0-9-]</c> charset.</summary>
    [Fact]
    public void Slugify_VeryLongString_ProducesValidSlug()
    {
        string longInput = string.Concat(Enumerable.Repeat("Long Title Segment! ", 500));

        string result = StringSlugifier.Slugify(longInput);

        Assert.NotEmpty(result);
        Assert.Matches(ValidSlugCharsetRegex(), result);
        Assert.DoesNotContain("--", result);
    }

    /// <summary>Verifies leading and trailing hyphens produced by punctuation at the string boundary are trimmed.</summary>
    [Fact]
    public void Slugify_PunctuationAtBoundaries_TrimsResultingHyphens()
    {
        string result = StringSlugifier.Slugify("...Hello World...");
        Assert.Equal("hello-world", result);
    }

    [GeneratedRegex("^[a-z0-9-]*$")]
    private static partial Regex ValidSlugCharsetRegex();
}
