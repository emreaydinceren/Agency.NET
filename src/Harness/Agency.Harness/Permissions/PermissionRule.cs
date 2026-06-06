using System.Text.RegularExpressions;

namespace Agency.Harness.Permissions;

/// <summary>
/// Represents a single parsed and compiled permission rule: either a bare tool pattern
/// (<c>ReadFile</c>) or a parameterized pattern (<c>ExecutePowershell(git status*)</c>).
/// Patterns may contain <c>*</c> wildcards; <c>**</c> is cosmetically identical to <c>*</c>
/// (both collapse to <c>.*</c>) per spec §4.1. Matching is OrdinalIgnoreCase; input key
/// values are normalized <c>\</c>→<c>/</c> before comparison.
/// </summary>
internal sealed class PermissionRule
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private readonly Regex _toolRegex;
    private readonly Regex? _inputRegex;

    private PermissionRule(string raw, string toolPattern, string? inputPattern, Regex toolRegex, Regex? inputRegex)
    {
        this.Raw = raw;
        this.ToolPattern = toolPattern;
        this.InputPattern = inputPattern;
        _toolRegex = toolRegex;
        _inputRegex = inputRegex;
    }

    /// <summary>The original config string, used verbatim in deny reasons.</summary>
    internal string Raw { get; }

    /// <summary>The tool-name pattern extracted from the rule; may contain <c>*</c>.</summary>
    internal string ToolPattern { get; }

    /// <summary>The input-key pattern; <see langword="null"/> for bare rules (matches any input).</summary>
    internal string? InputPattern { get; }

    /// <summary>
    /// Parses <paramref name="text"/> into a <see cref="PermissionRule"/>.
    /// Patterns are compiled to anchored regexes at parse time — matching is O(1) per call.
    /// </summary>
    /// <exception cref="FormatException">
    /// Thrown when <paramref name="text"/> is empty/whitespace, has no tool name (e.g. <c>()</c>),
    /// or has an unclosed parenthesis (e.g. <c>Tool(</c>).
    /// </exception>
    internal static PermissionRule Parse(string text)
    {
        if (!TryParseCore(text, out PermissionRule? rule, out string? error))
        {
            throw new FormatException($"Invalid permission rule '{text}': {error}");
        }

        return rule!;
    }

    /// <summary>
    /// Attempts to parse <paramref name="text"/> into a <see cref="PermissionRule"/>.
    /// Returns <see langword="false"/> and sets <paramref name="rule"/> to <see langword="null"/>
    /// on any malformed input; never throws.
    /// </summary>
    internal static bool TryParse(string text, out PermissionRule? rule)
    {
        return TryParseCore(text, out rule, out _);
    }

    /// <summary>
    /// Returns <see langword="true"/> when this rule matches <paramref name="toolName"/>
    /// and <paramref name="keyValue"/>:
    /// <list type="bullet">
    ///   <item>Tool pattern must match <paramref name="toolName"/> (OrdinalIgnoreCase).</item>
    ///   <item>Bare rules match any <paramref name="keyValue"/>, including <see langword="null"/>.</item>
    ///   <item>Parameterized rules never match when <paramref name="keyValue"/> is <see langword="null"/> (fail-safe, spec §4.3 step 3).</item>
    ///   <item>Before input matching, <c>\</c> is normalized to <c>/</c> in <paramref name="keyValue"/>.</item>
    /// </list>
    /// </summary>
    internal bool Matches(string toolName, string? keyValue)
    {
        if (!RegexMatch(_toolRegex, toolName))
        {
            return false;
        }

        if (_inputRegex is null)
        {
            // Bare rule — matches any input including null key value.
            return true;
        }

        if (keyValue is null)
        {
            // Parameterized rule never matches a null key value (fail-safe).
            return false;
        }

        string normalized = keyValue.Replace('\\', '/');
        return RegexMatch(_inputRegex, normalized);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static bool TryParseCore(string text, out PermissionRule? rule, out string? error)
    {
        rule = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "rule text must not be empty or whitespace";
            return false;
        }

        int parenIndex = text.IndexOf('(');

        if (parenIndex < 0)
        {
            // Bare rule — no parentheses at all.
            string toolPattern = text;
            Regex toolRegex = CompilePattern(toolPattern);
            rule = new PermissionRule(text, toolPattern, inputPattern: null, toolRegex, inputRegex: null);
            error = null;
            return true;
        }

        // Parameterized rule: must end with ')' and have a non-empty tool name before '('.
        if (parenIndex == 0)
        {
            error = "tool name must not be empty";
            return false;
        }

        if (!text.EndsWith(')'))
        {
            error = "unclosed parenthesis — rule must end with ')'";
            return false;
        }

        string parsedToolPattern = text[..parenIndex];
        // Everything between '(' and the final ')'.
        string parsedInputPattern = text[(parenIndex + 1)..^1];

        Regex parsedToolRegex = CompilePattern(parsedToolPattern);
        Regex parsedInputRegex = CompilePattern(parsedInputPattern);

        rule = new PermissionRule(text, parsedToolPattern, parsedInputPattern, parsedToolRegex, parsedInputRegex);
        error = null;
        return true;
    }

    /// <summary>
    /// Translates a glob pattern (may contain <c>*</c> and <c>**</c>) to an anchored, compiled,
    /// case-insensitive <see cref="Regex"/> with a 250 ms match timeout.
    /// One or more consecutive <c>*</c> (escaped as <c>\*</c> by <see cref="Regex.Escape"/>) are
    /// collapsed to a single <c>.*</c>, so <c>**</c> behaves identically to <c>*</c>.
    /// </summary>
    private static Regex CompilePattern(string pattern)
    {
        // Escape all regex metacharacters, then replace runs of escaped '*' with '.*'.
        string escaped = Regex.Escape(pattern);
        // Regex.Escape converts '*' → '\*'. Replace one-or-more consecutive '\*' with '.*'.
        string regexBody = Regex.Replace(escaped, @"(\\\*)+", ".*");
        string anchored = $"^{regexBody}$";

        return new Regex(
            anchored,
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            MatchTimeout);
    }

    private static bool RegexMatch(Regex regex, string candidate)
    {
        try
        {
            return regex.IsMatch(candidate);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
