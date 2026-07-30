using System.Text.RegularExpressions;

namespace Agency.VectorStore.Common;

/// <summary>
/// Validates and canonicalises project names (Spec §6.1, §8.1).
/// </summary>
public static partial class ProjectName
{
    /// <summary>
    /// The maximum allowed length, in characters, of a project name after trimming.
    /// </summary>
    public const int MaxLength = 64;

    /// <summary>
    /// Attempts to validate and canonicalise <paramref name="raw"/>.
    /// </summary>
    public static bool TryNormalize(string? raw, out string canonical, out string? error)
    {
        canonical = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Project name is required.";
            return false;
        }

        string trimmed = raw.Trim();

        if (trimmed.Length < 1 || trimmed.Length > MaxLength)
        {
            error = $"Project name must be 1-{MaxLength} characters.";
            return false;
        }

        if (trimmed == "*")
        {
            error = "'*' is reserved for the global scope.";
            return false;
        }

        if (!ValidCharactersRegex().IsMatch(trimmed))
        {
            error = "Project name may only contain letters, digits, '.', '_' or '-', and must start with a letter or digit.";
            return false;
        }

        canonical = trimmed.ToLowerInvariant();
        error = null;
        return true;
    }

    /// <summary>
    /// Validates and canonicalises <paramref name="raw"/>, throwing <see cref="ArgumentException"/> on failure.
    /// </summary>
    public static string EnsureValid(string? raw)
    {
        if (!TryNormalize(raw, out string canonical, out string? error))
        {
            throw new ArgumentException(error, nameof(raw));
        }

        return canonical;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex ValidCharactersRegex();
}
