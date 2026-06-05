using System.Text.RegularExpressions;

namespace Agency.Harness.Hooks.Configuration;

internal sealed class HookMatcher
{
    private enum Mode { MatchAll, ExactSet, Regex }

    private readonly Mode _mode;
    private readonly HashSet<string>? _names;
    private readonly Regex? _regex;

    private HookMatcher(Mode mode, HashSet<string>? names = null, Regex? regex = null)
    {
        _mode = mode;
        _names = names;
        _regex = regex;
    }

    internal static HookMatcher Create(string? matcher)
    {
        if (string.IsNullOrEmpty(matcher) || matcher == "*")
        {
            return new HookMatcher(Mode.MatchAll);
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(matcher, "^[A-Za-z0-9_|]+$"))
        {
            var names = new HashSet<string>(
                matcher.Split('|'),
                StringComparer.OrdinalIgnoreCase);
            return new HookMatcher(Mode.ExactSet, names: names);
        }

        // Regex mode — fail fast on invalid patterns
        Regex compiled;
        try
        {
            compiled = new Regex(
                matcher,
                RegexOptions.Compiled | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250));
        }
        catch (RegexParseException ex)
        {
            throw new ArgumentException($"Invalid hook matcher pattern: {ex.Message}", nameof(matcher), ex);
        }

        return new HookMatcher(Mode.Regex, regex: compiled);
    }

    internal bool IsMatch(string candidate)
    {
        return _mode switch
        {
            Mode.MatchAll => true,
            Mode.ExactSet => _names!.Contains(candidate),
            Mode.Regex    => RegexMatch(candidate),
            _             => false
        };
    }

    private bool RegexMatch(string candidate)
    {
        try
        {
            return _regex!.IsMatch(candidate);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
