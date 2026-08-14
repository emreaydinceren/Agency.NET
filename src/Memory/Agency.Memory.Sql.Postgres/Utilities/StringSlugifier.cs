using System.Text.RegularExpressions;

namespace Agency.Memory.Sql.Postgres.Utilities;

/// <summary>URL-safe slug generation for memory keys derived from agent-provided titles.</summary>
public static partial class StringSlugifier
{
    /// <summary>
    /// Converts a natural-language string into a lowercase, hyphen-separated slug: whitespace and
    /// underscore runs become a single hyphen, remaining non-alphanumeric characters are stripped,
    /// consecutive hyphens are deduplicated, and leading/trailing hyphens are trimmed.
    /// </summary>
    /// <param name="input">The string to slugify.</param>
    /// <returns>The deterministic, URL-safe slug.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="input"/> is null, empty, or whitespace.</exception>
    public static string Slugify(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("Input cannot be null or whitespace.", nameof(input));
        }

        string lowered = input.ToLowerInvariant();
        string hyphenated = WhitespaceOrUnderscoreRegex().Replace(lowered, "-");
        string stripped = NonSlugCharacterRegex().Replace(hyphenated, string.Empty);
        string deduped = ConsecutiveHyphensRegex().Replace(stripped, "-");
        return deduped.Trim('-');
    }

    [GeneratedRegex(@"[\s_]+")]
    private static partial Regex WhitespaceOrUnderscoreRegex();

    [GeneratedRegex("[^a-z0-9-]")]
    private static partial Regex NonSlugCharacterRegex();

    [GeneratedRegex("-{2,}")]
    private static partial Regex ConsecutiveHyphensRegex();
}
